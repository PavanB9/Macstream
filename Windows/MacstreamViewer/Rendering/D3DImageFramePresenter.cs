using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using MacstreamViewer.Diagnostics;
using MacstreamViewer.Video;

namespace MacstreamViewer.Rendering;

internal sealed class D3DImageFramePresenter : IDisposable
{
    private const uint D3D_SDK_VERSION = 32;
    private const uint D3DADAPTER_DEFAULT = 0;
    private const int D3DDEVTYPE_HAL = 1;
    private const int D3DCREATE_FPU_PRESERVE = 0x00000002;
    private const int D3DCREATE_MULTITHREADED = 0x00000004;
    private const int D3DCREATE_HARDWARE_VERTEXPROCESSING = 0x00000040;
    private const int D3DSWAPEFFECT_DISCARD = 1;
    private const uint D3DPRESENT_INTERVAL_IMMEDIATE = 0x80000000;
    private const int D3DFMT_A8R8G8B8 = 21;
    private const int D3DPOOL_DEFAULT = 0;
    private const int D3DPOOL_SYSTEMMEM = 2;
    private const uint D3DUSAGE_RENDERTARGET = 0x00000001;

    private readonly IntPtr _windowHandle;
    private readonly D3DImage _image = new();
    private readonly D3D11VideoRenderer _d3d11VideoRenderer = new();
    private IntPtr _d3d;
    private IntPtr _device;
    private IntPtr _renderTexture;
    private IntPtr _renderSurface;
    private IntPtr _uploadSurface;
    private IntPtr _sharedHandle;
    private int _width;
    private int _height;
    private int _stride;
    private bool _disposed;

    public D3DImage Image => _image;

    public D3DImageFramePresenter(IntPtr windowHandle)
    {
        _windowHandle = windowHandle;
    }

    public bool Initialize(int width, int height)
    {
        DisposeResources();

        _width = width;
        _height = height;
        _stride = width * 4;

        var hr = Direct3DCreate9Ex(D3D_SDK_VERSION, out _d3d);
        if (Failed(hr) || _d3d == IntPtr.Zero)
        {
            DiagLog.Write($"D3DImage init failed: Direct3DCreate9Ex hr=0x{hr:x8}");
            return false;
        }

        var presentationParameters = new D3DPRESENT_PARAMETERS
        {
            BackBufferWidth = 1,
            BackBufferHeight = 1,
            BackBufferFormat = D3DFMT_A8R8G8B8,
            BackBufferCount = 1,
            SwapEffect = D3DSWAPEFFECT_DISCARD,
            hDeviceWindow = _windowHandle,
            Windowed = 1,
            PresentationInterval = D3DPRESENT_INTERVAL_IMMEDIATE,
        };

        var behaviorFlags = D3DCREATE_FPU_PRESERVE | D3DCREATE_MULTITHREADED | D3DCREATE_HARDWARE_VERTEXPROCESSING;
        hr = CreateDeviceEx(
            _d3d,
            D3DADAPTER_DEFAULT,
            D3DDEVTYPE_HAL,
            _windowHandle,
            behaviorFlags,
            ref presentationParameters,
            IntPtr.Zero,
            out _device);
        if (Failed(hr) || _device == IntPtr.Zero)
        {
            DiagLog.Write($"D3DImage init failed: CreateDeviceEx hr=0x{hr:x8}");
            DisposeResources();
            return false;
        }

        if (!TryCreateRenderTexture(width, height, preferShared: true)
            && !TryCreateRenderTexture(width, height, preferShared: false))
        {
            DisposeResources();
            return false;
        }

        hr = GetSurfaceLevel(_renderTexture, 0, out _renderSurface);
        if (Failed(hr) || _renderSurface == IntPtr.Zero)
        {
            DiagLog.Write($"D3DImage init failed: GetSurfaceLevel hr=0x{hr:x8}");
            DisposeResources();
            return false;
        }

        hr = CreateOffscreenPlainSurface(
            _device,
            (uint)width,
            (uint)height,
            D3DFMT_A8R8G8B8,
            D3DPOOL_SYSTEMMEM,
            out _uploadSurface,
            IntPtr.Zero);
        if (Failed(hr) || _uploadSurface == IntPtr.Zero)
        {
            DiagLog.Write($"D3DImage init failed: CreateOffscreenPlainSurface hr=0x{hr:x8}");
            DisposeResources();
            return false;
        }

        _image.Lock();
        try
        {
            _image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, _renderSurface, false);
        }
        finally
        {
            _image.Unlock();
        }

        DiagLog.Write($"D3DImage renderer initialized: {width}x{height}");
        return true;
    }

    private unsafe bool TryCreateRenderTexture(int width, int height, bool preferShared)
    {
        var sharedHandle = IntPtr.Zero;
        var sharedHandlePtr = preferShared ? (IntPtr)(&sharedHandle) : IntPtr.Zero;
        var hr = CreateTexture(
            _device,
            (uint)width,
            (uint)height,
            1,
            D3DUSAGE_RENDERTARGET,
            D3DFMT_A8R8G8B8,
            D3DPOOL_DEFAULT,
            out _renderTexture,
            sharedHandlePtr);

        if (Failed(hr) || _renderTexture == IntPtr.Zero)
        {
            DiagLog.Write($"D3DImage init failed: CreateTexture shared={preferShared} hr=0x{hr:x8}");
            _renderTexture = IntPtr.Zero;
            return false;
        }

        _sharedHandle = preferShared ? sharedHandle : IntPtr.Zero;
        if (preferShared && _sharedHandle == IntPtr.Zero)
            DiagLog.Write("D3DImage shared texture created without a shared handle; D3D11 direct output unavailable");

        return true;
    }

    public unsafe bool Present(
        byte[] data,
        int offset,
        int length,
        int sourceStride,
        bool flipVertical,
        out D3DImagePresentMetrics metrics)
    {
        metrics = default;

        if (_device == IntPtr.Zero || _uploadSurface == IntPtr.Zero || _renderSurface == IntPtr.Zero)
            return false;

        if (data.Length - offset < Math.Min(length, sourceStride * _height))
            return false;

        var uploadStart = Stopwatch.GetTimestamp();
        var hr = LockRect(_uploadSurface, out var lockedRect, IntPtr.Zero, 0);
        if (Failed(hr) || lockedRect.pBits == IntPtr.Zero)
        {
            DiagLog.Write($"D3DImage LockRect failed: hr=0x{hr:x8}");
            return false;
        }

        try
        {
            fixed (byte* sourceBase = data)
            {
                var source = sourceBase + offset;
                var destination = (byte*)lockedRect.pBits;
                var rowBytes = _stride;

                if (!flipVertical && lockedRect.Pitch == sourceStride && sourceStride == rowBytes)
                {
                    Buffer.MemoryCopy(source, destination, lockedRect.Pitch * _height, rowBytes * _height);
                }
                else
                {
                    for (var y = 0; y < _height; y++)
                    {
                        var sourceY = flipVertical ? _height - 1 - y : y;
                        Buffer.MemoryCopy(
                            source + sourceY * sourceStride,
                            destination + y * lockedRect.Pitch,
                            lockedRect.Pitch,
                            rowBytes);
                    }
                }
            }
        }
        finally
        {
            _ = UnlockRect(_uploadSurface);
        }
        metrics = metrics with { UploadTicks = Stopwatch.GetTimestamp() - uploadStart };

        var updateStart = Stopwatch.GetTimestamp();
        hr = UpdateSurface(_device, _uploadSurface, IntPtr.Zero, _renderSurface, IntPtr.Zero);
        metrics = metrics with { UpdateSurfaceTicks = Stopwatch.GetTimestamp() - updateStart };
        if (Failed(hr))
        {
            DiagLog.Write($"D3DImage UpdateSurface failed: hr=0x{hr:x8}");
            return false;
        }

        if (_image.IsFrontBufferAvailable)
        {
            var dirtyStart = Stopwatch.GetTimestamp();
            _image.Lock();
            try
            {
                _image.AddDirtyRect(new Int32Rect(0, 0, _width, _height));
            }
            finally
            {
                _image.Unlock();
            }
            metrics = metrics with { DirtyTicks = Stopwatch.GetTimestamp() - dirtyStart };
        }

        return true;
    }

    public bool PresentD3D11Frame(D3D11DecodedFrame frame, out D3D11DirectPresentMetrics metrics)
    {
        metrics = default;

        try
        {
            if (_renderSurface == IntPtr.Zero || _sharedHandle == IntPtr.Zero)
                return false;

            if (!_d3d11VideoRenderer.Initialize(frame.Device, frame.DeviceContext, _sharedHandle, _width, _height))
                return false;

            if (!_d3d11VideoRenderer.Present(frame, out metrics))
                return false;

            if (_image.IsFrontBufferAvailable)
            {
                var dirtyStart = Stopwatch.GetTimestamp();
                _image.Lock();
                try
                {
                    _image.AddDirtyRect(new Int32Rect(0, 0, _width, _height));
                }
                finally
                {
                    _image.Unlock();
                }

                metrics = metrics with { DirtyTicks = Stopwatch.GetTimestamp() - dirtyStart };
            }

            return true;
        }
        catch (Exception ex)
        {
            DiagLog.Write(ex, "D3D11 direct render error");
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        DisposeResources();
        GC.SuppressFinalize(this);
    }

    private void DisposeResources()
    {
        _d3d11VideoRenderer.Dispose();

        if (_image.Dispatcher.CheckAccess())
        {
            _image.Lock();
            try
            {
                _image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, IntPtr.Zero);
            }
            finally
            {
                _image.Unlock();
            }
        }

        ReleaseCom(ref _uploadSurface);
        ReleaseCom(ref _renderSurface);
        ReleaseCom(ref _renderTexture);
        ReleaseCom(ref _device);
        ReleaseCom(ref _d3d);
        _sharedHandle = IntPtr.Zero;
    }

    private static bool Failed(int hr) => hr < 0;

    private static void ReleaseCom(ref IntPtr unknown)
    {
        if (unknown == IntPtr.Zero)
            return;

        _ = Marshal.Release(unknown);
        unknown = IntPtr.Zero;
    }

    private static T GetMethod<T>(IntPtr comObject, int slot) where T : Delegate
    {
        var vtable = Marshal.ReadIntPtr(comObject);
        var method = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(method);
    }

    private static int CreateDeviceEx(
        IntPtr direct3D,
        uint adapter,
        int deviceType,
        IntPtr focusWindow,
        int behaviorFlags,
        ref D3DPRESENT_PARAMETERS presentationParameters,
        IntPtr fullscreenDisplayMode,
        out IntPtr device)
    {
        var createDeviceEx = GetMethod<CreateDeviceExDelegate>(direct3D, 20);
        return createDeviceEx(
            direct3D,
            adapter,
            deviceType,
            focusWindow,
            behaviorFlags,
            ref presentationParameters,
            fullscreenDisplayMode,
            out device);
    }

    private static int CreateTexture(
        IntPtr device,
        uint width,
        uint height,
        uint levels,
        uint usage,
        int format,
        int pool,
        out IntPtr texture,
        IntPtr sharedHandle)
    {
        var createTexture = GetMethod<CreateTextureDelegate>(device, 23);
        return createTexture(device, width, height, levels, usage, format, pool, out texture, sharedHandle);
    }

    private static int CreateOffscreenPlainSurface(
        IntPtr device,
        uint width,
        uint height,
        int format,
        int pool,
        out IntPtr surface,
        IntPtr sharedHandle)
    {
        var createOffscreenPlainSurface = GetMethod<CreateOffscreenPlainSurfaceDelegate>(device, 36);
        return createOffscreenPlainSurface(device, width, height, format, pool, out surface, sharedHandle);
    }

    private static int UpdateSurface(
        IntPtr device,
        IntPtr sourceSurface,
        IntPtr sourceRect,
        IntPtr destinationSurface,
        IntPtr destinationPoint)
    {
        var updateSurface = GetMethod<UpdateSurfaceDelegate>(device, 30);
        return updateSurface(device, sourceSurface, sourceRect, destinationSurface, destinationPoint);
    }

    private static int GetSurfaceLevel(IntPtr texture, uint level, out IntPtr surface)
    {
        var getSurfaceLevel = GetMethod<GetSurfaceLevelDelegate>(texture, 18);
        return getSurfaceLevel(texture, level, out surface);
    }

    private static int LockRect(IntPtr surface, out D3DLOCKED_RECT lockedRect, IntPtr rect, uint flags)
    {
        var lockRect = GetMethod<LockRectDelegate>(surface, 13);
        return lockRect(surface, out lockedRect, rect, flags);
    }

    private static int UnlockRect(IntPtr surface)
    {
        var unlockRect = GetMethod<UnlockRectDelegate>(surface, 14);
        return unlockRect(surface);
    }

    [DllImport("d3d9.dll", ExactSpelling = true)]
    private static extern int Direct3DCreate9Ex(uint sdkVersion, out IntPtr direct3D);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateDeviceExDelegate(
        IntPtr self,
        uint adapter,
        int deviceType,
        IntPtr focusWindow,
        int behaviorFlags,
        ref D3DPRESENT_PARAMETERS presentationParameters,
        IntPtr fullscreenDisplayMode,
        out IntPtr returnedDeviceInterface);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateTextureDelegate(
        IntPtr self,
        uint width,
        uint height,
        uint levels,
        uint usage,
        int format,
        int pool,
        out IntPtr texture,
        IntPtr sharedHandle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateOffscreenPlainSurfaceDelegate(
        IntPtr self,
        uint width,
        uint height,
        int format,
        int pool,
        out IntPtr surface,
        IntPtr sharedHandle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int UpdateSurfaceDelegate(
        IntPtr self,
        IntPtr sourceSurface,
        IntPtr sourceRect,
        IntPtr destinationSurface,
        IntPtr destinationPoint);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetSurfaceLevelDelegate(IntPtr self, uint level, out IntPtr surface);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int LockRectDelegate(IntPtr self, out D3DLOCKED_RECT lockedRect, IntPtr rect, uint flags);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int UnlockRectDelegate(IntPtr self);

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DPRESENT_PARAMETERS
    {
        public uint BackBufferWidth;
        public uint BackBufferHeight;
        public int BackBufferFormat;
        public uint BackBufferCount;
        public int MultiSampleType;
        public uint MultiSampleQuality;
        public int SwapEffect;
        public IntPtr hDeviceWindow;
        public int Windowed;
        public int EnableAutoDepthStencil;
        public int AutoDepthStencilFormat;
        public uint Flags;
        public uint FullScreenRefreshRateInHz;
        public uint PresentationInterval;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DLOCKED_RECT
    {
        public int Pitch;
        public IntPtr pBits;
    }
}

internal readonly record struct D3DImagePresentMetrics(long UploadTicks, long UpdateSurfaceTicks, long DirtyTicks);
