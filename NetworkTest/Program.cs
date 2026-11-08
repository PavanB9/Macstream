using System.Net;
using System.Net.Sockets;

namespace NetworkTest;

class Program
{
    static async Task Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("Usage:");
            Console.WriteLine("  NetworkTest server [port]          - start the throughput test server");
            Console.WriteLine("  NetworkTest client <host> [port]    - start the throughput test client");
            Console.WriteLine("  NetworkTest ping <host> [port]      - measure latency");
            Console.WriteLine("  NetworkTest discovery               - list network interfaces");
            return;
        }

        var mode = args[0].ToLower();
        var port = args.Length > 2 ? int.Parse(args[2]) : 9800;
        var host = args.Length > 1 ? args[1] : "localhost";

        switch (mode)
        {
            case "server":
                await RunServer(port);
                break;
            case "client":
                await RunClient(host, port);
                break;
            case "ping":
                await RunPingTest(host, port);
                break;
            case "discovery":
                DiscoverInterfaces();
                break;
            default:
                Console.WriteLine($"Unknown mode: {mode}");
                break;
        }
    }

    static async Task RunServer(int port)
    {
        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        Console.WriteLine($"[Server] listening on port {port}, waiting for a connection...");

        while (true)
        {
            var client = await listener.AcceptTcpClientAsync();
            _ = Task.Run(() => HandleClient(client));
        }
    }

    static async Task HandleClient(TcpClient client)
    {
        var endpoint = client.Client.RemoteEndPoint;
        Console.WriteLine($"[Server] client connected: {endpoint}");

        using var stream = client.GetStream();
        var buffer = new byte[1024 * 1024]; // 1MB buffer

        // Receive the mode command
        var modeBuffer = new byte[1];
        await stream.ReadAsync(modeBuffer);
        var mode = (TestMode)modeBuffer[0];

        switch (mode)
        {
            case TestMode.ThroughputUpload:
                // Client sends, server receives
                await ReceiveThroughput(stream, buffer);
                break;
            case TestMode.ThroughputDownload:
                // Server sends, client receives
                await SendThroughput(stream, buffer);
                break;
            case TestMode.Latency:
                await HandleLatencyTest(stream);
                break;
        }

        client.Close();
        Console.WriteLine($"[Server] client disconnected: {endpoint}");
    }

    static async Task SendThroughput(NetworkStream stream, byte[] buffer)
    {
        // Fill with random data
        Random.Shared.NextBytes(buffer);

        long totalBytes = 0;
        var startTime = DateTime.UtcNow;
        var duration = TimeSpan.FromSeconds(10);
        var reportInterval = TimeSpan.FromSeconds(1);
        var lastReport = startTime;

        Console.WriteLine("[Server] sending data (10 s)...");

        while (DateTime.UtcNow - startTime < duration)
        {
            await stream.WriteAsync(buffer.AsMemory(0, buffer.Length));
            totalBytes += buffer.Length;

            if (DateTime.UtcNow - lastReport >= reportInterval)
            {
                var elapsed = (DateTime.UtcNow - lastReport).TotalSeconds;
                var speed = (totalBytes / (DateTime.UtcNow - startTime).TotalSeconds) / (1024.0 * 1024.0);
                Console.WriteLine($"[Server] sent {totalBytes / (1024.0 * 1024.0):F1} MB, speed: {speed:F1} MB/s");
                lastReport = DateTime.UtcNow;
            }
        }

        var totalTime = (DateTime.UtcNow - startTime).TotalSeconds;
        var avgSpeed = (totalBytes / totalTime) / (1024.0 * 1024.0);
        Console.WriteLine($"[Server] done! total {totalBytes / (1024.0 * 1024.0):F1} MB, average speed: {avgSpeed:F1} MB/s");
    }

    static async Task ReceiveThroughput(NetworkStream stream, byte[] buffer)
    {
        long totalBytes = 0;
        var startTime = DateTime.UtcNow;
        var lastReport = startTime;

        Console.WriteLine("[Server] receiving data (10 s)...");

        int bytesRead;
        while ((bytesRead = await stream.ReadAsync(buffer)) > 0)
        {
            totalBytes += bytesRead;

            if (DateTime.UtcNow - lastReport >= TimeSpan.FromSeconds(1))
            {
                var elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
                var speed = (totalBytes / elapsed) / (1024.0 * 1024.0);
                Console.WriteLine($"[Server] received {totalBytes / (1024.0 * 1024.0):F1} MB, speed: {speed:F1} MB/s");
                lastReport = DateTime.UtcNow;
            }
        }

        var totalTime = (DateTime.UtcNow - startTime).TotalSeconds;
        var avgSpeed = (totalBytes / totalTime) / (1024.0 * 1024.0);
        Console.WriteLine($"[Server] done! total {totalBytes / (1024.0 * 1024.0):F1} MB, average speed: {avgSpeed:F1} MB/s");
    }

    static async Task HandleLatencyTest(NetworkStream stream)
    {
        var pingBuffer = new byte[64];
        int received = 0;

        while (true)
        {
            int bytesRead;
            try
            {
                bytesRead = await stream.ReadAsync(pingBuffer.AsMemory(0, 64));
                if (bytesRead == 0) break;
            }
            catch
            {
                break;
            }

            // Echo back immediately
            await stream.WriteAsync(pingBuffer.AsMemory(0, bytesRead));
            received++;
        }

        Console.WriteLine($"[Server] latency test done, handled {received} pings");
    }

    static async Task RunClient(string host, int port)
    {
        Console.WriteLine($"[Client] connecting to {host}:{port}...");
        using var client = new TcpClient();
        await client.ConnectAsync(host, port);

        using var stream = client.GetStream();
        var buffer = new byte[1024 * 1024];

        // Upload speed test
        Console.WriteLine("\n=== Upload speed test (Windows → Mac) ===");
        await stream.WriteAsync(new byte[] { (byte)TestMode.ThroughputUpload });
        await RunUploadTest(stream, buffer);

        // Reconnect for the download test
        client.Close();

        Console.WriteLine($"\n[Client] reconnecting for the download speed test...");
        using var client2 = new TcpClient();
        await client2.ConnectAsync(host, port);
        using var stream2 = client2.GetStream();

        Console.WriteLine("\n=== Download speed test (Mac → Windows) ===");
        await stream2.WriteAsync(new byte[] { (byte)TestMode.ThroughputDownload });
        await RunDownloadTest(stream2, buffer);
    }

    static async Task RunUploadTest(NetworkStream stream, byte[] buffer)
    {
        Random.Shared.NextBytes(buffer);

        long totalBytes = 0;
        var startTime = DateTime.UtcNow;
        var duration = TimeSpan.FromSeconds(10);
        var lastReport = startTime;

        while (DateTime.UtcNow - startTime < duration)
        {
            await stream.WriteAsync(buffer.AsMemory(0, buffer.Length));
            totalBytes += buffer.Length;

            if (DateTime.UtcNow - lastReport >= TimeSpan.FromSeconds(1))
            {
                var elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
                var speed = (totalBytes / elapsed) / (1024.0 * 1024.0);
                Console.WriteLine($"[Client] upload: {totalBytes / (1024.0 * 1024.0):F1} MB, speed: {speed:F1} MB/s");
                lastReport = DateTime.UtcNow;
            }
        }

        var avgSpeed = (totalBytes / (DateTime.UtcNow - startTime).TotalSeconds) / (1024.0 * 1024.0);
        Console.WriteLine($"[Client] upload done! average speed: {avgSpeed:F1} MB/s");
    }

    static async Task RunDownloadTest(NetworkStream stream, byte[] buffer)
    {
        long totalBytes = 0;
        var startTime = DateTime.UtcNow;

        int bytesRead;
        while ((bytesRead = await stream.ReadAsync(buffer)) > 0)
        {
            totalBytes += bytesRead;

            if (DateTime.UtcNow - startTime >= TimeSpan.FromSeconds(10))
                break;

            var elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
            if (elapsed >= 1 && totalBytes > 0)
            {
                var speed = (totalBytes / elapsed) / (1024.0 * 1024.0);
                Console.WriteLine($"[Client] download: {totalBytes / (1024.0 * 1024.0):F1} MB, speed: {speed:F1} MB/s");
            }
        }

        var avgSpeed = (totalBytes / (DateTime.UtcNow - startTime).TotalSeconds) / (1024.0 * 1024.0);
        Console.WriteLine($"[Client] download done! average speed: {avgSpeed:F1} MB/s");
    }

    static async Task RunPingTest(string host, int port)
    {
        Console.WriteLine($"[Ping] connecting to {host}:{port}...");

        using var client = new TcpClient();
        await client.ConnectAsync(host, port);
        using var stream = client.GetStream();

        await stream.WriteAsync(new byte[] { (byte)TestMode.Latency });

        var pingData = new byte[64];
        var pongBuffer = new byte[64];
        Random.Shared.NextBytes(pingData);

        var latencies = new List<double>();
        Console.WriteLine("[Ping] sending 100 pings...\n");

        for (int i = 0; i < 100; i++)
        {
            BitConverter.GetBytes(i).CopyTo(pingData, 0);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await stream.WriteAsync(pingData);
            await stream.ReadExactlyAsync(pongBuffer);
            sw.Stop();

            var latencyUs = sw.Elapsed.TotalMicroseconds;
            latencies.Add(latencyUs);

            if (i % 10 == 0)
                Console.WriteLine($"  ping {i}: {latencyUs:F0} μs ({latencyUs / 1000.0:F2} ms)");
        }

        Console.WriteLine($"\n[Ping] results:");
        Console.WriteLine($"  min: {latencies.Min():F0} μs");
        Console.WriteLine($"  max: {latencies.Max():F0} μs");
        Console.WriteLine($"  avg: {latencies.Average():F0} μs");
        Console.WriteLine($"  median: {latencies.OrderBy(x => x).Skip(50).First():F0} μs");
        Console.WriteLine($"  P95:  {latencies.OrderBy(x => x).Skip(94).First():F0} μs");
        Console.WriteLine($"  P99:  {latencies.OrderBy(x => x).Skip(98).First():F0} μs");
    }

    static void DiscoverInterfaces()
    {
        Console.WriteLine("[Discovery] network interfaces:\n");

        foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            var ipProps = ni.GetIPProperties();
            var ipv4 = ipProps.UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);

            var isThunderbolt = ni.Name.Contains("Thunderbolt", StringComparison.OrdinalIgnoreCase) ||
                               ni.Description.Contains("Thunderbolt", StringComparison.OrdinalIgnoreCase) ||
                               ni.Name.Contains("USB", StringComparison.OrdinalIgnoreCase) ||
                               ni.Description.Contains("USB", StringComparison.OrdinalIgnoreCase);

            var marker = isThunderbolt ? " *** Thunderbolt/USB ***" : "";

            Console.WriteLine($"  [{ni.OperationalStatus}] {ni.Name}{marker}");
            Console.WriteLine($"    description: {ni.Description}");
            Console.WriteLine($"    type: {ni.NetworkInterfaceType}");
            Console.WriteLine($"    speed: {ni.Speed / 1_000_000} Mbps");

            if (ipv4 != null)
                Console.WriteLine($"    IPv4: {ipv4.Address}");

            Console.WriteLine();
        }

        Console.WriteLine("Tip: with a direct cable, both machines get a link-local 169.254.x.x address.");
        Console.WriteLine("      Mac: System Settings → Network (the USB-C Ethernet adapter or Thunderbolt Bridge)");
        Console.WriteLine("      Windows: Settings → Network & internet → Ethernet");
    }

    enum TestMode : byte
    {
        ThroughputUpload = 1,
        ThroughputDownload = 2,
        Latency = 3,
    }
}
