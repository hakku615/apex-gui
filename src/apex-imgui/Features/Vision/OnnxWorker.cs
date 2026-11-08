using System.Diagnostics;
using System.Threading;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

internal readonly struct OnnxInferenceSnapshot
{
    public readonly string Status;
    public readonly double AvgInferenceMs;

    public OnnxInferenceSnapshot(
        string status,
        double avgInferenceMs)
    {
        Status = status;
        AvgInferenceMs = avgInferenceMs;
    }
}

internal readonly struct OnnxDebugBox
{
    public readonly int InputWidth;
    public readonly int InputHeight;
    public readonly float X;
    public readonly float Y;
    public readonly float W;
    public readonly float H;
    public readonly float Score;

    public OnnxDebugBox(int inputWidth, int inputHeight, float x, float y, float w, float h, float score)
    {
        InputWidth = inputWidth;
        InputHeight = inputHeight;
        X = x;
        Y = y;
        W = w;
        H = h;
        Score = score;
    }
}

internal sealed class OnnxWorker : IDisposable
{
    private readonly object _sync = new();
    private readonly Thread _thread;
    private readonly AutoResetEvent _frameArrived = new(false);
    private readonly OnnxModelConfig _model;
    private readonly int _dmlDeviceId;

    private bool _running = true;
    private readonly byte[][] _frameBuffers = new[] { Array.Empty<byte>(), Array.Empty<byte>() };
    private int _pendingBufferIndex = -1;
    private int _processingBufferIndex = -1;
    private int _pendingFrameWidth;
    private int _pendingFrameHeight;
    private int _pendingFrameId;
    private int _lastProcessedFrameId;
    private IAimAssistDetectionSink? _detectionConsumer;
    private OnnxDebugBox[] _latestBoxes = Array.Empty<OnnxDebugBox>();
    private float[] _preprocessBuffer = Array.Empty<float>();
    private int[] _xMap = Array.Empty<int>();
    private int _xMapSrcW = -1;
    private int _xMapDstW = -1;

    // NMS scratch buffers, reused across frames: both parse paths and ApplyNms run on this
    // worker's single thread, so there is no cross-thread sharing to guard against.
    private readonly List<(float x, float y, float w, float h, float score, float obj, float classScore)> _rawCandidates = new();
    private readonly List<(float x, float y, float w, float h, float score)> _nmsCandidates = new();
    private readonly List<(float x, float y, float w, float h, float score)> _nmsKept = new();
    private OnnxDebugBox[] _nmsBoxBuffer = Array.Empty<OnnxDebugBox>();

    private readonly List<double> _windowSamples = new(256);
    private DateTime _windowStartUtc = DateTime.UtcNow;

    private OnnxInferenceSnapshot _snapshot = new(
        "Not started",
        0.0);

    public OnnxWorker(OnnxModelConfig model, int dmlDeviceId)
    {
        _model = model;
        _dmlDeviceId = dmlDeviceId;
        _thread = new Thread(WorkerMain)
        {
            IsBackground = true,
            Name = "ONNX-DML-Worker"
        };
        _thread.Start();
    }

    public void SubmitFrame(byte[] frameData, int width, int height, int frameId)
    {
        lock (_sync)
        {
            if (!_running)
            {
                return;
            }

            var targetBufferIndex = _processingBufferIndex == 0 ? 1 : 0;
            if (_frameBuffers[targetBufferIndex].Length != frameData.Length)
            {
                _frameBuffers[targetBufferIndex] = new byte[frameData.Length];
            }

            System.Buffer.BlockCopy(frameData, 0, _frameBuffers[targetBufferIndex], 0, frameData.Length);
            _pendingBufferIndex = targetBufferIndex;
            _pendingFrameWidth = width;
            _pendingFrameHeight = height;
            _pendingFrameId = frameId;
        }

        _frameArrived.Set();
    }

    public OnnxInferenceSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            return _snapshot;
        }
    }

    public OnnxDebugBox[] GetDebugBoxes()
    {
        lock (_sync)
        {
            var copy = new OnnxDebugBox[_latestBoxes.Length];
            Array.Copy(_latestBoxes, copy, _latestBoxes.Length);
            return copy;
        }
    }

    public void SetDetectionConsumer(IAimAssistDetectionSink? detectionConsumer)
    {
        lock (_sync)
        {
            _detectionConsumer = detectionConsumer;
        }
    }

    private void WorkerMain()
    {
        try
        {
            using var options = new SessionOptions();
            options.AppendExecutionProvider_DML(_dmlDeviceId);
            using var session = new InferenceSession(_model.OnnxPath, options);

            var input = session.InputMetadata.First();
            var inputName = input.Key;
            var inputDims = ResolveInputShape(input.Value.Dimensions, _model.InputHeight, _model.InputWidth);
            var layout = DetectLayout(inputDims);

            SetStatus("Running");
            while (_running)
            {
                _frameArrived.WaitOne(50);
                if (!_running)
                {
                    break;
                }

                byte[] frame;
                int frameWidth;
                int frameHeight;
                int frameId;
                int bufferIndex;
                lock (_sync)
                {
                    if (_pendingFrameId == 0 || _pendingFrameId == _lastProcessedFrameId || _pendingBufferIndex < 0)
                    {
                        continue;
                    }

                    bufferIndex = _pendingBufferIndex;
                    frame = _frameBuffers[bufferIndex];
                    frameWidth = _pendingFrameWidth;
                    frameHeight = _pendingFrameHeight;
                    frameId = _pendingFrameId;
                    _lastProcessedFrameId = frameId;
                    _processingBufferIndex = bufferIndex;
                }

                var cycleTimer = Stopwatch.StartNew();
                var inputData = Preprocess(frame, frameWidth, frameHeight, _model.InputWidth, _model.InputHeight, layout, ref _preprocessBuffer);
                var inputTensor = new DenseTensor<float>(inputData, inputDims);
                using var outputs = session.Run(new[] { NamedOnnxValue.CreateFromTensor(inputName, inputTensor) });
                var inferenceMs = cycleTimer.Elapsed.TotalMilliseconds;

                _ = CountDetections(
                    outputs,
                    _model.OutputFormat,
                    _model.InputWidth,
                    _model.InputHeight,
                    _model.ConfThreshold,
                    _model.IouThreshold,
                    _model.AllowedClasses,
                    out var boxes);

                IAimAssistDetectionSink? detectionConsumer;
                var detectionState = new SmartCoreDetectionState(boxes, DateTime.UtcNow);
                lock (_sync)
                {
                    _latestBoxes = boxes;
                    detectionConsumer = _detectionConsumer;
                }
                detectionConsumer?.SetAimAssistDetections(detectionState);

                PushInferenceSample(inferenceMs);

                lock (_sync)
                {
                    if (_processingBufferIndex == bufferIndex)
                    {
                        _processingBufferIndex = -1;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            SetStatus($"推理失败: {ex.GetType().Name}: {ex.Message}");
            _running = false;
            ClearDetectionsForConsumer();
        }
    }

    private void ClearDetectionsForConsumer()
    {
        IAimAssistDetectionSink? detectionConsumer;
        lock (_sync)
        {
            _latestBoxes = Array.Empty<OnnxDebugBox>();
            detectionConsumer = _detectionConsumer;
        }
        detectionConsumer?.SetAimAssistDetections(SmartCoreDetectionState.Empty);
    }

    private void SetStatus(string status)
    {
        lock (_sync)
        {
            _snapshot = new OnnxInferenceSnapshot(
                status,
                _snapshot.AvgInferenceMs);
        }
    }

    private void PushInferenceSample(double ms)
    {
        lock (_sync)
        {
            _windowSamples.Add(ms);
            var elapsed = DateTime.UtcNow - _windowStartUtc;
            if (elapsed.TotalSeconds >= 1.0)
            {
                var avg = _windowSamples.Count > 0 ? _windowSamples.Average() : 0.0;
                _snapshot = new OnnxInferenceSnapshot("Running", avg);
                _windowSamples.Clear();
                _windowStartUtc = DateTime.UtcNow;
            }
        }
    }

    private static int[] ResolveInputShape(IReadOnlyList<int> dims, int height, int width)
    {
        var resolved = dims.Select(d => d <= 0 ? 1 : d).ToArray();
        if (resolved.Length != 4)
        {
            return new[] { 1, 3, height, width };
        }

        if (resolved[1] == 3 || resolved[1] == 1)
        {
            resolved[0] = 1;
            resolved[2] = height;
            resolved[3] = width;
            return resolved;
        }

        resolved[0] = 1;
        resolved[1] = height;
        resolved[2] = width;
        resolved[3] = 3;
        return resolved;
    }

    private static string DetectLayout(int[] dims)
    {
        if (dims.Length == 4 && dims[1] is 1 or 3)
        {
            return "NCHW";
        }

        return "NHWC";
    }

    private float[] Preprocess(byte[] bgra, int srcW, int srcH, int dstW, int dstH, string layout, ref float[] buffer)
    {
        // 255 is a constant but a runtime float divide; the reciprocal multiply is exact
        // for every byte value in [0,255] at single precision.
        const float Inv255 = 1f / 255f;

        var requiredLength = dstW * dstH * 3;
        if (buffer.Length != requiredLength)
        {
            buffer = new float[requiredLength];
        }

        // The nearest-neighbour column map only changes when the region or model input
        // size changes, so keep it cached instead of doing an integer divide per pixel.
        if (_xMapSrcW != srcW || _xMapDstW != dstW || _xMap.Length != dstW)
        {
            _xMap = new int[dstW];
            for (var x = 0; x < dstW; x++)
            {
                _xMap[x] = x * srcW / dstW;
            }

            _xMapSrcW = srcW;
            _xMapDstW = dstW;
        }

        var xMap = _xMap;
        var data = buffer;
        var planeSize = dstW * dstH;

        if (layout == "NCHW")
        {
            for (var y = 0; y < dstH; y++)
            {
                var srcRow = (y * srcH / dstH) * srcW * 4;
                var pixel = y * dstW;
                for (var x = 0; x < dstW; x++, pixel++)
                {
                    var srcIndex = srcRow + xMap[x] * 4;
                    data[pixel] = bgra[srcIndex + 2] * Inv255;
                    data[planeSize + pixel] = bgra[srcIndex + 1] * Inv255;
                    data[2 * planeSize + pixel] = bgra[srcIndex] * Inv255;
                }
            }
        }
        else
        {
            for (var y = 0; y < dstH; y++)
            {
                var srcRow = (y * srcH / dstH) * srcW * 4;
                var pixel = y * dstW * 3;
                for (var x = 0; x < dstW; x++, pixel += 3)
                {
                    var srcIndex = srcRow + xMap[x] * 4;
                    data[pixel] = bgra[srcIndex + 2] * Inv255;
                    data[pixel + 1] = bgra[srcIndex + 1] * Inv255;
                    data[pixel + 2] = bgra[srcIndex] * Inv255;
                }
            }
        }

        return data;
    }

    private int CountDetections(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs,
        YoloOutputFormat outputFormat,
        int inputWidth,
        int inputHeight,
        float confThres,
        float iouThres,
        HashSet<int> allowedClasses,
        out OnnxDebugBox[] boxes)
    {
        boxes = Array.Empty<OnnxDebugBox>();
        foreach (var output in outputs)
        {
            Tensor<float>? tensor;
            try
            {
                tensor = output.AsTensor<float>();
            }
            catch
            {
                continue;
            }

            if (tensor.Rank != 3)
            {
                continue;
            }

            var dims = tensor.Dimensions.ToArray();
            var values = tensor.ToArray();
            if (TryParseDetections(
                    values,
                    dims,
                    outputFormat,
                    inputWidth,
                    inputHeight,
                    confThres,
                    iouThres,
                    allowedClasses,
                    out var count,
                    out boxes))
            {
                return count;
            }
        }

        return 0;
    }

    private bool TryParseDetections(
        float[] values,
        int[] dims,
        YoloOutputFormat outputFormat,
        int inputWidth,
        int inputHeight,
        float confThres,
        float iouThres,
        HashSet<int> allowedClasses,
        out int count,
        out OnnxDebugBox[] boxes)
    {
        return outputFormat switch
        {
            YoloOutputFormat.YoloV8 => TryParseYoloV8Detections(
                values,
                dims,
                inputWidth,
                inputHeight,
                confThres,
                iouThres,
                allowedClasses,
                out count,
                out boxes),
            _ => TryParseYoloV5Detections(
                values,
                dims,
                inputWidth,
                inputHeight,
                confThres,
                iouThres,
                allowedClasses,
                out count,
                out boxes)
        };
    }

    private bool TryParseYoloV5Detections(
        float[] values,
        int[] dims,
        int inputWidth,
        int inputHeight,
        float confThres,
        float iouThres,
        HashSet<int> allowedClasses,
        out int count,
        out OnnxDebugBox[] boxes)
    {
        count = 0;
        boxes = Array.Empty<OnnxDebugBox>();
        if (dims.Length != 3)
        {
            return false;
        }

        int rows;
        int cols;
        bool transposed;
        if (dims[2] >= 6)
        {
            rows = dims[1];
            cols = dims[2];
            transposed = false;
        }
        else if (dims[1] >= 6)
        {
            rows = dims[2];
            cols = dims[1];
            transposed = true;
        }
        else
        {
            return false;
        }

        var candidateBoxes = _rawCandidates;
        candidateBoxes.Clear();
        for (var i = 0; i < rows; i++)
        {
            int baseIndex;
            if (!transposed)
            {
                baseIndex = i * cols;
            }
            else
            {
                baseIndex = i;
            }

            float Read(int c) => !transposed ? values[baseIndex + c] : values[c * rows + baseIndex];

            var obj = Read(4);
            if (obj <= 0f)
            {
                continue;
            }

            var bestClassScore = 0f;
            var bestClass = -1;
            for (var c = 5; c < cols; c++)
            {
                var cls = Read(c);
                if (cls > bestClassScore)
                {
                    bestClassScore = cls;
                    bestClass = c - 5;
                }
            }

            if (bestClass >= 0 && allowedClasses.Count > 0 && !allowedClasses.Contains(bestClass))
            {
                continue;
            }

            var score = obj * bestClassScore;
            if (score < confThres)
            {
                continue;
            }

            candidateBoxes.Add((Read(0), Read(1), Math.Abs(Read(2)), Math.Abs(Read(3)), score, obj, bestClassScore));
        }

        var nmsCandidates = _nmsCandidates;
        nmsCandidates.Clear();
        foreach (var box in candidateBoxes)
        {
            nmsCandidates.Add((box.x, box.y, box.w, box.h, box.score));
        }

        return ApplyNms(nmsCandidates, iouThres, inputWidth, inputHeight, out count, out boxes);
    }

    private bool TryParseYoloV8Detections(
        float[] values,
        int[] dims,
        int inputWidth,
        int inputHeight,
        float confThres,
        float iouThres,
        HashSet<int> allowedClasses,
        out int count,
        out OnnxDebugBox[] boxes)
    {
        count = 0;
        boxes = Array.Empty<OnnxDebugBox>();
        if (!TryResolveOutputLayout(dims, minFeatures: 4, out var rows, out var cols, out var transposed))
        {
            return false;
        }

        var candidateBoxes = _nmsCandidates;
        candidateBoxes.Clear();
        for (var i = 0; i < rows; i++)
        {
            var baseIndex = transposed ? i : i * cols;
            float Read(int c) => transposed ? values[c * rows + baseIndex] : values[baseIndex + c];

            var bestClassScore = 0f;
            var bestClass = -1;
            for (var c = 4; c < cols; c++)
            {
                var cls = Read(c);
                if (cls > bestClassScore)
                {
                    bestClassScore = cls;
                    bestClass = c - 4;
                }
            }

            if (bestClassScore <= 0f)
            {
                continue;
            }

            if (bestClass >= 0 && allowedClasses.Count > 0 && !allowedClasses.Contains(bestClass))
            {
                continue;
            }

            if (bestClassScore < confThres)
            {
                continue;
            }

            candidateBoxes.Add((Read(0), Read(1), Math.Abs(Read(2)), Math.Abs(Read(3)), bestClassScore));
        }

        return ApplyNms(candidateBoxes, iouThres, inputWidth, inputHeight, out count, out boxes);
    }

    private static bool TryResolveOutputLayout(
        int[] dims,
        int minFeatures,
        out int rows,
        out int cols,
        out bool transposed)
    {
        rows = 0;
        cols = 0;
        transposed = false;
        if (dims.Length != 3)
        {
            return false;
        }

        if (dims[1] <= dims[2])
        {
            if (dims[1] < minFeatures)
            {
                return false;
            }

            transposed = true;
            rows = dims[2];
            cols = dims[1];
        }
        else
        {
            if (dims[2] < minFeatures)
            {
                return false;
            }

            rows = dims[1];
            cols = dims[2];
        }

        return rows > 0;
    }

    private bool ApplyNms(
        List<(float x, float y, float w, float h, float score)> candidateBoxes,
        float iouThres,
        int inputWidth,
        int inputHeight,
        out int count,
        out OnnxDebugBox[] boxes)
    {
        count = 0;
        boxes = Array.Empty<OnnxDebugBox>();
        if (candidateBoxes.Count == 0)
        {
            return true;
        }

        candidateBoxes.Sort((a, b) => b.score.CompareTo(a.score));
        var kept = _nmsKept;
        kept.Clear();
        foreach (var box in candidateBoxes)
        {
            var suppressed = false;
            foreach (var keptBox in kept)
            {
                if (ComputeIoU(
                        (box.x, box.y, box.w, box.h, box.score),
                        (keptBox.x, keptBox.y, keptBox.w, keptBox.h, keptBox.score)) > iouThres)
                {
                    suppressed = true;
                    break;
                }
            }

            if (!suppressed)
            {
                kept.Add(box);
            }
        }

        count = kept.Count;
        if (_nmsBoxBuffer.Length < count)
        {
            _nmsBoxBuffer = new OnnxDebugBox[count];
        }

        for (var i = 0; i < count; i++)
        {
            var box = kept[i];
            _nmsBoxBuffer[i] = new OnnxDebugBox(inputWidth, inputHeight, box.x, box.y, box.w, box.h, box.score);
        }

        // The buffer is reused across frames and handed out as-is, so stale slots from a busier
        // previous frame must be cleared or consumers would see boxes that no longer exist.
        if (_nmsBoxBuffer.Length > count)
        {
            Array.Clear(_nmsBoxBuffer, count, _nmsBoxBuffer.Length - count);
        }

        boxes = _nmsBoxBuffer;
        return true;
    }

    private static float ComputeIoU(
        (float x, float y, float w, float h, float score) a,
        (float x, float y, float w, float h, float score) b)
    {
        var ax1 = a.x - a.w * 0.5f;
        var ay1 = a.y - a.h * 0.5f;
        var ax2 = a.x + a.w * 0.5f;
        var ay2 = a.y + a.h * 0.5f;

        var bx1 = b.x - b.w * 0.5f;
        var by1 = b.y - b.h * 0.5f;
        var bx2 = b.x + b.w * 0.5f;
        var by2 = b.y + b.h * 0.5f;

        var interX1 = Math.Max(ax1, bx1);
        var interY1 = Math.Max(ay1, by1);
        var interX2 = Math.Min(ax2, bx2);
        var interY2 = Math.Min(ay2, by2);
        var interW = Math.Max(0f, interX2 - interX1);
        var interH = Math.Max(0f, interY2 - interY1);
        var interArea = interW * interH;
        var union = a.w * a.h + b.w * b.h - interArea;
        if (union <= 0f)
        {
            return 0f;
        }

        return interArea / union;
    }

    public void Dispose()
    {
        _running = false;
        _frameArrived.Set();
        if (_thread.IsAlive)
        {
            _thread.Join(500);
        }

        if (!_thread.IsAlive)
        {
            _frameArrived.Dispose();
        }
    }
}

