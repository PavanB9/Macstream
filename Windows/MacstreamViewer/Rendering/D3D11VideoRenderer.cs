using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using MacstreamViewer.Diagnostics;
using MacstreamViewer.Video;

namespace MacstreamViewer.Rendering;

internal sealed class D3D11VideoRenderer : IDisposable
{
    private static readonly Guid IidD3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    private static readonly Guid IidDxgiDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    private static readonly Guid IidDxgiDevice1 = new("77db970f-6276-48ba-ba28-070143b4392c");
    private static readonly Guid IidDxgiFactory2 = new("50c83a1c-e072-4c48-87b0-3630fa36a6d0");
    private static readonly Guid IidDxgiFactory5 = new("7632e1f5-ee65-4dca-87fd-84cd75f8838d");

    private const int DxgiFormatB8G8R8A8Unorm = 87;
    private const uint DxgiUsageRenderTargetOutput = 0x20;
    private const int DxgiScalingNone = 1;
    private const int DxgiSwapEffectFlipDiscard = 4;
    private const uint DxgiSwapChainFlagAllowTearing = 0x800;
    private const uint DxgiPresentAllowTearing = 0x200;
    private const uint DxgiMwaNoAltEnter = 0x2;
    private const int DxgiFeaturePresentAllowTearing = 0;

    private const int DxgiFormatR8G8Unorm = 49;
    private const int DxgiFormatR8Unorm = 61;
    private const int D3D11SrvDimensionTexture2DArray = 5;
    private const uint D3D11PrimitiveTopologyTriangleList = 4;
    private const int D3D11FilterMinMagMipLinear = 0x15;
    private const int D3D11TextureAddressClamp = 3;
    private const int D3D11ComparisonAlways = 8;

    private IntPtr _device;
    private IntPtr _deviceContext;
    private IntPtr _sharedHandle;
    private IntPtr _sharedTexture;
    private IntPtr _renderTargetView;
    private IntPtr _vertexShader;
    private IntPtr _pixelShader;
    private IntPtr _samplerState;
    private int _width;
    private int _height;

    // Swap-chain mode (video presented straight into a native child window).
    private IntPtr _swapChain;
    private IntPtr _swapChainHwnd;
    private int _backBufferWidth;
    private int _backBufferHeight;
    private int _requestedWidth;
    private int _requestedHeight;
    private bool _allowTearing;
    private uint _swapChainFlags;

    public bool Initialize(IntPtr device, IntPtr deviceContext, IntPtr sharedHandle, int width, int height)
    {
        if (device == IntPtr.Zero || deviceContext == IntPtr.Zero || sharedHandle == IntPtr.Zero)
            return false;

        if (_device == device
            && _deviceContext == deviceContext
            && _sharedHandle == sharedHandle
            && _width == width
            && _height == height
            && _renderTargetView != IntPtr.Zero)
        {
            return true;
        }

        DisposeResources();

        _device = device;
        _deviceContext = deviceContext;
        _sharedHandle = sharedHandle;
        _width = width;
        _height = height;

        var textureIid = IidD3D11Texture2D;
        var hr = OpenSharedResource(_device, sharedHandle, ref textureIid, out _sharedTexture);
        if (Failed(hr) || _sharedTexture == IntPtr.Zero)
        {
            DiagLog.Write($"D3D11 direct init failed: OpenSharedResource hr=0x{hr:x8}");
            DisposeResources();
            return false;
        }

        hr = CreateRenderTargetView(_device, _sharedTexture, IntPtr.Zero, out _renderTargetView);
        if (Failed(hr) || _renderTargetView == IntPtr.Zero)
        {
            DiagLog.Write($"D3D11 direct init failed: CreateRenderTargetView hr=0x{hr:x8}");
            DisposeResources();
            return false;
        }

        if (!CreateShaders() || !CreateSampler())
        {
            DisposeResources();
            return false;
        }

        DiagLog.Write($"D3D11 direct renderer initialized: {width}x{height}, shared=0x{sharedHandle:x}");
        return true;
    }

    public bool Present(D3D11DecodedFrame frame, out D3D11DirectPresentMetrics metrics)
    {
        metrics = default;

        if (_device == IntPtr.Zero
            || _deviceContext == IntPtr.Zero
            || _renderTargetView == IntPtr.Zero
            || _vertexShader == IntPtr.Zero
            || _pixelShader == IntPtr.Zero
            || _samplerState == IntPtr.Zero
            || frame.Texture == IntPtr.Zero)
        {
            return false;
        }

        var srvStart = Stopwatch.GetTimestamp();
        if (!CreatePlaneShaderResourceViews(frame, out var yView, out var uvView))
            return false;

        metrics = metrics with { SrvTicks = Stopwatch.GetTimestamp() - srvStart };

        try
        {
            var drawStart = Stopwatch.GetTimestamp();
            if (_swapChain != IntPtr.Zero)
            {
                if (!PresentToSwapChain(frame, yView, uvView))
                    return false;
            }
            else
            {
                DrawFrame(yView, uvView, new D3D11_VIEWPORT { Width = _width, Height = _height, MinDepth = 0, MaxDepth = 1 }, clear: false);
                Flush(_deviceContext);
            }
            metrics = metrics with { DrawTicks = Stopwatch.GetTimestamp() - drawStart };
            return true;
        }
        finally
        {
            UnbindShaderResourceViews();
            ReleaseCom(ref uvView);
            ReleaseCom(ref yView);
        }
    }

    private bool CreatePlaneShaderResourceViews(D3D11DecodedFrame frame, out IntPtr yView, out IntPtr uvView)
    {
        yView = IntPtr.Zero;
        uvView = IntPtr.Zero;

        var yDesc = ShaderResourceViewDesc(DxgiFormatR8Unorm, frame.ArraySlice);
        var hr = CreateShaderResourceView(_device, frame.Texture, ref yDesc, out yView);
        if (Failed(hr) || yView == IntPtr.Zero)
        {
            DiagLog.Write($"D3D11 direct failed: creating the Y plane SRV failed hr=0x{hr:x8}, slice={frame.ArraySlice}");
            return false;
        }

        var uvDesc = ShaderResourceViewDesc(DxgiFormatR8G8Unorm, frame.ArraySlice);
        hr = CreateShaderResourceView(_device, frame.Texture, ref uvDesc, out uvView);
        if (Failed(hr) || uvView == IntPtr.Zero)
        {
            DiagLog.Write($"D3D11 direct failed: creating the UV plane SRV failed hr=0x{hr:x8}, slice={frame.ArraySlice}");
            ReleaseCom(ref yView);
            return false;
        }

        return true;
    }

    private static D3D11_SHADER_RESOURCE_VIEW_DESC ShaderResourceViewDesc(int format, int arraySlice) => new()
    {
        Format = format,
        ViewDimension = D3D11SrvDimensionTexture2DArray,
        MostDetailedMip = 0,
        MipLevels = 1,
        FirstArraySlice = unchecked((uint)arraySlice),
        ArraySize = 1,
    };

    private unsafe void DrawFrame(IntPtr yView, IntPtr uvView, D3D11_VIEWPORT viewport, bool clear)
    {
        if (clear)
        {
            var black = stackalloc float[4] { 0f, 0f, 0f, 1f };
            ClearRenderTargetView(_deviceContext, _renderTargetView, (IntPtr)black);
        }

        var renderTargets = stackalloc IntPtr[1];
        renderTargets[0] = _renderTargetView;
        var shaderResources = stackalloc IntPtr[2];
        shaderResources[0] = yView;
        shaderResources[1] = uvView;
        var samplers = stackalloc IntPtr[1];
        samplers[0] = _samplerState;

        RSSetViewports(_deviceContext, 1, ref viewport);
        OMSetRenderTargets(_deviceContext, 1, (IntPtr)renderTargets, IntPtr.Zero);
        IASetPrimitiveTopology(_deviceContext, D3D11PrimitiveTopologyTriangleList);
        VSSetShader(_deviceContext, _vertexShader, IntPtr.Zero, 0);
        PSSetShader(_deviceContext, _pixelShader, IntPtr.Zero, 0);
        PSSetShaderResources(_deviceContext, 0, 2, (IntPtr)shaderResources);
        PSSetSamplers(_deviceContext, 0, 1, (IntPtr)samplers);
        Draw(_deviceContext, 3, 0);
    }

    private unsafe void UnbindShaderResourceViews()
    {
        var empty = stackalloc IntPtr[2];
        PSSetShaderResources(_deviceContext, 0, 2, (IntPtr)empty);
    }

    // ---------------------------------------------------------------------------------------
    // Swap-chain mode: the video is drawn straight into a native child window through a DXGI
    // flip-model swap chain, scaled by our own shader, and presented immediately. This skips the
    // shared-texture -> D3D9 -> WPF D3DImage -> WPF composition hop (1-2 refreshes of latency),
    // and makes the Catmull-Rom filter the final scaling step instead of WPF's bitmap scaler.
    // ---------------------------------------------------------------------------------------

    public bool IsSwapChainMode => _swapChain != IntPtr.Zero;

    public bool InitializeSwapChain(IntPtr device, IntPtr deviceContext, IntPtr hwnd, int videoWidth, int videoHeight, int targetWidth, int targetHeight)
    {
        if (device == IntPtr.Zero || deviceContext == IntPtr.Zero || hwnd == IntPtr.Zero)
            return false;

        targetWidth = Math.Max(1, targetWidth);
        targetHeight = Math.Max(1, targetHeight);

        if (_swapChain != IntPtr.Zero && _device == device && _deviceContext == deviceContext && _swapChainHwnd == hwnd)
        {
            _width = videoWidth;
            _height = videoHeight;
            return true;
        }

        DisposeResources();
        _device = device;
        _deviceContext = deviceContext;
        _swapChainHwnd = hwnd;
        _width = videoWidth;
        _height = videoHeight;

        if (!CreateSwapChain(targetWidth, targetHeight) || !CreateBackBufferView() || !CreateShaders() || !CreateSampler())
        {
            DisposeResources();
            return false;
        }

        DiagLog.Write($"Swap chain renderer ready: video {videoWidth}x{videoHeight}, window {targetWidth}x{targetHeight}, tearing={_allowTearing}");
        return true;
    }

    /// <summary>Thread-safe: the next Present resizes the back buffers to the window size.</summary>
    public void RequestResize(int width, int height)
    {
        Volatile.Write(ref _requestedWidth, Math.Max(1, width));
        Volatile.Write(ref _requestedHeight, Math.Max(1, height));
    }

    private bool CreateSwapChain(int width, int height)
    {
        IntPtr dxgiDevice = IntPtr.Zero, adapter = IntPtr.Zero, factory = IntPtr.Zero, factory5 = IntPtr.Zero, dxgiDevice1 = IntPtr.Zero;
        try
        {
            var iid = IidDxgiDevice;
            if (Marshal.QueryInterface(_device, in iid, out dxgiDevice) < 0)
                return LogFail("QueryInterface IDXGIDevice", 0);

            var hr = GetMethod<GetAdapterDelegate>(dxgiDevice, 7)(dxgiDevice, out adapter);
            if (Failed(hr)) return LogFail("IDXGIDevice.GetAdapter", hr);

            var factoryIid = IidDxgiFactory2;
            hr = GetMethod<GetParentDelegate>(adapter, 6)(adapter, ref factoryIid, out factory);
            if (Failed(hr)) return LogFail("IDXGIAdapter.GetParent(IDXGIFactory2)", hr);

            _allowTearing = false;
            var factory5Iid = IidDxgiFactory5;
            if (Marshal.QueryInterface(factory, in factory5Iid, out factory5) >= 0)
            {
                var supported = 0;
                hr = GetMethod<CheckFeatureSupportDelegate>(factory5, 28)(factory5, DxgiFeaturePresentAllowTearing, ref supported, sizeof(int));
                _allowTearing = !Failed(hr) && supported != 0 && !UseVsync();
            }

            _swapChainFlags = _allowTearing ? DxgiSwapChainFlagAllowTearing : 0;
            var desc = new DXGI_SWAP_CHAIN_DESC1
            {
                Width = (uint)width,
                Height = (uint)height,
                Format = DxgiFormatB8G8R8A8Unorm,
                SampleCount = 1,
                BufferUsage = DxgiUsageRenderTargetOutput,
                BufferCount = 2,
                Scaling = DxgiScalingNone,
                SwapEffect = DxgiSwapEffectFlipDiscard,
                Flags = _swapChainFlags,
            };
            hr = GetMethod<CreateSwapChainForHwndDelegate>(factory, 15)(factory, _device, _swapChainHwnd, ref desc, IntPtr.Zero, IntPtr.Zero, out _swapChain);
            if (Failed(hr) || _swapChain == IntPtr.Zero) return LogFail("CreateSwapChainForHwnd", hr);

            _ = GetMethod<MakeWindowAssociationDelegate>(factory, 8)(factory, _swapChainHwnd, DxgiMwaNoAltEnter);

            // Keep at most one frame queued in the GPU so presentation never builds up delay.
            var device1Iid = IidDxgiDevice1;
            if (Marshal.QueryInterface(_device, in device1Iid, out dxgiDevice1) >= 0)
                _ = GetMethod<SetMaximumFrameLatencyDelegate>(dxgiDevice1, 12)(dxgiDevice1, 1);

            _backBufferWidth = width;
            _backBufferHeight = height;
            Volatile.Write(ref _requestedWidth, width);
            Volatile.Write(ref _requestedHeight, height);
            return true;
        }
        finally
        {
            ReleaseCom(ref dxgiDevice1);
            ReleaseCom(ref factory5);
            ReleaseCom(ref factory);
            ReleaseCom(ref adapter);
            ReleaseCom(ref dxgiDevice);
        }
    }

    private bool CreateBackBufferView()
    {
        var iid = IidD3D11Texture2D;
        var hr = GetMethod<GetBufferDelegate>(_swapChain, 9)(_swapChain, 0, ref iid, out var backBuffer);
        if (Failed(hr) || backBuffer == IntPtr.Zero) return LogFail("IDXGISwapChain.GetBuffer", hr);
        try
        {
            hr = CreateRenderTargetView(_device, backBuffer, IntPtr.Zero, out _renderTargetView);
            if (Failed(hr) || _renderTargetView == IntPtr.Zero)
                return LogFail("CreateRenderTargetView(back buffer)", hr);
            return true;
        }
        finally
        {
            ReleaseCom(ref backBuffer);
        }
    }

    private bool ApplyPendingResize()
    {
        var width = Volatile.Read(ref _requestedWidth);
        var height = Volatile.Read(ref _requestedHeight);
        if (width <= 0 || height <= 0 || (width == _backBufferWidth && height == _backBufferHeight))
            return true;

        // All references to the back buffers must be gone before ResizeBuffers.
        OMSetRenderTargets(_deviceContext, 0, IntPtr.Zero, IntPtr.Zero);
        ReleaseCom(ref _renderTargetView);
        Flush(_deviceContext);

        var hr = GetMethod<ResizeBuffersDelegate>(_swapChain, 13)(_swapChain, 0, (uint)width, (uint)height, 0, _swapChainFlags);
        if (Failed(hr)) return LogFail("IDXGISwapChain.ResizeBuffers", hr);

        _backBufferWidth = width;
        _backBufferHeight = height;
        return CreateBackBufferView();
    }

    private bool PresentToSwapChain(D3D11DecodedFrame frame, IntPtr yView, IntPtr uvView)
    {
        if (!ApplyPendingResize())
            return false;

        // Letterbox: largest rectangle with the video's aspect ratio, centred in the window.
        var videoWidth = frame.Width > 0 ? frame.Width : _width;
        var videoHeight = frame.Height > 0 ? frame.Height : _height;
        var scale = Math.Min(_backBufferWidth / (double)videoWidth, _backBufferHeight / (double)videoHeight);
        var drawWidth = Math.Max(1, Math.Round(videoWidth * scale));
        var drawHeight = Math.Max(1, Math.Round(videoHeight * scale));
        var viewport = new D3D11_VIEWPORT
        {
            TopLeftX = (float)Math.Floor((_backBufferWidth - drawWidth) / 2),
            TopLeftY = (float)Math.Floor((_backBufferHeight - drawHeight) / 2),
            Width = (float)drawWidth,
            Height = (float)drawHeight,
            MinDepth = 0,
            MaxDepth = 1,
        };

        DrawFrame(yView, uvView, viewport, clear: true);

        var syncInterval = _allowTearing ? 0u : (UseVsync() ? 1u : 0u);
        var flags = _allowTearing ? DxgiPresentAllowTearing : 0u;
        var hr = GetMethod<PresentDelegate>(_swapChain, 8)(_swapChain, syncInterval, flags);
        // DXGI_STATUS_OCCLUDED (0x087A0001) is a success code (window minimised/covered).
        if (Failed(hr))
            return LogFail("IDXGISwapChain.Present", hr);
        return true;
    }

    private static bool UseVsync() =>
        EnvVars.Get("MACSTREAM_VSYNC") == "1";

    private static bool LogFail(string what, int hr)
    {
        DiagLog.Write($"Swap chain renderer: {what} failed hr=0x{hr:x8}");
        return false;
    }

    private bool CreateShaders()
    {
        var vertexShaderSource = """
            struct VSOut
            {
                float4 position : SV_POSITION;
                float2 texCoord : TEXCOORD0;
            };

            VSOut main(uint vertexId : SV_VertexID)
            {
                VSOut output;
                float2 positions[3] = {
                    float2(-1.0, -1.0),
                    float2(-1.0,  3.0),
                    float2( 3.0, -1.0)
                };
                float2 texCoords[3] = {
                    float2(0.0, 1.0),
                    float2(0.0, -1.0),
                    float2(2.0, 1.0)
                };
                output.position = float4(positions[vertexId], 0.0, 1.0);
                output.texCoord = texCoords[vertexId];
                return output;
            }
            """;

        var useFullRange = ShouldUseFullColorRange();
        var sharpScaler = !string.Equals(EnvVars.Get("MACSTREAM_SCALER"), "bilinear", StringComparison.OrdinalIgnoreCase);
        var pixelShaderSource = $"#define SHARP_SCALER {(sharpScaler ? 1 : 0)}\n" + LumaSamplingSource +
            (useFullRange ? FullRangePixelShaderSource : LimitedRangePixelShaderSource);
        DiagLog.Write($"D3D11 direct color conversion: BT.709 {(useFullRange ? "full" : "limited")} range, scaler={(sharpScaler ? "catmull-rom" : "bilinear")}");

        return CompileShader(vertexShaderSource, "vs_4_0", out _vertexShader, CreateVertexShader)
            && CompileShader(pixelShaderSource, "ps_4_0", out _pixelShader, CreatePixelShader);
    }

    // Shared declarations + luma sampling. With SHARP_SCALER, luma uses a Catmull-Rom bicubic
    // filter (9 bilinear taps) instead of plain bilinear, so text edges stay crisper when the
    // Mac picture is scaled to the window (e.g. 1664 rows onto a 1440p monitor). Chroma stays
    // bilinear: it is half resolution anyway and the eye is far less sensitive to it.
    private const string LumaSamplingSource = """
        Texture2DArray<float> yPlane : register(t0);
        Texture2DArray<float2> uvPlane : register(t1);
        SamplerState linearSampler : register(s0);

        float LumaTap(float2 uv)
        {
            return yPlane.SampleLevel(linearSampler, float3(uv, 0.0), 0).r;
        }

        float SampleLuma(float2 uv)
        {
        #if SHARP_SCALER
            float width, height, elements;
            yPlane.GetDimensions(width, height, elements);
            float2 texSize = float2(width, height);
            float2 samplePos = uv * texSize;
            float2 texPos1 = floor(samplePos - 0.5) + 0.5;
            float2 f = samplePos - texPos1;
            float2 w0 = f * (-0.5 + f * (1.0 - 0.5 * f));
            float2 w1 = 1.0 + f * f * (-2.5 + 1.5 * f);
            float2 w2 = f * (0.5 + f * (2.0 - 1.5 * f));
            float2 w3 = f * f * (-0.5 + 0.5 * f);
            float2 w12 = w1 + w2;
            float2 tc0 = (texPos1 - 1.0) / texSize;
            float2 tc3 = (texPos1 + 2.0) / texSize;
            float2 tc12 = (texPos1 + w2 / w12) / texSize;
            return
                LumaTap(float2(tc0.x, tc0.y)) * w0.x * w0.y +
                LumaTap(float2(tc12.x, tc0.y)) * w12.x * w0.y +
                LumaTap(float2(tc3.x, tc0.y)) * w3.x * w0.y +
                LumaTap(float2(tc0.x, tc12.y)) * w0.x * w12.y +
                LumaTap(float2(tc12.x, tc12.y)) * w12.x * w12.y +
                LumaTap(float2(tc3.x, tc12.y)) * w3.x * w12.y +
                LumaTap(float2(tc0.x, tc3.y)) * w0.x * w3.y +
                LumaTap(float2(tc12.x, tc3.y)) * w12.x * w3.y +
                LumaTap(float2(tc3.x, tc3.y)) * w3.x * w3.y;
        #else
            return LumaTap(uv);
        #endif
        }

        """;

    private const string LimitedRangePixelShaderSource = """
        float4 main(float4 position : SV_POSITION, float2 texCoord : TEXCOORD0) : SV_TARGET
        {
            float ySample = SampleLuma(texCoord);
            float2 uvSample = uvPlane.Sample(linearSampler, float3(texCoord, 0.0)).rg;
            float y = saturate((ySample - 16.0 / 255.0) * (255.0 / 219.0));
            float2 uv = (uvSample - float2(128.0 / 255.0, 128.0 / 255.0)) * (255.0 / 224.0);
            float r = y + 1.5748 * uv.y;
            float g = y - 0.1873 * uv.x - 0.4681 * uv.y;
            float b = y + 1.8556 * uv.x;
            return float4(saturate(r), saturate(g), saturate(b), 1.0);
        }
        """;

    private const string FullRangePixelShaderSource = """
        float4 main(float4 position : SV_POSITION, float2 texCoord : TEXCOORD0) : SV_TARGET
        {
            float y = saturate(SampleLuma(texCoord));
            float2 uv = uvPlane.Sample(linearSampler, float3(texCoord, 0.0)).rg - float2(0.5, 0.5);
            float r = y + 1.5748 * uv.y;
            float g = y - 0.1873 * uv.x - 0.4681 * uv.y;
            float b = y + 1.8556 * uv.x;
            return float4(saturate(r), saturate(g), saturate(b), 1.0);
        }
        """;

    private static bool ShouldUseFullColorRange()
    {
        var range = EnvVars.Get("MACSTREAM_D3D11_COLOR_RANGE");
        return string.Equals(range, "full", StringComparison.OrdinalIgnoreCase);
    }

    private delegate int CreateShaderFromBlob(IntPtr device, IntPtr bytecode, nuint bytecodeLength, IntPtr classLinkage, out IntPtr shader);

    private bool CompileShader(string source, string target, out IntPtr shader, CreateShaderFromBlob createShader)
    {
        shader = IntPtr.Zero;
        var hr = D3DCompile(
            source,
            (nuint)Encoding.ASCII.GetByteCount(source),
            null,
            IntPtr.Zero,
            IntPtr.Zero,
            "main",
            target,
            0,
            0,
            out var blob,
            out var errorBlob);

        try
        {
            if (Failed(hr) || blob == IntPtr.Zero)
            {
                var error = errorBlob == IntPtr.Zero ? "<none>" : Marshal.PtrToStringAnsi(GetBlobPointer(errorBlob));
                DiagLog.Write($"D3D11 direct init failed: D3DCompile {target} hr=0x{hr:x8}, error={error}");
                return false;
            }

            hr = createShader(_device, GetBlobPointer(blob), GetBlobSize(blob), IntPtr.Zero, out shader);
            if (Failed(hr) || shader == IntPtr.Zero)
            {
                DiagLog.Write($"D3D11 direct init failed: CreateShader {target} hr=0x{hr:x8}");
                return false;
            }

            return true;
        }
        finally
        {
            ReleaseCom(ref errorBlob);
            ReleaseCom(ref blob);
        }
    }

    private bool CreateSampler()
    {
        var samplerDesc = new D3D11_SAMPLER_DESC
        {
            Filter = D3D11FilterMinMagMipLinear,
            AddressU = D3D11TextureAddressClamp,
            AddressV = D3D11TextureAddressClamp,
            AddressW = D3D11TextureAddressClamp,
            ComparisonFunc = D3D11ComparisonAlways,
            MaxLOD = float.MaxValue,
        };

        var hr = CreateSamplerState(_device, ref samplerDesc, out _samplerState);
        if (Failed(hr) || _samplerState == IntPtr.Zero)
        {
            DiagLog.Write($"D3D11 direct init failed: CreateSamplerState hr=0x{hr:x8}");
            return false;
        }

        return true;
    }

    public void Dispose()
    {
        DisposeResources();
        GC.SuppressFinalize(this);
    }

    private void DisposeResources()
    {
        ReleaseCom(ref _samplerState);
        ReleaseCom(ref _pixelShader);
        ReleaseCom(ref _vertexShader);
        if (_deviceContext != IntPtr.Zero && _renderTargetView != IntPtr.Zero)
            OMSetRenderTargets(_deviceContext, 0, IntPtr.Zero, IntPtr.Zero);
        ReleaseCom(ref _renderTargetView);
        ReleaseCom(ref _sharedTexture);
        ReleaseCom(ref _swapChain);
        _swapChainHwnd = IntPtr.Zero;
        _backBufferWidth = 0;
        _backBufferHeight = 0;
        _device = IntPtr.Zero;
        _deviceContext = IntPtr.Zero;
        _sharedHandle = IntPtr.Zero;
        _width = 0;
        _height = 0;
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

    private static int OpenSharedResource(IntPtr device, IntPtr sharedHandle, ref Guid iid, out IntPtr resource)
    {
        var openSharedResource = GetMethod<OpenSharedResourceDelegate>(device, 28);
        return openSharedResource(device, sharedHandle, ref iid, out resource);
    }

    private static int CreateRenderTargetView(IntPtr device, IntPtr resource, IntPtr desc, out IntPtr renderTargetView)
    {
        var createRenderTargetView = GetMethod<CreateRenderTargetViewDelegate>(device, 9);
        return createRenderTargetView(device, resource, desc, out renderTargetView);
    }

    private static int CreateShaderResourceView(
        IntPtr device,
        IntPtr resource,
        ref D3D11_SHADER_RESOURCE_VIEW_DESC desc,
        out IntPtr shaderResourceView)
    {
        var createShaderResourceView = GetMethod<CreateShaderResourceViewDelegate>(device, 7);
        return createShaderResourceView(device, resource, ref desc, out shaderResourceView);
    }

    private static int CreateVertexShader(IntPtr device, IntPtr bytecode, nuint bytecodeLength, IntPtr classLinkage, out IntPtr shader)
    {
        var createVertexShader = GetMethod<CreateShaderDelegate>(device, 12);
        return createVertexShader(device, bytecode, bytecodeLength, classLinkage, out shader);
    }

    private static int CreatePixelShader(IntPtr device, IntPtr bytecode, nuint bytecodeLength, IntPtr classLinkage, out IntPtr shader)
    {
        var createPixelShader = GetMethod<CreateShaderDelegate>(device, 15);
        return createPixelShader(device, bytecode, bytecodeLength, classLinkage, out shader);
    }

    private static int CreateSamplerState(IntPtr device, ref D3D11_SAMPLER_DESC desc, out IntPtr samplerState)
    {
        var createSamplerState = GetMethod<CreateSamplerStateDelegate>(device, 23);
        return createSamplerState(device, ref desc, out samplerState);
    }

    private static void RSSetViewports(IntPtr context, uint viewportCount, ref D3D11_VIEWPORT viewport)
    {
        var rsSetViewports = GetMethod<RSSetViewportsDelegate>(context, 44);
        rsSetViewports(context, viewportCount, ref viewport);
    }

    private static void OMSetRenderTargets(IntPtr context, uint renderTargetCount, IntPtr renderTargetViews, IntPtr depthStencilView)
    {
        var omSetRenderTargets = GetMethod<OMSetRenderTargetsDelegate>(context, 33);
        omSetRenderTargets(context, renderTargetCount, renderTargetViews, depthStencilView);
    }

    private static void IASetPrimitiveTopology(IntPtr context, uint primitiveTopology)
    {
        var iaSetPrimitiveTopology = GetMethod<IASetPrimitiveTopologyDelegate>(context, 24);
        iaSetPrimitiveTopology(context, primitiveTopology);
    }

    private static void VSSetShader(IntPtr context, IntPtr shader, IntPtr classInstances, uint classInstanceCount)
    {
        var vsSetShader = GetMethod<SetShaderDelegate>(context, 11);
        vsSetShader(context, shader, classInstances, classInstanceCount);
    }

    private static void PSSetShader(IntPtr context, IntPtr shader, IntPtr classInstances, uint classInstanceCount)
    {
        var psSetShader = GetMethod<SetShaderDelegate>(context, 9);
        psSetShader(context, shader, classInstances, classInstanceCount);
    }

    private static void PSSetShaderResources(IntPtr context, uint startSlot, uint viewCount, IntPtr shaderResourceViews)
    {
        var psSetShaderResources = GetMethod<SetResourcesDelegate>(context, 8);
        psSetShaderResources(context, startSlot, viewCount, shaderResourceViews);
    }

    private static void PSSetSamplers(IntPtr context, uint startSlot, uint samplerCount, IntPtr samplers)
    {
        var psSetSamplers = GetMethod<SetResourcesDelegate>(context, 10);
        psSetSamplers(context, startSlot, samplerCount, samplers);
    }

    private static void Draw(IntPtr context, uint vertexCount, uint startVertexLocation)
    {
        var draw = GetMethod<DrawDelegate>(context, 13);
        draw(context, vertexCount, startVertexLocation);
    }

    private static void ClearRenderTargetView(IntPtr context, IntPtr renderTargetView, IntPtr colorRgba)
    {
        var clear = GetMethod<ClearRenderTargetViewDelegate>(context, 50);
        clear(context, renderTargetView, colorRgba);
    }

    private static void Flush(IntPtr context)
    {
        var flush = GetMethod<FlushDelegate>(context, 111);
        flush(context);
    }

    private static IntPtr GetBlobPointer(IntPtr blob)
    {
        var getBufferPointer = GetMethod<GetBufferPointerDelegate>(blob, 3);
        return getBufferPointer(blob);
    }

    private static nuint GetBlobSize(IntPtr blob)
    {
        var getBufferSize = GetMethod<GetBufferSizeDelegate>(blob, 4);
        return getBufferSize(blob);
    }

    [DllImport("d3dcompiler_47.dll", ExactSpelling = true, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private static extern int D3DCompile(
        string sourceData,
        nuint sourceDataSize,
        string? sourceName,
        IntPtr defines,
        IntPtr include,
        string entryPoint,
        string target,
        uint flags1,
        uint flags2,
        out IntPtr code,
        out IntPtr errorMessages);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int OpenSharedResourceDelegate(IntPtr self, IntPtr resourceHandle, ref Guid returnedInterface, out IntPtr resource);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateRenderTargetViewDelegate(IntPtr self, IntPtr resource, IntPtr desc, out IntPtr renderTargetView);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateShaderResourceViewDelegate(IntPtr self, IntPtr resource, ref D3D11_SHADER_RESOURCE_VIEW_DESC desc, out IntPtr shaderResourceView);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateShaderDelegate(IntPtr self, IntPtr bytecode, nuint bytecodeLength, IntPtr classLinkage, out IntPtr shader);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateSamplerStateDelegate(IntPtr self, ref D3D11_SAMPLER_DESC desc, out IntPtr samplerState);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void RSSetViewportsDelegate(IntPtr self, uint viewportCount, ref D3D11_VIEWPORT viewports);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void OMSetRenderTargetsDelegate(IntPtr self, uint renderTargetCount, IntPtr renderTargetViews, IntPtr depthStencilView);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void IASetPrimitiveTopologyDelegate(IntPtr self, uint topology);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void SetShaderDelegate(IntPtr self, IntPtr shader, IntPtr classInstances, uint classInstanceCount);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void SetResourcesDelegate(IntPtr self, uint startSlot, uint resourceCount, IntPtr resources);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void DrawDelegate(IntPtr self, uint vertexCount, uint startVertexLocation);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void FlushDelegate(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void ClearRenderTargetViewDelegate(IntPtr self, IntPtr renderTargetView, IntPtr colorRgba);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetAdapterDelegate(IntPtr self, out IntPtr adapter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetParentDelegate(IntPtr self, ref Guid riid, out IntPtr parent);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CheckFeatureSupportDelegate(IntPtr self, int feature, ref int supportData, int supportDataSize);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateSwapChainForHwndDelegate(IntPtr self, IntPtr device, IntPtr hwnd, ref DXGI_SWAP_CHAIN_DESC1 desc, IntPtr fullscreenDesc, IntPtr restrictToOutput, out IntPtr swapChain);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int MakeWindowAssociationDelegate(IntPtr self, IntPtr hwnd, uint flags);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetMaximumFrameLatencyDelegate(IntPtr self, uint maxLatency);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PresentDelegate(IntPtr self, uint syncInterval, uint flags);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetBufferDelegate(IntPtr self, uint buffer, ref Guid riid, out IntPtr surface);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ResizeBuffersDelegate(IntPtr self, uint bufferCount, uint width, uint height, int newFormat, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct DXGI_SWAP_CHAIN_DESC1
    {
        public uint Width;
        public uint Height;
        public int Format;
        public int Stereo;
        public uint SampleCount;
        public uint SampleQuality;
        public uint BufferUsage;
        public uint BufferCount;
        public int Scaling;
        public int SwapEffect;
        public int AlphaMode;
        public uint Flags;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr GetBufferPointerDelegate(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate nuint GetBufferSizeDelegate(IntPtr self);

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_SHADER_RESOURCE_VIEW_DESC
    {
        public int Format;
        public int ViewDimension;
        public uint MostDetailedMip;
        public uint MipLevels;
        public uint FirstArraySlice;
        public uint ArraySize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_SAMPLER_DESC
    {
        public int Filter;
        public int AddressU;
        public int AddressV;
        public int AddressW;
        public float MipLODBias;
        public uint MaxAnisotropy;
        public int ComparisonFunc;
        public float BorderColor0;
        public float BorderColor1;
        public float BorderColor2;
        public float BorderColor3;
        public float MinLOD;
        public float MaxLOD;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_VIEWPORT
    {
        public float TopLeftX;
        public float TopLeftY;
        public float Width;
        public float Height;
        public float MinDepth;
        public float MaxDepth;
    }
}

internal readonly record struct D3D11DirectPresentMetrics(long SrvTicks, long DrawTicks, long DirtyTicks);
