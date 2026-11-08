using System.Windows;
using Microsoft.Extensions.Logging;
using MacstreamViewer.Decoder;
using MacstreamViewer.Diagnostics;
using MacstreamViewer.Display;
using MacstreamViewer.Network;
using MacstreamViewer.Protocol;

namespace MacstreamViewer;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Contains("--ffmpeg-self-test"))
        {
            using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
            DiagLog.Write("FFmpeg self-test started");
            using var decoder = new H264Decoder(loggerFactory.CreateLogger<H264Decoder>());
            var ok = decoder.Initialize(1920, 1080);
            DiagLog.Write($"FFmpeg self-test result: {ok}");
            Shutdown(ok ? 0 : 1);
            return;
        }

        if (e.Args.Contains("--probe"))
        {
            RunProbe(e.Args);
            Shutdown(0);
            return;
        }

        base.OnStartup(e);
        new MainWindow().Show();
    }

    private static void RunProbe(string[] args)
    {
        var hostIndex = Array.IndexOf(args, "--probe") + 1;
        var host = hostIndex > 0 && hostIndex < args.Length ? args[hostIndex] : "169.254.79.57";
        var port = hostIndex + 1 < args.Length && int.TryParse(args[hostIndex + 1], out var parsedPort)
            ? parsedPort
            : ProtocolConstants.VideoPort;

        var h264Frames = 0;
        var hevcFrames = 0;
        var rawFrames = 0;
        var keyFrames = 0;
        var bytes = 0L;
        var receiver = new FrameReceiver();

        receiver.OnH264Frame += (_, frame) =>
        {
            h264Frames++;
            if (frame.IsKeyFrame) keyFrames++;
            bytes += frame.Data.Length;
            if (h264Frames <= 3 || frame.IsKeyFrame)
                DiagLog.Write($"Probe H264: idx={frame.FrameIndex}, key={frame.IsKeyFrame}, bytes={frame.Data.Length}");
        };
        receiver.OnHevcFrame += (_, frame) =>
        {
            hevcFrames++;
            if (frame.IsKeyFrame) keyFrames++;
            bytes += frame.Data.Length;
            if (hevcFrames <= 3 || frame.IsKeyFrame)
                DiagLog.Write($"Probe HEVC: idx={frame.FrameIndex}, key={frame.IsKeyFrame}, bytes={frame.Data.Length}");
        };
        receiver.OnRawFrame += (_, frame) =>
        {
            rawFrames++;
            bytes += frame.PixelData.Length;
            if (rawFrames <= 3)
                DiagLog.Write($"Probe RAW: idx={frame.FrameIndex}, {frame.Width}x{frame.Height}, stride={frame.BytesPerRow}, bytes={frame.PixelData.Length}");
        };
        receiver.OnReceiveError += (_, message) => DiagLog.Write($"Probe receive error: {message}");

        DiagLog.Write($"Probe started: {host}:{port}");
        receiver.ConnectAsync(host, port, new ClientDisplayInfo(1920, 1080, 60, 96)).GetAwaiter().GetResult();
        Task.Delay(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        receiver.Disconnect();
        DiagLog.Write($"Probe result: h264={h264Frames}, hevc={hevcFrames}, raw={rawFrames}, key={keyFrames}, bytes={bytes}");
    }
}
