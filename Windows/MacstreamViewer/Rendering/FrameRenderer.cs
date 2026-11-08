using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using MacstreamViewer.Diagnostics;
using MacstreamViewer.Protocol;
using MacstreamViewer.Video;

namespace MacstreamViewer.Rendering;

public class FrameRenderer
{
    private readonly ILogger _logger;
    private readonly IntPtr _windowHandle;
    private WriteableBitmap? _bitmap;
    private D3DImageFramePresenter? _d3dPresenter;
    private int _width;
    private int _height;
    private int _stride;
    private readonly object _renderLock = new();
    private PendingFrame? _pendingFrame;
    private bool _renderScheduled;
    private DateTime _lastRenderReportTime = DateTime.UtcNow;
    private long _renderTicks;
    private long _d3dUploadTicks;
    private long _d3dUpdateSurfaceTicks;
    private long _d3dDirtyTicks;
    private long _d3d11SrvTicks;
    private long _d3d11DrawTicks;
    private int _renderFrames;
    private int _replacedPendingFrames;
    private string _rendererName = "none";
    private bool _d3d11DirectDisabled;
    private bool _d3d11DirectFailureLogged;

    public ImageSource? Bitmap => (ImageSource?)_d3dPresenter?.Image ?? _bitmap;

    public bool HasPendingFrame
    {
        get
        {
            lock (_renderLock)
            {
                return _pendingFrame != null;
            }
        }
    }

    public event EventHandler? OnFrameRendered;
    public event EventHandler? OnImageSourceChanged;

    public FrameRenderer(ILogger<FrameRenderer> logger, IntPtr windowHandle)
    {
        _logger = logger;
        _windowHandle = windowHandle;
    }

    public void Initialize(int width, int height)
    {
        _width = width;
        _height = height;
        _stride = width * 4;
        _d3dPresenter?.Dispose();
        _d3dPresenter = null;
        _bitmap = null;
        _d3d11DirectDisabled = false;
        _d3d11DirectFailureLogged = false;

        if (ShouldUseD3DImage())
        {
            var presenter = new D3DImageFramePresenter(_windowHandle);
            if (presenter.Initialize(width, height))
            {
                _d3dPresenter = presenter;
                _rendererName = "D3DImage";
                _logger.LogInformation("D3DImage renderer initialized ({Width}x{Height})", width, height);
                OnImageSourceChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            presenter.Dispose();
            _logger.LogWarning("D3DImage renderer init failed, falling back to WriteableBitmap");
        }

        _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        _rendererName = "WriteableBitmap";
        _logger.LogInformation("WriteableBitmap renderer initialized ({Width}x{Height})", width, height);
        OnImageSourceChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RenderRawFrame(RawFramePayload frame)
    {
        if (_bitmap == null && _d3dPresenter == null) return;

        var sourceStride = frame.BytesPerRow > 0 ? frame.BytesPerRow : _stride;
        var expectedSize = sourceStride * _height;
        if (!TryGetArray(frame.PixelData, out var data, out var offset)) return;
        if (data.Length - offset < expectedSize) return;

        QueueLatestFrame(new PendingFrame(data, offset, expectedSize, sourceStride, FlipVertical: true, ReturnBuffer: null));
    }

    public void RenderDecodedFrame(DecodedFrameData frame)
    {
        if (_bitmap == null && _d3dPresenter == null)
        {
            frame.ReturnBuffer?.Invoke(frame.PixelData);
            return;
        }

        var data = frame.PixelData;
        var length = frame.PixelDataLength > 0 ? frame.PixelDataLength : data.Length;
        if (data.Length == 0 || length <= 0)
        {
            frame.ReturnBuffer?.Invoke(data);
            return;
        }

        QueueLatestFrame(new PendingFrame(data, 0, length, _stride, FlipVertical: false, frame.ReturnBuffer));
    }

    public bool CanUseD3D11Direct => _d3dPresenter != null && !_d3d11DirectDisabled && ShouldUseD3D11Direct();

    public bool RenderD3D11Frame(D3D11DecodedFrame frame)
    {
        if (!CanUseD3D11Direct)
            return false;

        if (TryPresentToSwapChain(frame))
            return true;

        QueueLatestFrame(PendingFrame.FromD3D11(frame));
        return true;
    }

    // --- Swap-chain presentation (preferred low-latency path) ------------------------------
    // Frames are drawn and presented on the decode thread the moment they're decoded, into the
    // native SwapChainVideoHost window, with no dispatcher hop and no WPF composition. If anything
    // fails, presentation falls back to the D3DImage path for the rest of the session.
    // MACSTREAM_SWAPCHAIN=0 disables it.

    private SwapChainVideoHost? _swapHost;
    private D3D11VideoRenderer? _swapRenderer;
    private D3D11DecodedFrame? _lastSwapFrame;
    private readonly object _swapLock = new();
    private bool _swapChainFailed;
    private bool _swapChainPresenting;
    private bool _swapHostShowRequested;

    /// <summary>The host window must become visible (and get a size) before presenting can start.</summary>
    public event EventHandler? SwapChainHostNeeded;

    /// <summary>True once frames are being presented through the swap chain; false when that stops.</summary>
    public event EventHandler<bool>? SwapChainPresentingChanged;

    public void AttachSwapChainHost(SwapChainVideoHost host)
    {
        _swapHost = host;
        host.PixelSizeChanged += OnSwapHostResized;
    }

    private static bool SwapChainAllowed() =>
        EnvVars.Get("MACSTREAM_SWAPCHAIN") != "0";

    private bool TryPresentToSwapChain(D3D11DecodedFrame frame)
    {
        var host = _swapHost;
        if (host == null || _swapChainFailed || !SwapChainAllowed())
            return false;

        if (host.ChildHandle == IntPtr.Zero || host.PixelWidth <= 0 || host.PixelHeight <= 0)
        {
            if (!_swapHostShowRequested)
            {
                _swapHostShowRequested = true;
                SwapChainHostNeeded?.Invoke(this, EventArgs.Empty);
            }
            return false;
        }

        bool presented;
        var started = false;
        lock (_swapLock)
        {
            _swapRenderer ??= new D3D11VideoRenderer();
            presented = _swapRenderer.InitializeSwapChain(
                            frame.Device, frame.DeviceContext, host.ChildHandle,
                            frame.Width, frame.Height, host.PixelWidth, host.PixelHeight)
                        && _swapRenderer.Present(frame, out _);

            if (presented)
            {
                _lastSwapFrame?.Release();
                _lastSwapFrame = frame;   // kept so a resize can redraw without waiting for a new frame
                started = !_swapChainPresenting;
                _swapChainPresenting = true;
            }
            else
            {
                _swapChainFailed = true;
                DisposeSwapRendererLocked();
            }
        }

        if (!presented)
        {
            DiagLog.Write("Swap chain presentation failed; using the WPF D3DImage path for this session");
            SwapChainPresentingChanged?.Invoke(this, false);
            return false;
        }

        if (started)
        {
            _rendererName = "SwapChain";
            DiagLog.Write("Presenting through the native swap chain");
            SwapChainPresentingChanged?.Invoke(this, true);
        }

        OnFrameRendered?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void OnSwapHostResized(int width, int height)
    {
        lock (_swapLock)
        {
            if (_swapRenderer == null || !_swapRenderer.IsSwapChainMode)
                return;

            _swapRenderer.RequestResize(width, height);
            if (_lastSwapFrame is { } last)
                _swapRenderer.Present(last, out _);   // redraw at the new size right away
        }
    }

    /// <summary>Releases the swap chain and the last frame. Call before disposing the decoder.</summary>
    public void ResetSwapChain()
    {
        bool wasPresenting;
        lock (_swapLock)
        {
            wasPresenting = _swapChainPresenting;
            DisposeSwapRendererLocked();
            _swapChainFailed = false;
            _swapHostShowRequested = false;
        }

        if (wasPresenting)
            SwapChainPresentingChanged?.Invoke(this, false);
    }

    private void DisposeSwapRendererLocked()
    {
        _lastSwapFrame?.Release();
        _lastSwapFrame = null;
        _swapRenderer?.Dispose();
        _swapRenderer = null;
        _swapChainPresenting = false;
    }

    private static bool TryGetArray(ReadOnlyMemory<byte> memory, out byte[] data, out int offset)
    {
        if (System.Runtime.InteropServices.MemoryMarshal.TryGetArray(memory, out var segment)
            && segment.Array != null)
        {
            data = segment.Array;
            offset = segment.Offset;
            return true;
        }

        data = memory.ToArray();
        offset = 0;
        return true;
    }

    private void QueueLatestFrame(PendingFrame frame)
    {
        var shouldSchedule = false;
        PendingFrame? replacedFrame = null;

        lock (_renderLock)
        {
            replacedFrame = _pendingFrame;
            if (replacedFrame != null)
                _replacedPendingFrames++;

            _pendingFrame = frame;
            if (!_renderScheduled)
            {
                _renderScheduled = true;
                shouldSchedule = true;
            }
        }

        replacedFrame?.Release();

        if (shouldSchedule)
            Application.Current.Dispatcher.BeginInvoke(RenderPendingFrame, DispatcherPriority.Render);
    }

    private void RenderPendingFrame()
    {
        PendingFrame? frame;
        lock (_renderLock)
        {
            frame = _pendingFrame;
            _pendingFrame = null;
        }

        if (frame != null)
        {
            try
            {
                WriteFrame(frame);
            }
            finally
            {
                frame.Release();
            }
        }

        var shouldScheduleAgain = false;
        lock (_renderLock)
        {
            if (_pendingFrame != null)
            {
                shouldScheduleAgain = true;
            }
            else
            {
                _renderScheduled = false;
            }
        }

        if (shouldScheduleAgain)
            Application.Current.Dispatcher.BeginInvoke(RenderPendingFrame, DispatcherPriority.Render);
    }

    private void WriteFrame(PendingFrame frame)
    {
        if (_bitmap == null && _d3dPresenter == null) return;

        if (frame.D3D11Frame != null)
        {
            WriteD3D11Frame(frame.D3D11Frame.Value);
            return;
        }

        var copyLen = Math.Min(frame.Length, Math.Min(frame.Data.Length - frame.Offset, _stride * _height));
        if (copyLen <= 0) return;

        var renderStart = Stopwatch.GetTimestamp();
        if (_d3dPresenter != null)
        {
            if (_d3dPresenter.Present(frame.Data, frame.Offset, copyLen, frame.SourceStride, frame.FlipVertical, out var presentMetrics))
            {
                OnFrameRendered?.Invoke(this, EventArgs.Empty);
                RecordRenderWork(renderStart, presentMetrics);
                return;
            }

            _logger.LogWarning("D3DImage Present failed, falling back to WriteableBitmap");
            DiagLog.Write("D3DImage Present failed, falling back to WriteableBitmap");
            _d3dPresenter.Dispose();
            _d3dPresenter = null;
            _bitmap = new WriteableBitmap(_width, _height, 96, 96, PixelFormats.Bgra32, null);
            _rendererName = "WriteableBitmap";
            OnImageSourceChanged?.Invoke(this, EventArgs.Empty);
        }

        if (_bitmap == null) return;

        _bitmap.Lock();
        try
        {
            unsafe
            {
                var dst = (byte*)_bitmap.BackBuffer;
                var dstStride = _bitmap.BackBufferStride;
                fixed (byte* srcBase = frame.Data)
                {
                    var src = srcBase + frame.Offset;
                    if (frame.FlipVertical)
                    {
                        for (int y = 0; y < _height; y++)
                        {
                            Buffer.MemoryCopy(
                                src + y * frame.SourceStride,
                                dst + (_height - 1 - y) * dstStride,
                                _stride,
                                _stride);
                        }
                    }
                    else
                    {
                        Buffer.MemoryCopy(src, dst, copyLen, copyLen);
                    }
                }
                _bitmap.AddDirtyRect(new Int32Rect(0, 0, _width, _height));
            }
        }
        finally
        {
            _bitmap.Unlock();
        }

        OnFrameRendered?.Invoke(this, EventArgs.Empty);
        RecordRenderWork(renderStart);
    }

    private void WriteD3D11Frame(D3D11DecodedFrame frame)
    {
        if (_d3dPresenter == null || _d3d11DirectDisabled)
            return;

        var renderStart = Stopwatch.GetTimestamp();
        if (_d3dPresenter.PresentD3D11Frame(frame, out var metrics))
        {
            OnFrameRendered?.Invoke(this, EventArgs.Empty);
            RecordD3D11RenderWork(renderStart, metrics);
            return;
        }

        _d3d11DirectDisabled = true;
        if (!_d3d11DirectFailureLogged)
        {
            _d3d11DirectFailureLogged = true;
            DiagLog.Write("D3D11 direct render failed; later frames use the download + BGRA path");
        }
    }

    private void RecordRenderWork(long startTimestamp, D3DImagePresentMetrics presentMetrics = default)
    {
        _renderTicks += Stopwatch.GetTimestamp() - startTimestamp;
        _d3dUploadTicks += presentMetrics.UploadTicks;
        _d3dUpdateSurfaceTicks += presentMetrics.UpdateSurfaceTicks;
        _d3dDirtyTicks += presentMetrics.DirtyTicks;
        _renderFrames++;

        var now = DateTime.UtcNow;
        if ((now - _lastRenderReportTime).TotalSeconds < 2.0)
            return;

        var frameCount = Math.Max(1, _renderFrames);
        var avgMs = TicksToAverageMs(_renderTicks, frameCount);
        var uploadMs = TicksToAverageMs(_d3dUploadTicks, frameCount);
        var updateMs = TicksToAverageMs(_d3dUpdateSurfaceTicks, frameCount);
        var dirtyMs = TicksToAverageMs(_d3dDirtyTicks, frameCount);
        var otherMs = Math.Max(0, avgMs - uploadMs - updateMs - dirtyMs);
        var frames = _renderFrames;
        var replaced = _replacedPendingFrames;
        var renderer = _rendererName;
        _renderTicks = 0;
        _d3dUploadTicks = 0;
        _d3dUpdateSurfaceTicks = 0;
        _d3dDirtyTicks = 0;
        _d3d11SrvTicks = 0;
        _d3d11DrawTicks = 0;
        _renderFrames = 0;
        _replacedPendingFrames = 0;
        _lastRenderReportTime = now;

        if (renderer == "D3DImage")
        {
            DiagLog.Write($"render timing: renderer={renderer}, frames={frames}, avg={avgMs:F1}ms, upload={uploadMs:F1}ms, updateSurface={updateMs:F1}ms, dirty={dirtyMs:F1}ms, other={otherMs:F1}ms, replaced={replaced}");
            return;
        }

        DiagLog.Write($"render timing: renderer={renderer}, frames={frames}, avg={avgMs:F1}ms, replaced={replaced}");
    }

    private void RecordD3D11RenderWork(long startTimestamp, D3D11DirectPresentMetrics presentMetrics)
    {
        _renderTicks += Stopwatch.GetTimestamp() - startTimestamp;
        _d3d11SrvTicks += presentMetrics.SrvTicks;
        _d3d11DrawTicks += presentMetrics.DrawTicks;
        _d3dDirtyTicks += presentMetrics.DirtyTicks;
        _renderFrames++;

        var now = DateTime.UtcNow;
        if ((now - _lastRenderReportTime).TotalSeconds < 2.0)
            return;

        var frameCount = Math.Max(1, _renderFrames);
        var avgMs = TicksToAverageMs(_renderTicks, frameCount);
        var srvMs = TicksToAverageMs(_d3d11SrvTicks, frameCount);
        var drawMs = TicksToAverageMs(_d3d11DrawTicks, frameCount);
        var dirtyMs = TicksToAverageMs(_d3dDirtyTicks, frameCount);
        var otherMs = Math.Max(0, avgMs - srvMs - drawMs - dirtyMs);
        var frames = _renderFrames;
        var replaced = _replacedPendingFrames;
        _renderTicks = 0;
        _d3dUploadTicks = 0;
        _d3dUpdateSurfaceTicks = 0;
        _d3dDirtyTicks = 0;
        _d3d11SrvTicks = 0;
        _d3d11DrawTicks = 0;
        _renderFrames = 0;
        _replacedPendingFrames = 0;
        _lastRenderReportTime = now;

        DiagLog.Write($"render timing: renderer=D3D11Direct, frames={frames}, avg={avgMs:F1}ms, srv={srvMs:F1}ms, draw={drawMs:F1}ms, dirty={dirtyMs:F1}ms, other={otherMs:F1}ms, replaced={replaced}");
    }

    private static double TicksToAverageMs(long ticks, int frames) =>
        ticks * 1000.0 / Stopwatch.Frequency / Math.Max(1, frames);

    private static bool ShouldUseD3DImage()
    {
        var renderer = EnvVars.Get("MACSTREAM_RENDERER");
        return !string.Equals(renderer, "wpf", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(renderer, "writeablebitmap", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldUseD3D11Direct()
    {
        var value = EnvVars.Get("MACSTREAM_D3D11_DIRECT");
        return !string.Equals(value, "0", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        _d3dPresenter?.Dispose();
        _d3dPresenter = null;
        _bitmap = null;
        _rendererName = "none";
    }

    private sealed record PendingFrame(
        byte[] Data,
        int Offset,
        int Length,
        int SourceStride,
        bool FlipVertical,
        Action<byte[]>? ReturnBuffer,
        D3D11DecodedFrame? D3D11Frame = null)
    {
        public static PendingFrame FromD3D11(D3D11DecodedFrame frame) =>
            new(Array.Empty<byte>(), 0, 0, 0, FlipVertical: false, ReturnBuffer: null, frame);

        public void Release()
        {
            if (D3D11Frame != null)
            {
                D3D11Frame.Value.Release();
                return;
            }

            ReturnBuffer?.Invoke(Data);
        }
    }
}

public readonly struct DecodedFrameData
{
    public int Width { get; init; }
    public int Height { get; init; }
    public byte[] PixelData { get; init; }
    public int PixelDataLength { get; init; }
    public Action<byte[]>? ReturnBuffer { get; init; }
}
