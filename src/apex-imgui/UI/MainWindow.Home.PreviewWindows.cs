using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;

public sealed partial class MainWindow
{
    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint WinMmTimeBeginPeriod(uint periodMs);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint WinMmTimeEndPeriod(uint periodMs);

    // The preview follows the capture -> detection rate instead of a fixed 60Hz: it polls for new
    // frames every millisecond and only repaints when the pipeline actually produced something.
    private const int SmartCorePreviewActiveIntervalMs = 1;
    private readonly object _smartCorePreviewWindowLock = new();
    private System.Windows.Forms.Form? _smartCorePreviewWindow;
    private bool _smartCorePreviewShuttingDown;

    private void UpdateSmartCorePreviewCaptureDemand(bool enabled)
    {
        _dxgiWorker?.SetPreviewFrameCacheEnabled(enabled);
    }

    private void OpenSmartCorePreviewWindow()
    {
        lock (_smartCorePreviewWindowLock)
        {
            if (_smartCorePreviewWindow is not null && !_smartCorePreviewWindow.IsDisposed)
            {
                _smartCorePreviewWindow.BeginInvoke(new Action(() =>
                {
                    _smartCorePreviewWindow.Show();
                    _smartCorePreviewWindow.Activate();
                    _smartCorePreviewWindow.BringToFront();
                }));
                UpdateSmartCorePreviewCaptureDemand(true);
                return;
            }

            var previewWindowThread = new Thread(() =>
            {
                const int WeaponPreviewGapPx = 0;
                const int WeaponPreviewPaddingPx = 0;
                var initialSize = Math.Max(1, _homeViewState.SnapOuterRange);
                var initialWeaponImageHeight = Math.Max(1, WeaponTemplateCatalog.TemplateHeight);
                var initialMetricsHeight = Math.Max(1, (System.Drawing.SystemFonts.MessageBoxFont?.Height ?? 12) * 5);
                var initialWeaponSectionHeight = initialWeaponImageHeight + WeaponPreviewPaddingPx * 2 + initialMetricsHeight;
                using var form = new SmartCorePreviewForm
                {
                    Text = string.Empty,
                    StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen,
                    ClientSize = new System.Drawing.Size(initialSize, initialSize + WeaponPreviewGapPx + initialWeaponSectionHeight),
                    TopMost = true,
                    FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedSingle,
                    MaximizeBox = false,
                    MinimizeBox = false
                };
                form.SizeGripStyle = System.Windows.Forms.SizeGripStyle.Hide;
                form.BackColor = UiColors.WeaponPreviewBackground;
                form.ShowInTaskbar = false;
                var refreshTimer = new System.Windows.Forms.Timer { Interval = SmartCorePreviewActiveIntervalMs };
                form.Shown += (_, _) =>
                {
                    form.MinimumSize = form.Size;
                    form.MaximumSize = form.Size;
                    form.TopMost = true;
                    UpdateSmartCorePreviewCaptureDemand(true);
                };

                form.VisibleChanged += (_, _) =>
                {
                    if (form.Visible)
                    {
                        refreshTimer.Start();
                    }
                    else
                    {
                        refreshTimer.Stop();
                    }

                    UpdateSmartCorePreviewCaptureDemand(form.Visible);
                };

                var frameBuffer = Array.Empty<byte>();
                var lastFrameId = 0;
                var frameWidth = 0;
                var frameHeight = 0;
                string? frameError = null;
                System.Drawing.Bitmap? cachedBitmap = null;
                var sobelBuffer = Array.Empty<byte>();
                var lastSobelFrameId = 0;
                var sobelWidth = 0;
                var sobelHeight = 0;
                System.Drawing.Bitmap? cachedSobelBitmap = null;
                var captureLatencySamples = new List<double>(256);
                var statsWindowStartUtc = DateTime.UtcNow;
                var captureSampleCount = 0;
                var captureLatencySumMs = 0.0;
                var displayCaptureFps = 0.0;
                var displayCaptureAvgMs = 0.0;
                var displayOnnxAvgMs = 0.0;
                var displayWeaponSimilarity = 0.0f;
                var displayWeaponName = WeaponTemplateCatalog.EmptyHandName;

                refreshTimer.Tick += (_, _) =>
                {
                    if (!form.Visible)
                    {
                        return;
                    }

                    var targetSize = Math.Max(1, _homeViewState.SnapOuterRange);
                    var weaponImageHeight = Math.Max(1, WeaponTemplateCatalog.TemplateHeight);
                    var metricsHeight = Math.Max(1, form.Font.Height * 5);
                    var weaponSectionHeight = weaponImageHeight + WeaponPreviewPaddingPx * 2 + metricsHeight;
                    var expectedClientSize = new System.Drawing.Size(targetSize, targetSize + WeaponPreviewGapPx + weaponSectionHeight);
                    if (form.ClientSize != expectedClientSize)
                    {
                        form.ClientSize = expectedClientSize;
                        form.MinimumSize = form.Size;
                        form.MaximumSize = form.Size;
                    }

                    var worker = _dxgiWorker;
                    var hasNewFrame = false;
                    if (worker is not null)
                    {
                        hasNewFrame = worker.TryCopyLatestFrame(ref frameBuffer, ref lastFrameId, out frameWidth, out frameHeight, out frameError);
                        captureLatencySamples.Clear();
                        worker.DrainCaptureSamples(captureLatencySamples);
                        if (captureLatencySamples.Count > 0)
                        {
                            captureSampleCount += captureLatencySamples.Count;
                            for (var i = 0; i < captureLatencySamples.Count; i++)
                            {
                                captureLatencySumMs += captureLatencySamples[i];
                            }
                        }
                    }
                    else
                    {
                        frameWidth = 0;
                        frameHeight = 0;
                    }

                    var hasNewSobel = false;
                    var weaponWorker = _weaponRecWorker;
                    if (weaponWorker is not null)
                    {
                        hasNewSobel = weaponWorker.TryCopyLatestSobel(ref sobelBuffer, ref lastSobelFrameId, out sobelWidth, out sobelHeight);
                    }
                    else
                    {
                        sobelWidth = 0;
                        sobelHeight = 0;
                    }

                    var statsUpdated = false;
                    var statsElapsed = DateTime.UtcNow - statsWindowStartUtc;
                    if (statsElapsed.TotalSeconds >= 1.0)
                    {
                        var elapsedSeconds = Math.Max(0.001, statsElapsed.TotalSeconds);
                        displayCaptureFps = captureSampleCount / elapsedSeconds;
                        displayCaptureAvgMs = captureSampleCount > 0 ? captureLatencySumMs / captureSampleCount : 0.0;
                        displayOnnxAvgMs = _onnxWorker?.GetSnapshot().AvgInferenceMs ?? 0.0;
                        var weaponResult = _weaponRecWorker?.GetLatestResult() ?? WeaponRecognitionResultState.Empty;
                        displayWeaponSimilarity = weaponResult.Similarity;
                        displayWeaponName = weaponResult.WeaponName;
                        captureSampleCount = 0;
                        captureLatencySumMs = 0.0;
                        statsWindowStartUtc = DateTime.UtcNow;
                        statsUpdated = true;
                    }

                    if (hasNewFrame || hasNewSobel || worker is null || statsUpdated)
                    {
                        form.Invalidate();
                    }
                };

                form.Paint += (_, e) =>
                {
                    e.Graphics.Clear(UiColors.WeaponPreviewBackground);
                    e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                    e.Graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;

                    var mainSize = Math.Max(1, _homeViewState.SnapOuterRange);
                    var mainRect = new System.Drawing.Rectangle(0, 0, mainSize, mainSize);
                    if (frameWidth <= 0 || frameHeight <= 0 || frameBuffer.Length != frameWidth * frameHeight * 4)
                    {
                        var statusText = string.IsNullOrWhiteSpace(frameError) ? "等待捕获画面..." : $"捕获错误: {frameError}";
                        using var statusBrush = new System.Drawing.SolidBrush(UiColors.PreviewStatusText);
                        e.Graphics.DrawString(statusText, form.Font, statusBrush, new System.Drawing.PointF(mainRect.X + 12f, mainRect.Y + 12f));
                    }
                    else
                    {
                        var scale = Math.Min(mainRect.Width / (float)frameWidth, mainRect.Height / (float)frameHeight);
                        scale = Math.Max(scale, 1f);
                        var drawWidth = Math.Max(1, (int)MathF.Round(frameWidth * scale));
                        var drawHeight = Math.Max(1, (int)MathF.Round(frameHeight * scale));
                        var drawRect = new System.Drawing.Rectangle(
                            mainRect.X + (mainRect.Width - drawWidth) / 2,
                            mainRect.Y + (mainRect.Height - drawHeight) / 2,
                            drawWidth,
                            drawHeight);

                        if (cachedBitmap is null || cachedBitmap.Width != frameWidth || cachedBitmap.Height != frameHeight)
                        {
                            cachedBitmap?.Dispose();
                            // 32bppRgb ignores the unused 4th byte: OBS' Spout texture carries
                            // alpha 0, and only this preview ever consumed alpha, so the capture
                            // side no longer has to force it opaque.
                            cachedBitmap = new System.Drawing.Bitmap(frameWidth, frameHeight, PixelFormat.Format32bppRgb);
                        }

                        var bitmapData = cachedBitmap.LockBits(
                            new System.Drawing.Rectangle(0, 0, frameWidth, frameHeight),
                            ImageLockMode.WriteOnly,
                            PixelFormat.Format32bppRgb);
                        try
                        {
                            Marshal.Copy(frameBuffer, 0, bitmapData.Scan0, frameBuffer.Length);
                        }
                        finally
                        {
                            cachedBitmap.UnlockBits(bitmapData);
                        }

                        e.Graphics.DrawImage(cachedBitmap, drawRect);

                        var boxes = _onnxWorker?.GetDebugBoxes() ?? Array.Empty<OnnxDebugBox>();
                        if (boxes.Length > 0)
                        {
                            using var boxPen = new System.Drawing.Pen(UiColors.WeaponPreviewTargetBox, 2f);
                            for (var i = 0; i < boxes.Length; i++)
                            {
                                var box = boxes[i];
                                var inputWidth = Math.Max(1, box.InputWidth);
                                var inputHeight = Math.Max(1, box.InputHeight);
                                var x1 = box.X - box.W * 0.5f;
                                var y1 = box.Y - box.H * 0.5f;
                                var x2 = box.X + box.W * 0.5f;
                                var y2 = box.Y + box.H * 0.5f;

                                var minX = Math.Clamp(MathF.Min(x1, x2), 0f, inputWidth);
                                var minY = Math.Clamp(MathF.Min(y1, y2), 0f, inputHeight);
                                var maxX = Math.Clamp(MathF.Max(x1, x2), 0f, inputWidth);
                                var maxY = Math.Clamp(MathF.Max(y1, y2), 0f, inputHeight);

                                var overlayRect = new System.Drawing.RectangleF(
                                    drawRect.Left + minX / inputWidth * drawRect.Width,
                                    drawRect.Top + minY / inputHeight * drawRect.Height,
                                    (maxX - minX) / inputWidth * drawRect.Width,
                                    (maxY - minY) / inputHeight * drawRect.Height);

                                if (overlayRect.Width > 1f && overlayRect.Height > 1f)
                                {
                                    e.Graphics.DrawRectangle(boxPen, overlayRect.X, overlayRect.Y, overlayRect.Width, overlayRect.Height);
                                }
                            }
                        }
                    }

                    var sobelSectionRect = new System.Drawing.Rectangle(
                        0,
                        mainRect.Bottom + WeaponPreviewGapPx,
                        form.ClientSize.Width,
                        Math.Max(1, form.ClientSize.Height - (mainRect.Bottom + WeaponPreviewGapPx)));

                    if (sobelWidth > 0 && sobelHeight > 0 && sobelBuffer.Length == sobelWidth * sobelHeight)
                    {
                        if (cachedSobelBitmap is null || cachedSobelBitmap.Width != sobelWidth || cachedSobelBitmap.Height != sobelHeight)
                        {
                            cachedSobelBitmap?.Dispose();
                            cachedSobelBitmap = new System.Drawing.Bitmap(sobelWidth, sobelHeight, PixelFormat.Format32bppArgb);
                        }

                        var rgba = new byte[sobelWidth * sobelHeight * 4];
                        for (var i = 0; i < sobelBuffer.Length; i++)
                        {
                            var g = sobelBuffer[i];
                            var dst = i * 4;
                            rgba[dst + 0] = g;
                            rgba[dst + 1] = g;
                            rgba[dst + 2] = g;
                            rgba[dst + 3] = 255;
                        }

                        var sobelBitmapData = cachedSobelBitmap.LockBits(
                            new System.Drawing.Rectangle(0, 0, sobelWidth, sobelHeight),
                            ImageLockMode.WriteOnly,
                            PixelFormat.Format32bppArgb);
                        try
                        {
                            Marshal.Copy(rgba, 0, sobelBitmapData.Scan0, rgba.Length);
                        }
                        finally
                        {
                            cachedSobelBitmap.UnlockBits(sobelBitmapData);
                        }

                        var sobelDrawRect = new System.Drawing.Rectangle(
                            sobelSectionRect.X + WeaponPreviewPaddingPx,
                            sobelSectionRect.Y + WeaponPreviewPaddingPx,
                            sobelWidth,
                            sobelHeight);
                        e.Graphics.DrawImage(cachedSobelBitmap, sobelDrawRect);
                    }
                    else
                    {
                        using var statusBrush = new System.Drawing.SolidBrush(UiColors.PreviewStatusText);
                        e.Graphics.DrawString(
                            "Weapon Sobel: waiting...",
                            form.Font,
                            statusBrush,
                            new System.Drawing.PointF(sobelSectionRect.X + 12f, sobelSectionRect.Y + 10f));
                    }

                    using var metricsBrush = new System.Drawing.SolidBrush(UiColors.PreviewStatusText);
                    var metricsX = sobelSectionRect.X + 2f;
                    var metricsY = sobelSectionRect.Y + Math.Max(1, WeaponTemplateCatalog.TemplateHeight);
                    var lineHeight = form.Font.GetHeight(e.Graphics);
                    e.Graphics.DrawString($"Capture FPS: {displayCaptureFps:F1}", form.Font, metricsBrush, new System.Drawing.PointF(metricsX, metricsY));
                    e.Graphics.DrawString($"Capture Avg: {displayCaptureAvgMs:F2} ms", form.Font, metricsBrush, new System.Drawing.PointF(metricsX, metricsY + lineHeight));
                    e.Graphics.DrawString($"ONNX Avg: {displayOnnxAvgMs:F2} ms", form.Font, metricsBrush, new System.Drawing.PointF(metricsX, metricsY + lineHeight * 2f));
                    e.Graphics.DrawString($"Similarity: {displayWeaponSimilarity:F3}", form.Font, metricsBrush, new System.Drawing.PointF(metricsX, metricsY + lineHeight * 3f));
                    e.Graphics.DrawString($"Weapon: {displayWeaponName}", form.Font, metricsBrush, new System.Drawing.PointF(metricsX, metricsY + lineHeight * 4f));
                };

                form.FormClosing += (_, e) =>
                {
                    if (!_smartCorePreviewShuttingDown && e.CloseReason == System.Windows.Forms.CloseReason.UserClosing)
                    {
                        e.Cancel = true;
                        form.Hide();
                    }
                };

                form.FormClosed += (_, _) =>
                {
                    UpdateSmartCorePreviewCaptureDemand(false);
                    refreshTimer.Stop();
                    refreshTimer.Dispose();
                    cachedBitmap?.Dispose();
                    cachedSobelBitmap?.Dispose();
                    lock (_smartCorePreviewWindowLock)
                    {
                        _smartCorePreviewWindow = null;
                    }
                };

                lock (_smartCorePreviewWindowLock)
                {
                    _smartCorePreviewWindow = form;
                }

                form.Show();
                WinMmTimeBeginPeriod(1);
                try
                {
                    System.Windows.Forms.Application.Run(form);
                }
                finally
                {
                    WinMmTimeEndPeriod(1);
                }
            })
            {
                IsBackground = true,
                Name = "SmartCorePreviewWindowThread"
            };
            previewWindowThread.SetApartmentState(ApartmentState.STA);
            previewWindowThread.Start();
        }
    }

    private void CloseSmartCorePreviewWindow()
    {
        lock (_smartCorePreviewWindowLock)
        {
            if (_smartCorePreviewWindow is not null && !_smartCorePreviewWindow.IsDisposed)
            {
                _smartCorePreviewShuttingDown = true;
                UpdateSmartCorePreviewCaptureDemand(false);
                _smartCorePreviewWindow.BeginInvoke(new Action(() => _smartCorePreviewWindow.Close()));
            }
        }
    }

    private bool IsSmartCorePreviewWindowOpen()
    {
        lock (_smartCorePreviewWindowLock)
        {
            return _smartCorePreviewWindow is not null && !_smartCorePreviewWindow.IsDisposed && _smartCorePreviewWindow.Visible;
        }
    }
}
