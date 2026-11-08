namespace MacstreamViewer.Video;

public readonly record struct D3D11DecodedFrame(
    IntPtr Device,
    IntPtr DeviceContext,
    IntPtr Texture,
    int ArraySlice,
    int Width,
    int Height,
    Action Release);
