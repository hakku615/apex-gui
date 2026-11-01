using System.Diagnostics;
using System.Runtime.InteropServices;
using Spout2.NET;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;

internal sealed class SpoutCapturer : IScreenCapturer
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private SpoutReceiver _receiver;
    private readonly Stopwatch _disconnectedFor = Stopwatch.StartNew();
    private byte[] _previousFrame = Array.Empty<byte>();
    private readonly string _senderName;
    private readonly object _regionLock = new();
    private byte[] _frameBuffer = Array.Empty<byte>();
    private byte[] _weaponRoiBuffer = Array.Empty<byte>();
    private ID3D11Texture2D? _frameStaging;
    private ID3D11Texture2D? _weaponRoiStaging;
    private int _requestedWidth;
    private int _requestedHeight;
    private int _frameWidth;
    private int _frameHeight;
    private int _captureWidth;
    private int _captureHeight;
    private int _captureLeft;
    private int _captureTop;
    private int _weaponRoiLeft;
    private int _weaponRoiTop;
    private int _weaponRoiWidth;
    private int _weaponRoiHeight;
    private bool _disposed;

    public SpoutCapturer(string? senderName)
    {
        _senderName = senderName?.Trim() ?? string.Empty;
        _device = D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport);
        _context = _device.ImmediateContext;
        _receiver = new SpoutReceiver(
            _device.NativePointer,
            _senderName.Length == 0 ? null : _senderName);
        SetCaptureRegion(320, 320);
    }

    public void SetCaptureRegion(int width, int height)
    {
        lock (_regionLock)
        {
            _requestedWidth = Math.Max(1, width);
            _requestedHeight = Math.Max(1, height);
            if (_frameWidth > 0 && _frameHeight > 0)
            {
                RebuildFrameLayoutUnlocked();
            }
        }
    }

    public bool TryWaitForFrame(int timeoutMs)
    {
        _ = timeoutMs;
        return !_disposed;
    }

    public bool TryCaptureFrame(
        int timeoutMs,
        bool captureWeaponRoi,
        out byte[] frameData,
        out int width,
        out int height,
        out byte[] weaponRoiData,
        out int weaponRoiWidth,
        out int weaponRoiHeight,
        out string? error)
    {
        _ = timeoutMs;
        frameData = Array.Empty<byte>();
        width = 0;
        height = 0;
        weaponRoiData = Array.Empty<byte>();
        weaponRoiWidth = 0;
        weaponRoiHeight = 0;
        error = null;

        if (_disposed)
        {
            error = "Spout capture disposed";
            return false;
        }

        try
        {
            if (!_receiver.Receive())
            {
                // The Spout receiver binds to a sender when it is created. If the sender did not exist
                // yet (game/OBS started after us), it never connects, so rebuild it once it appears.
                if (_senderName.Length > 0 &&
                    _disconnectedFor.Elapsed.TotalMilliseconds > 1000.0 &&
                    SenderExists(_senderName))
                {
                    _receiver.Dispose();
                    _receiver = new SpoutReceiver(_device.NativePointer, _senderName);
                    _disconnectedFor.Restart();
                }

                error = string.IsNullOrEmpty(_senderName)
                    ? "未找到 Spout sender（OBS 未运行或未开启 Spout 输出）"
                    : $"未找到 Spout sender: {_senderName}";
                return false;
            }

            _disconnectedFor.Restart();

            var texturePointer = _receiver.Texture;
            var senderWidth = _receiver.SenderWidth;
            var senderHeight = _receiver.SenderHeight;
            if (texturePointer == 0 || senderWidth <= 0 || senderHeight <= 0)
            {
                error = "Spout sender 暂时没有可用帧";
                return false;
            }

            lock (_regionLock)
            {
                if (senderWidth != _frameWidth || senderHeight != _frameHeight)
                {
                    _frameWidth = senderWidth;
                    _frameHeight = senderHeight;
                    RebuildFrameLayoutUnlocked();
                }
            }

            // The Spout receiver hands out a borrowed ID3D11Texture2D* (no added reference).
            // Take one of our own so disposing the wrapper cannot destroy the sender's texture.
            using var senderTexture = new ID3D11Texture2D(texturePointer);
            senderTexture.AddRef();

            int captureWidth;
            int captureHeight;
            int captureLeft;
            int captureTop;
            byte[] frameBuffer;
            int weaponLeft;
            int weaponTop;
            int weaponWidth;
            int weaponHeight;
            byte[] weaponBuffer;
            lock (_regionLock)
            {
                captureWidth = _captureWidth;
                captureHeight = _captureHeight;
                captureLeft = _captureLeft;
                captureTop = _captureTop;
                frameBuffer = _frameBuffer;
                weaponLeft = _weaponRoiLeft;
                weaponTop = _weaponRoiTop;
                weaponWidth = _weaponRoiWidth;
                weaponHeight = _weaponRoiHeight;
                weaponBuffer = _weaponRoiBuffer;
            }

            if (captureWidth <= 0 || captureHeight <= 0 || frameBuffer.Length != captureWidth * captureHeight * 4)
            {
                error = "Spout 捕获区域无效";
                return false;
            }

            var frameStaging = _frameStaging;
            if (frameStaging is null)
            {
                error = "Spout staging texture unavailable";
                return false;
            }

            CopyRegionToBuffer(senderTexture, frameStaging, captureLeft, captureTop, captureWidth, captureHeight, frameBuffer);
            ForceOpaque(frameBuffer);

            // Always take a fresh copy, then skip only when the picture is byte-for-byte identical:
            // the sender keeps its texture between updates, so re-running inference on a duplicate
            // would just burn the GPU without changing the result.
            var frameSpan = frameBuffer.AsSpan();
            if (_previousFrame.Length == frameBuffer.Length && frameSpan.SequenceEqual(_previousFrame))
            {
                return false;
            }

            if (_previousFrame.Length != frameBuffer.Length)
            {
                _previousFrame = new byte[frameBuffer.Length];
            }

            Buffer.BlockCopy(frameBuffer, 0, _previousFrame, 0, frameBuffer.Length);

            frameData = frameBuffer;
            width = captureWidth;
            height = captureHeight;

            if (captureWeaponRoi &&
                weaponWidth > 0 && weaponHeight > 0 &&
                _weaponRoiStaging is not null &&
                weaponBuffer.Length == weaponWidth * weaponHeight * 3)
            {
                CopyRegionToBgrBuffer(senderTexture, _weaponRoiStaging, weaponLeft, weaponTop, weaponWidth, weaponHeight, weaponBuffer);
                weaponRoiData = weaponBuffer;
                weaponRoiWidth = weaponWidth;
                weaponRoiHeight = weaponHeight;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _frameStaging?.Dispose();
        _frameStaging = null;
        _weaponRoiStaging?.Dispose();
        _weaponRoiStaging = null;
        _receiver.Dispose();
        _context.Dispose();
        _device.Dispose();
    }

    private void RebuildFrameLayoutUnlocked()
    {
        var clampedWidth = Math.Clamp(_requestedWidth, 1, _frameWidth);
        var clampedHeight = Math.Clamp(_requestedHeight, 1, _frameHeight);
        _captureWidth = clampedWidth;
        _captureHeight = clampedHeight;
        _captureLeft = (_frameWidth - clampedWidth) / 2;
        _captureTop = (_frameHeight - clampedHeight) / 2;

        var requiredBytes = clampedWidth * clampedHeight * 4;
        if (_frameBuffer.Length != requiredBytes)
        {
            _frameBuffer = new byte[requiredBytes];
        }

        if (_frameStaging is null ||
            _frameStaging.Description.Width != (uint)clampedWidth ||
            _frameStaging.Description.Height != (uint)clampedHeight)
        {
            _frameStaging?.Dispose();
            _frameStaging = CreateStagingTexture(clampedWidth, clampedHeight);
        }

        var (roiLeft, roiTop, roiWidth, roiHeight) = DesktopCaptureFrameCopy.CalcWeaponRoi(_frameWidth, _frameHeight);
        _weaponRoiLeft = roiLeft;
        _weaponRoiTop = roiTop;
        _weaponRoiWidth = roiWidth;
        _weaponRoiHeight = roiHeight;

        var roiBytes = roiWidth * roiHeight * 3;
        if (_weaponRoiBuffer.Length != roiBytes)
        {
            _weaponRoiBuffer = roiBytes > 0 ? new byte[roiBytes] : Array.Empty<byte>();
        }

        if (roiWidth <= 0 || roiHeight <= 0)
        {
            _weaponRoiStaging?.Dispose();
            _weaponRoiStaging = null;
            return;
        }

        if (_weaponRoiStaging is null ||
            _weaponRoiStaging.Description.Width != (uint)roiWidth ||
            _weaponRoiStaging.Description.Height != (uint)roiHeight)
        {
            _weaponRoiStaging?.Dispose();
            _weaponRoiStaging = CreateStagingTexture(roiWidth, roiHeight);
        }
    }

    private unsafe void CopyRegionToBgrBuffer(
        ID3D11Texture2D source,
        ID3D11Texture2D staging,
        int left,
        int top,
        int width,
        int height,
        byte[] destination)
    {
        _context.CopySubresourceRegion(
            staging, 0, 0, 0, 0, source, 0,
            new Vortice.Mathematics.Box(left, top, 0, left + width, top + height, 1));
        _context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out var mapped).CheckError();
        try
        {
            var sourcePointer = (byte*)mapped.DataPointer;
            for (var y = 0; y < height; y++)
            {
                var sourceRow = sourcePointer + y * mapped.RowPitch;
                var destinationRow = y * width * 3;
                for (var x = 0; x < width; x++)
                {
                    var pixelOffset = x * 4;
                    destination[destinationRow + x * 3 + 0] = sourceRow[pixelOffset + 2];
                    destination[destinationRow + x * 3 + 1] = sourceRow[pixelOffset + 1];
                    destination[destinationRow + x * 3 + 2] = sourceRow[pixelOffset + 0];
                }
            }
        }
        finally
        {
            _context.Unmap(staging, 0);
        }
    }

    private void CopyRegionToBuffer(
        ID3D11Texture2D source,
        ID3D11Texture2D staging,
        int left,
        int top,
        int width,
        int height,
        byte[] destination)
    {
        _context.CopySubresourceRegion(
            staging, 0, 0, 0, 0, source, 0,
            new Vortice.Mathematics.Box(left, top, 0, left + width, top + height, 1));
        _context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out var mapped).CheckError();
        try
        {
            var rowBytes = width * 4;
            for (var y = 0; y < height; y++)
            {
                Marshal.Copy(mapped.DataPointer + y * (int)mapped.RowPitch, destination, y * rowBytes, rowBytes);
            }
        }
        finally
        {
            _context.Unmap(staging, 0);
        }
    }

    private static void ForceOpaque(byte[] bgra)
    {
        // OBS' scene render target carries alpha 0 in the Spout texture; the preview draws with
        // Format32bppArgb, so unnormalised alpha makes the captured picture invisible.
        for (var i = 3; i < bgra.Length; i += 4)
        {
            bgra[i] = 255;
        }
    }

    private static bool SenderExists(string senderName)
    {
        try
        {
            using var senders = new SpoutSenders();
            foreach (var name in senders.Names())
            {
                if (string.Equals(name, senderName, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
        catch
        {
            // Treat enumeration failures as "not available yet".
        }

        return false;
    }


    private ID3D11Texture2D CreateStagingTexture(int width, int height)
    {
        var textureDesc = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None
        };
        return _device.CreateTexture2D(textureDesc);
    }
}
