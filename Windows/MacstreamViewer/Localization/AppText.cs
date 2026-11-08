namespace MacstreamViewer.Localization;

/// <summary>Text shown in the viewer's window.</summary>
public static class AppText
{
    public static string WindowTitle => "Macstream";

    public static string MacAddressLabel => "Mac address:";
    public static string PortLabel => "Port:";
    public static string ConnectButton => "Connect";
    public static string DisconnectButton => "Disconnect";
    public static string FullscreenButton => "Fullscreen";
    public static string ExitFullscreenButton => "Exit full screen";
    public static string Connected => "Connected";
    public static string NotConnected => "Disconnected";
    public static string NotConnectedPrompt => "Not connected - start the server on the Mac, then click Connect";

    public static string Usb4Detecting => "Mac: searching...";
    public static string Usb4NotFound => "Mac: not found on the cable";
    public static string Usb4Error => "Mac: search failed";

    public static string ErrorTitle => "Error";
    public static string WaitingVideoData => "Waiting for video data...";
    public static string DetectingMacUsb4Ip => "Looking for the Mac on the cable...";
    public static string MacUsb4IpNotDetected => "The Mac was not found on the cable. Is the server running on the Mac?";
    public static string ConnectionFailedShort => "Connection failed";

    public static string Usb4Host(string host) => $"Mac: {host}";

    public static string Connecting(string host, int port) => $"Connecting to {host}:{port}...";

    public static string ConnectionFailed(string message) => $"Connection failed: {message}";

    public static string ReceiveInterrupted(string? message) => $"Receive interrupted: {message}";

    public static string AutoReconnecting(int attempt) => $"Connection interrupted, reconnecting #{attempt}...";

    public static string AutoReconnectFailed(string message) => $"Auto reconnect failed, retrying: {message}";

    public static string DetectedMacUsb4Ip(string host) => $"Found the Mac at {host}";

    public static string ReceivedKeyFrame(string codecName, ulong frameIndex) => $"Received {codecName} key frame #{frameIndex}, decoding...";

    public static string ReceivedFrameWaitingDecoder(string codecName, ulong frameIndex) => $"Received {codecName} frame #{frameIndex}, waiting for decoder output...";

    public static string ReceivingNoDecoder(int fpsCount, long totalKb) => $"Receiving (no decoder) FPS: {fpsCount} total: {totalKb}KB";

    public static string HardwareDecoderInitFailed(string codec) => $"{codec} hardware decoder initialization failed - requires a GPU and FFmpeg build with D3D11VA/DXVA2 support";

    public static string DecoderUnavailable(string message) => $"Decoder unavailable: {message}";

    public static string Bandwidth(double mbps) => $"Bandwidth: {mbps:F1} Mbps";
}
