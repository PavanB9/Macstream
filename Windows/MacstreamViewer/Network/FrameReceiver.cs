using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using MacstreamViewer.Display;
using MacstreamViewer.Diagnostics;
using MacstreamViewer.Protocol;
using MacstreamViewer.Security;

namespace MacstreamViewer.Network;

public class FrameReceiver
{
    private const uint MaxPayloadLength = 128 * 1024 * 1024;
    private const int SocketBufferSize = 4 * 1024 * 1024;
    private const int LargePacketLogBytes = 256 * 1024;
    private const double SlowPacketReadMs = 20.0;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    private TcpClient? _client;
    private Stream? _stream;
    private CancellationTokenSource? _cts;
    private Channel<OutgoingPacket>? _sendQueue;
    private long _connectionGeneration;

    /// <summary>Packets the client sends (keyframe requests, pings, input), in order.</summary>
    private readonly record struct OutgoingPacket(PacketType Type, byte[] Bytes, string? LogMessage);

    /// <summary>
    /// Upper bound on queued outgoing packets. Input events are ~20 bytes, so this is far more
    /// than a healthy link ever holds; if it fills up, the link is stalled and the connection
    /// will time out anyway.
    /// </summary>
    private const int MaxQueuedSendPackets = 2048;

    public event EventHandler<FramePayload>? OnH264Frame;
    public event EventHandler<FramePayload>? OnHevcFrame;
    public event EventHandler<RawFramePayload>? OnRawFrame;
    public event EventHandler<ServerStatsPayload>? OnServerStats;
    /// <summary>CLIPBOARD payloads from the Mac (a copy; raised on the receive thread).</summary>
    public event EventHandler<byte[]>? OnClipboard;
    /// <summary>INPUT_FEEDBACK payloads (the mouse and the Mac's own screen), on the receive thread.</summary>
    public event EventHandler<byte[]>? OnInputFeedback;

    /// <summary>Round-trip time of the last ping, in milliseconds.</summary>
    public event EventHandler<double>? OnPingRoundTrip;
    public event EventHandler<bool>? OnConnectionChanged;
    public event EventHandler<string>? OnReceiveError;

    /// <summary>
    /// First connection to a Mac: shows the 6-digit code and returns true only if the user
    /// confirms the Mac shows the same code. Called on a network thread. Unset = refuse.
    /// </summary>
    public Func<string, Task<bool>>? ConfirmPairing { get; set; }

    public bool IsConnected => _client?.Connected ?? false;

    /// <summary>What to ask the Mac to show; sent in HELLO on each connection.</summary>
    public MacScreenRequest Screen { get; set; } = MacScreenRequest.Mirror;

    /// <summary>
    /// True for IP addresses on this machine, the direct cable (link-local) or a private local
    /// network. Host names are refused, so no name lookup ever happens and nothing on the
    /// internet can be reached by mistake.
    /// </summary>
    public static bool IsLocalAddress(string host)
    {
        if (!IPAddress.TryParse(host, out var address))
            return false;
        if (IPAddress.IsLoopback(address))
            return true;
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal;
        var b = address.GetAddressBytes();
        return b[0] == 10
            || (b[0] == 169 && b[1] == 254)
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168);
    }

    public async Task ConnectAsync(string host, int port, ClientDisplayInfo displayInfo, CancellationToken ct = default)
    {
        DiagLog.Write($"ConnectAsync started: {host}:{port}");
        // Local only: the viewer never connects outside the direct cable / local network.
        if (!IsLocalAddress(host))
            throw new InvalidOperationException(
                $"Macstream only connects to a local address (169.254.x.x direct cable, or 10.x / 172.16-31.x / 192.168.x). '{host}' is not one.");
        DisconnectCurrent(notify: false);
        var generation = Interlocked.Increment(ref _connectionGeneration);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var address = IPAddress.TryParse(host, out var parsedAddress) ? parsedAddress : null;
        var client = address == null
            ? new TcpClient()
            : new TcpClient(address.AddressFamily);
        client.NoDelay = true;
        client.ReceiveBufferSize = SocketBufferSize;
        client.SendBufferSize = 256 * 1024;

        var connectTask = address == null
            ? client.ConnectAsync(host, port)
            : client.ConnectAsync(address, port);

        try
        {
            await connectTask.WaitAsync(ConnectTimeout, ct);

            var networkStream = client.GetStream();
            _client = client;
            _stream = networkStream;
            _cts = cts;

            // Pair (first time) or authenticate, then encrypt everything from here on.
            Stream stream = await ClientHandshake.RunAsync(
                networkStream, PairingStore.Shared, Environment.MachineName,
                ConfirmPairing,
                maxIncomingRecord: (int)MaxPayloadLength + ProtocolConstants.HeaderSize,
                cts.Token);
            if (!IsCurrentConnection(generation))
                throw new OperationCanceledException("connection was replaced by a new session");
            _stream = stream;

            await SendHelloAsync(stream, displayInfo, cts.Token);

            // One writer task per connection keeps control packets in the order they were
            // queued: a mouse-up must never overtake its mouse-down.
            var sendQueue = Channel.CreateBounded<OutgoingPacket>(new BoundedChannelOptions(MaxQueuedSendPackets)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
            });
            _sendQueue = sendQueue;
            _ = Task.Run(() => SendLoop(stream, sendQueue.Reader, cts.Token), CancellationToken.None);
            DiagLog.Write($"TCP connected: recvBuf={client.ReceiveBufferSize}, sendBuf={client.SendBufferSize}, generation={generation}");

            if (!IsCurrentConnection(generation))
                throw new OperationCanceledException("connection was replaced by a new session");

            OnConnectionChanged?.Invoke(this, true);

            _ = Task.Run(async () =>
            {
                try
                {
                    await ReceiveLoop(stream, cts.Token);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    if (IsCurrentConnection(generation))
                    {
                        Console.WriteLine($"[Network] disconnected: {ex.Message}");
                        DiagLog.Write(ex, "ReceiveLoop error");
                        OnReceiveError?.Invoke(this, ex.Message);
                    }
                }
                finally
                {
                    var wasCurrent = IsCurrentConnection(generation);
                    if (wasCurrent)
                        ClearConnectionFields();
                    // Close the socket too, so the Mac sees the disconnect at once (a broken or
                    // tampered stream must not stay half-open).
                    try { stream.Dispose(); } catch { }
                    try { client.Close(); } catch { }
                    if (wasCurrent)
                        OnConnectionChanged?.Invoke(this, false);
                }
            }, CancellationToken.None);
        }
        catch
        {
            if (IsCurrentConnection(generation))
                DisconnectCurrent(notify: false);

            CloseClient(client, cts);

            throw;
        }
    }

    public void Disconnect()
    {
        DisconnectCurrent(notify: true);
    }

    /// <summary>
    /// Asks the Mac server for an immediate keyframe (FRAME_REQUEST, empty payload).
    /// Used when the decoder lost its reference frames. Fire-and-forget.
    /// </summary>
    public void RequestKeyFrame()
    {
        SendControlPacket(PacketType.FrameRequest, Array.Empty<byte>(), "Requested keyframe from server");
    }

    /// <summary>Sends a ping; the server echoes it and OnPingRoundTrip reports the round trip.</summary>
    public void SendPing()
    {
        SendControlPacket(PacketType.Stats, ServerStatsPayload.CreatePing(Stopwatch.GetTimestamp()), logMessage: null);
    }

    /// <summary>
    /// Sends one INPUT_EVENT (Windows -> Mac mouse/keyboard). Payloads come from
    /// <see cref="MacInputPayload"/>. Fire-and-forget, in order.
    /// </summary>
    public void SendInput(byte[] payload)
    {
        SendControlPacket(PacketType.InputEvent, payload, logMessage: null);
    }

    /// <summary>Sends one CLIPBOARD packet (see <see cref="ClipboardPayload"/>), in order with input.</summary>
    public void SendClipboard(byte[] payload)
    {
        SendControlPacket(PacketType.Clipboard, payload, logMessage: null);
    }

    /// <summary>Queues a small control packet for the connection's writer task. Never blocks.</summary>
    private void SendControlPacket(PacketType type, byte[] payload, string? logMessage)
    {
        var queue = _sendQueue;
        if (queue == null)
            return;

        var header = new PacketHeader
        {
            Magic = ProtocolConstants.Magic,
            Version = ProtocolConstants.Version,
            Type = type,
            PayloadLength = (uint)payload.Length,
        }.Encode();
        var packet = new byte[header.Length + payload.Length];
        header.CopyTo(packet, 0);
        payload.CopyTo(packet, header.Length);

        if (!queue.Writer.TryWrite(new OutgoingPacket(type, packet, logMessage)))
            DiagLog.Write($"Control packet {type} dropped: send queue full or closed");
    }

    private static async Task SendLoop(Stream stream, ChannelReader<OutgoingPacket> reader, CancellationToken ct)
    {
        try
        {
            await foreach (var item in reader.ReadAllAsync(ct))
            {
                await stream.WriteAsync(item.Bytes, ct);
                if (item.LogMessage != null)
                    DiagLog.Write(item.LogMessage);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // The receive loop notices the broken connection and reports it.
            DiagLog.Write($"Control send loop stopped: {ex.Message}");
        }
    }

    private async Task ReceiveLoop(Stream stream, CancellationToken ct)
    {
        var headerBuf = new byte[ProtocolConstants.HeaderSize];
        byte[]? dataBuf = null;
        var frameCount = 0;
        var lastFpsTime = DateTime.UtcNow;
        var fpsCount = 0;
        var loggedFirstPacket = false;
        var loggedUnknownPacketTypes = new HashSet<ushort>();
        var lastPacketReadLogTime = DateTime.MinValue;

        while (!ct.IsCancellationRequested && _stream != null)
        {
            await ReadExact(stream, headerBuf, 0, headerBuf.Length, ct);

            PacketType packetType;
            int payloadLength;

            if (PacketHeader.TryDecode(headerBuf, out var header))
            {
                if (header.PayloadLength > MaxPayloadLength)
                    throw new InvalidDataException($"invalid payload length: {header.PayloadLength}");

                packetType = header.Type;
                payloadLength = (int)header.PayloadLength;
                dataBuf = dataBuf?.Length >= payloadLength ? dataBuf : new byte[payloadLength];
                var payloadReadStart = Stopwatch.GetTimestamp();
                await ReadExact(stream, dataBuf, 0, payloadLength, ct);
                var payloadReadMs = ElapsedMilliseconds(payloadReadStart);

                if (!loggedFirstPacket)
                {
                    DiagLog.Write($"first packet: protocol=WM type={packetType} payloadLen={payloadLength}");
                    loggedFirstPacket = true;
                }

                var packetReadLogNow = DateTime.UtcNow;
                if ((packetType == PacketType.FrameH264 || packetType == PacketType.FrameHevc) &&
                    (payloadLength >= LargePacketLogBytes || payloadReadMs >= SlowPacketReadMs) &&
                    (packetReadLogNow - lastPacketReadLogTime).TotalMilliseconds >= 500)
                {
                    DiagLog.Write($"network read: payload={payloadLength}, read={payloadReadMs:F1}ms");
                    lastPacketReadLogTime = packetReadLogNow;
                }
            }
            else
            {
                if (loggedFirstPacket)
                    throw new InvalidDataException($"WM protocol stream out of sync: {Convert.ToHexString(headerBuf)}");

                var legacyFrame = await TryReadLegacyFrame(stream, headerBuf, dataBuf, ct);
                if (legacyFrame == null)
                    throw new InvalidDataException($"invalid packet header: {Convert.ToHexString(headerBuf)}");

                dataBuf = legacyFrame.Value.Buffer;
                packetType = legacyFrame.Value.PacketType;
                payloadLength = legacyFrame.Value.PayloadLength;

                if (!loggedFirstPacket)
                {
                    DiagLog.Write($"first packet: protocol=legacy type={packetType} payloadLen={payloadLength} first10={Convert.ToHexString(headerBuf)}");
                    loggedFirstPacket = true;
                }
            }

            var isVideoFrame = false;
            switch (packetType)
            {
                case PacketType.FrameH264:
                    OnH264Frame?.Invoke(this, FramePayload.Parse(dataBuf, payloadLength));
                    isVideoFrame = true;
                    break;
                case PacketType.FrameHevc:
                    OnHevcFrame?.Invoke(this, FramePayload.Parse(dataBuf, payloadLength));
                    isVideoFrame = true;
                    break;
                case PacketType.FrameRaw:
                    OnRawFrame?.Invoke(this, RawFramePayload.Parse(dataBuf, payloadLength, 0, 0));
                    isVideoFrame = true;
                    break;
                case PacketType.Clipboard:
                    OnClipboard?.Invoke(this, dataBuf.AsSpan(0, payloadLength).ToArray());
                    break;
                case PacketType.InputFeedback:
                    if (payloadLength <= 64)
                        OnInputFeedback?.Invoke(this, dataBuf.AsSpan(0, payloadLength).ToArray());
                    break;
                case PacketType.Stats:
                    var statsSpan = dataBuf.AsSpan(0, payloadLength);
                    if (ServerStatsPayload.TryParse(statsSpan, out var serverStats))
                        OnServerStats?.Invoke(this, serverStats);
                    else if (ServerStatsPayload.TryParsePong(statsSpan, out var token))
                        OnPingRoundTrip?.Invoke(this, (Stopwatch.GetTimestamp() - token) * 1000.0 / Stopwatch.Frequency);
                    break;
                default:
                    var rawType = (ushort)packetType;
                    if (loggedUnknownPacketTypes.Add(rawType))
                        DiagLog.Write($"ignoring unknown packet type: 0x{rawType:X4}, payloadLen={payloadLength}");
                    break;
            }

            if (isVideoFrame)
            {
                frameCount++;
                fpsCount++;
            }

            var now = DateTime.UtcNow;
            if ((now - lastFpsTime).TotalSeconds >= 2.0)
            {
                var fps = fpsCount / (now - lastFpsTime).TotalSeconds;
                Console.WriteLine($"[Network] FPS: {fps:F1}");
                DiagLog.Write($"Network FPS: {fps:F1}, totalFrames={frameCount}");
                fpsCount = 0;
                lastFpsTime = now;
            }
        }
    }

    private async Task SendHelloAsync(Stream stream, ClientDisplayInfo displayInfo, CancellationToken ct)
    {
        // 0-15: this PC's monitor. 16-31: what to show. 32-35: highest frame rate for an extra
        // display (older Mac servers read only what they know and ignore the rest).
        var screen = Screen;
        var payload = new byte[36];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), (uint)displayInfo.Width);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), (uint)displayInfo.Height);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8, 4), (uint)displayInfo.RefreshRate);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(12, 4), (uint)displayInfo.Dpi);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(16, 4), screen.ExtraDisplay ? 1u : 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(20, 4), (uint)Math.Clamp(screen.TextScale, 0, 2));
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(24, 4), (uint)Math.Max(screen.Width, 0));
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(28, 4), (uint)Math.Max(screen.Height, 0));
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(32, 4), screen.MaxFps > 60 ? (uint)Math.Min(screen.MaxFps, 120) : 0u);

        var header = new PacketHeader
        {
            Magic = ProtocolConstants.Magic,
            Version = ProtocolConstants.Version,
            Type = PacketType.Hello,
            PayloadLength = (uint)payload.Length,
        }.Encode();

        var packet = new byte[header.Length + payload.Length];
        Buffer.BlockCopy(header, 0, packet, 0, header.Length);
        Buffer.BlockCopy(payload, 0, packet, header.Length, payload.Length);

        await stream.WriteAsync(packet.AsMemory(0, packet.Length), ct);
        await stream.FlushAsync(ct);
        DiagLog.Write($"sending HELLO: {displayInfo.Width}x{displayInfo.Height} @ {displayInfo.RefreshRate}Hz, dpi={displayInfo.Dpi}, wants: {screen}");
    }

    private async Task<LegacyFrame?> TryReadLegacyFrame(Stream stream, byte[] firstBytes, byte[]? dataBuf, CancellationToken ct)
    {
        var packetLength = BitConverter.ToUInt32(firstBytes, 0);
        if (packetLength < 2 || packetLength > MaxPayloadLength + 2)
            return null;

        var packetType = firstBytes[4] switch
        {
            0 => PacketType.FrameH264,
            1 => PacketType.FrameRaw,
            _ => default
        };
        if (packetType == default)
            return null;

        var payloadLength = (int)packetLength - 2;
        dataBuf = dataBuf?.Length >= payloadLength ? dataBuf : new byte[payloadLength];

        var bytesAlreadyRead = Math.Min(payloadLength, firstBytes.Length - 6);
        if (bytesAlreadyRead > 0)
            Buffer.BlockCopy(firstBytes, 6, dataBuf, 0, bytesAlreadyRead);

        var remaining = payloadLength - bytesAlreadyRead;
        if (remaining > 0)
            await ReadExact(stream, dataBuf, bytesAlreadyRead, remaining, ct);

        return new LegacyFrame(packetType, payloadLength, dataBuf);
    }

    private readonly record struct LegacyFrame(PacketType PacketType, int PayloadLength, byte[] Buffer);

    private async Task ReadExact(Stream stream, byte[] buf, int offset, int count, CancellationToken ct)
    {
        var end = offset + count;
        while (offset < end)
        {
            var n = await stream.ReadAsync(buf.AsMemory(offset, end - offset), ct);
            if (n == 0) throw new IOException("connection closed");
            offset += n;
        }
    }

    private bool IsCurrentConnection(long generation)
    {
        return Volatile.Read(ref _connectionGeneration) == generation;
    }

    private void DisconnectCurrent(bool notify)
    {
        Interlocked.Increment(ref _connectionGeneration);
        var cts = _cts;
        var stream = _stream;
        var client = _client;
        ClearConnectionFields();
        CloseClient(client, cts, stream);

        if (notify)
            OnConnectionChanged?.Invoke(this, false);
    }

    private void ClearConnectionFields()
    {
        _sendQueue?.Writer.TryComplete();
        _sendQueue = null;
        _cts = null;
        _stream = null;
        _client = null;
    }

    private static void CloseClient(TcpClient? client, CancellationTokenSource? cts, Stream? stream = null)
    {
        try { cts?.Cancel(); } catch { }
        try { stream?.Close(); } catch { }
        try { client?.Close(); } catch { }
        cts?.Dispose();
    }

    private static double ElapsedMilliseconds(long startTimestamp)
    {
        return (Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency;
    }
}
