using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using MacstreamViewer.Diagnostics;

namespace MacstreamViewer.Network;

public sealed record Usb4IpDetectionResult(
    string Host,
    string LocalAddress,
    string InterfaceName,
    string InterfaceDescription,
    int Port,
    double ProbeMilliseconds);

public static class Usb4IpDetector
{
    private const int ProbeTimeoutMs = 120;
    private const int MaxParallelProbes = 1024;   // full 169.254/16 sweep in ~8 s on a direct link

    /// <summary>
    /// Looks for the Mac server on direct-link (169.254.0.0/16 link-local) adapters only.
    /// Wi-Fi/LAN subnets and virtual adapters (Hyper-V, WSL, VPNs) are never probed.
    /// </summary>
    /// <param name="fullSweep">
    /// When false, only the typed host and ARP neighbours on the direct link are probed (cheap; used by
    /// the background loop). When true, the whole link-local range on the direct link is also probed.
    /// </param>
    public static async Task<Usb4IpDetectionResult?> DetectAsync(
        int port,
        string? preferredHost = null,
        CancellationToken ct = default,
        bool fullSweep = true)
    {
        var interfaces = GetCandidateInterfaces();
        if (interfaces.Count == 0)
        {
            DiagLog.Write("USB4IP detection: no usable IPv4 network interface");
            return null;
        }

        var targets = EnumerateTargets(interfaces, preferredHost, fullSweep).ToList();
        if (targets.Count == 0)
        {
            DiagLog.Write("USB4IP detection: no candidate addresses to probe");
            return null;
        }

        DiagLog.Write($"USB4IP detection started: interfaces={interfaces.Count}, targets={targets.Count}, port={port}");
        using var foundCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var foundGate = new object();
        Usb4IpDetectionResult? found = null;

        try
        {
            await Parallel.ForEachAsync(
                targets,
                new ParallelOptions
                {
                    CancellationToken = foundCts.Token,
                    MaxDegreeOfParallelism = MaxParallelProbes,
                },
                async (target, token) =>
                {
                    if (found != null)
                        return;

                    var result = await ProbeAsync(target, port, token);
                    if (result == null)
                        return;

                    lock (foundGate)
                    {
                        if (found != null)
                            return;

                        found = result;
                        foundCts.Cancel();
                    }
                });
        }
        catch (OperationCanceledException) when (found != null)
        {
        }

        if (found != null)
        {
            DiagLog.Write(
                $"USB4IP detection succeeded: host={found.Host}, local={found.LocalAddress}, iface={found.InterfaceName}, probe={found.ProbeMilliseconds:F1}ms");
        }
        else
        {
            DiagLog.Write("USB4IP detection found no reachable Mac server");
        }

        return found;
    }

    private static List<CandidateInterface> GetCandidateInterfaces()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(ni =>
                ni.OperationalStatus == OperationalStatus.Up &&
                ni.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                ni.NetworkInterfaceType != NetworkInterfaceType.Tunnel &&
                ni.NetworkInterfaceType != NetworkInterfaceType.Wireless80211 &&
                !IsVirtualAdapter(ni))
            .SelectMany(ni =>
            {
                var props = ni.GetIPProperties();
                return props.UnicastAddresses
                    .Where(a =>
                        a.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(a.Address) &&
                        IsLinkLocal(a.Address) &&
                        a.IPv4Mask != null)
                    .Select(a => new CandidateInterface(
                        ni.Name,
                        ni.Description,
                        a.Address,
                        a.IPv4Mask,
                        ScoreInterface(ni, a.Address)));
            })
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ToList();
    }

    private static bool IsVirtualAdapter(NetworkInterface ni)
    {
        var text = $"{ni.Name} {ni.Description}";

        // Windows' USB4 host-to-host adapter is a software (virtual) NIC, but it IS a direct cable link.
        if (text.Contains("USB4", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Thunderbolt", StringComparison.OrdinalIgnoreCase))
            return false;

        // Bluetooth networking reports itself as Ethernet; it is wireless, so never search it.
        string[] markers = ["Hyper-V", "vEthernet", "Virtual", "VMware", "VirtualBox", "WSL", "TAP-", "WireGuard", "Tailscale", "ZeroTier", "VPN", "Loopback", "Bluetooth", "Wi-Fi", "WLAN", "Wireless"];
        return markers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static int ScoreInterface(NetworkInterface ni, IPAddress address)
    {
        var text = $"{ni.Name} {ni.Description}";
        var score = 0;

        if (IsLinkLocal(address))
            score += 80;
        if (text.Contains("Thunderbolt", StringComparison.OrdinalIgnoreCase))
            score += 80;
        if (text.Contains("USB4", StringComparison.OrdinalIgnoreCase))
            score += 80;
        if (text.Contains("USB", StringComparison.OrdinalIgnoreCase))
            score += 50;
        if (text.Contains("TBT", StringComparison.OrdinalIgnoreCase))
            score += 50;
        if (text.Contains("Network Bridge", StringComparison.OrdinalIgnoreCase))
            score += 30;
        if (ni.Speed >= 5_000_000_000)
            score += 25;
        if (ni.Speed >= 20_000_000_000)
            score += 25;

        return score;
    }

    private static IEnumerable<ProbeTarget> EnumerateTargets(
        IReadOnlyList<CandidateInterface> interfaces,
        string? preferredHost,
        bool fullSweep)
    {
        var emitted = new HashSet<string>(StringComparer.Ordinal);

        foreach (var address in PreferredAddresses(preferredHost))
        {
            foreach (var candidateInterface in InterfacesForAddress(interfaces, address))
            {
                if (TryEmit(emitted, candidateInterface, address, out var target))
                    yield return target;
            }
        }

        foreach (var address in ArpNeighborAddresses())
        {
            foreach (var candidateInterface in InterfacesForAddress(interfaces, address))
            {
                if (TryEmit(emitted, candidateInterface, address, out var target))
                    yield return target;
            }
        }

        if (!fullSweep)
            yield break;

        foreach (var candidateInterface in interfaces.Where(i => IsLinkLocal(i.Address)))
        {
            foreach (var address in LinkLocalAddresses())
            {
                if (TryEmit(emitted, candidateInterface, address, out var target))
                    yield return target;
            }
        }
    }

    private static IEnumerable<IPAddress> PreferredAddresses(string? preferredHost)
    {
        if (string.IsNullOrWhiteSpace(preferredHost))
            yield break;

        if (IPAddress.TryParse(preferredHost.Trim(), out var address) &&
            address.AddressFamily == AddressFamily.InterNetwork)
        {
            yield return address;
        }
    }

    private static IEnumerable<IPAddress> ArpNeighborAddresses()
    {
        var addresses = new List<IPAddress>();

        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "arp",
                Arguments = "-a",
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };

            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(500))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
            }

            foreach (Match match in Regex.Matches(output, @"\b(?:\d{1,3}\.){3}\d{1,3}\b"))
            {
                if (IPAddress.TryParse(match.Value, out var address) &&
                    address.AddressFamily == AddressFamily.InterNetwork &&
                    !IsBroadcastOrNetworkAddress(address))
                {
                    addresses.Add(address);
                }
            }
        }
        catch (Exception ex)
        {
            DiagLog.Write($"USB4IP detection: reading ARP neighbours failed: {ex.Message}");
        }

        return addresses;
    }

    private static IEnumerable<CandidateInterface> InterfacesForAddress(
        IReadOnlyList<CandidateInterface> interfaces,
        IPAddress address)
    {
        return interfaces
            .Where(candidateInterface =>
                IsSameAddressFamily(candidateInterface.Address, address) &&
                !candidateInterface.Address.Equals(address) &&
                (IsSameSubnet(candidateInterface.Address, address, candidateInterface.Mask) ||
                 IsLinkLocal(candidateInterface.Address) && IsLinkLocal(address)))
            .OrderByDescending(candidateInterface => candidateInterface.Score)
            .Select(candidateInterface => candidateInterface);
    }

    private static IEnumerable<IPAddress> LinkLocalAddresses()
    {
        for (var third = 1; third <= 254; third++)
        {
            for (var fourth = 1; fourth <= 254; fourth++)
            {
                yield return IPAddress.Parse($"169.254.{third}.{fourth}");
            }
        }
    }

    private static bool TryEmit(
        HashSet<string> emitted,
        CandidateInterface candidateInterface,
        IPAddress address,
        out ProbeTarget target)
    {
        target = default;
        if (candidateInterface.Address.Equals(address) || IsBroadcastOrNetworkAddress(address))
            return false;

        var key = $"{candidateInterface.Address}>{address}";
        if (!emitted.Add(key))
            return false;

        target = new ProbeTarget(candidateInterface, address);
        return true;
    }

    private static async Task<Usb4IpDetectionResult?> ProbeAsync(
        ProbeTarget target,
        int port,
        CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ProbeTimeoutMs);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var client = new TcpClient(new IPEndPoint(target.Interface.Address, 0))
            {
                NoDelay = true,
            };
            await client.ConnectAsync(target.Address, port, timeoutCts.Token);
            stopwatch.Stop();

            return new Usb4IpDetectionResult(
                target.Address.ToString(),
                target.Interface.Address.ToString(),
                target.Interface.Name,
                target.Interface.Description,
                port,
                stopwatch.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (SocketException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
    }

    private static bool IsSameAddressFamily(IPAddress left, IPAddress right)
    {
        return left.AddressFamily == right.AddressFamily;
    }

    private static bool IsSameSubnet(IPAddress left, IPAddress right, IPAddress mask)
    {
        return (ToUInt32(left) & ToUInt32(mask)) == (ToUInt32(right) & ToUInt32(mask));
    }

    private static bool IsLinkLocal(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes is [169, 254, _, _];
    }

    private static bool IsBroadcastOrNetworkAddress(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[3] == 0 || bytes[3] == 255 || address.Equals(IPAddress.Any) || address.Equals(IPAddress.Broadcast);
    }

    private static uint ToUInt32(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24) |
               ((uint)bytes[1] << 16) |
               ((uint)bytes[2] << 8) |
               bytes[3];
    }

    private sealed record CandidateInterface(
        string Name,
        string Description,
        IPAddress Address,
        IPAddress Mask,
        int Score);

    private readonly record struct ProbeTarget(CandidateInterface Interface, IPAddress Address);
}
