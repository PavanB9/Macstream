using System.Buffers.Binary;

namespace MacstreamViewer.Protocol;

public enum PacketType : ushort
{
    Hello = 0x0001,
    HelloAck = 0x0002,
    DisplayInfo = 0x0010,
    FrameRequest = 0x0020,
    FrameH264 = 0x0030,
    FrameRaw = 0x0031,
    FrameHevc = 0x0032,
    InputEvent = 0x0040,
    InputFeedback = 0x0041,   // Mac -> PC: the mouse moved onto / back from the Mac's own screen
    Stats = 0x0050,
    Clipboard = 0x0080,   // clipboard sync, both directions (see Sharing/ClipboardSync.cs)
    // 0x0060 (separate cursor channel) and 0x0070/0x0071 (Mac -> Windows control) were
    // removed from the original fork; the values stay reserved.
    // Security handshake (plaintext, before anything else; see PROTOCOL.md)
    AuthClientHello = 0x0090,
    AuthServerHello = 0x0091,
    PairNonce = 0x0092,
    PairReveal = 0x0093,
    PairResult = 0x0094,
}

public static class ProtocolConstants
{
    public const ushort Magic = 0x574D; // "WM"
    public const ushort Version = 0x0001;
    public const int ControlPort = 9801;
    public const int VideoPort = 9802;
    public const int HeaderSize = 10;
    public const int H264FrameMetadataSize = 17;
    public const int H264FrameMetadataWithSize = 25;
    public const int RawFrameMetadataSize = 16;
    public const int RawFrameMetadataWithSize = 28;
}

public readonly struct PacketHeader
{
    public ushort Magic { get; init; }
    public ushort Version { get; init; }
    public PacketType Type { get; init; }
    public uint PayloadLength { get; init; }

    public byte[] Encode()
    {
        var data = new byte[ProtocolConstants.HeaderSize];
        BitConverter.GetBytes((ushort)Magic).CopyTo(data, 0);
        BitConverter.GetBytes((ushort)Version).CopyTo(data, 2);
        BitConverter.GetBytes((ushort)Type).CopyTo(data, 4);
        BitConverter.GetBytes((uint)PayloadLength).CopyTo(data, 6);
        return data;
    }

    public static bool TryDecode(ReadOnlySpan<byte> data, out PacketHeader header)
    {
        header = default;
        if (data.Length < ProtocolConstants.HeaderSize) return false;

        var magic = BitConverter.ToUInt16(data[..2]);
        if (magic != ProtocolConstants.Magic) return false;

        var version = BitConverter.ToUInt16(data[2..4]);
        if (version != ProtocolConstants.Version) return false;

        header = new PacketHeader
        {
            Magic = magic,
            Version = version,
            Type = (PacketType)BitConverter.ToUInt16(data[4..6]),
            PayloadLength = BitConverter.ToUInt32(data[6..10]),
        };
        return true;
    }
}

public readonly struct FramePayload
{
    public ulong FrameIndex { get; init; }
    public ulong Timestamp { get; init; }
    public bool IsKeyFrame { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public ReadOnlyMemory<byte> Data { get; init; }

    public static FramePayload Parse(byte[] payload, int payloadLength)
    {
        return Parse(payload.AsSpan(0, payloadLength));
    }

    public static FramePayload Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < ProtocolConstants.H264FrameMetadataSize)
            throw new ArgumentException("H.264 frame payload is too short.", nameof(payload));

        var dataOffset = ProtocolConstants.H264FrameMetadataSize;
        var width = 0;
        var height = 0;

        if (payload.Length >= ProtocolConstants.H264FrameMetadataWithSize)
        {
            var payloadWidth = (int)BitConverter.ToUInt32(payload[17..21]);
            var payloadHeight = (int)BitConverter.ToUInt32(payload[21..25]);
            if (payloadWidth >= 16 && payloadWidth <= 16384 &&
                payloadHeight >= 16 && payloadHeight <= 16384)
            {
                width = payloadWidth;
                height = payloadHeight;
                dataOffset = ProtocolConstants.H264FrameMetadataWithSize;
            }
        }

        return new FramePayload
        {
            FrameIndex = BitConverter.ToUInt64(payload[..8]),
            Timestamp = BitConverter.ToUInt64(payload[8..16]),
            IsKeyFrame = payload[16] != 0,
            Width = width,
            Height = height,
            Data = payload[dataOffset..].ToArray(),
        };
    }
}

public readonly struct RawFramePayload
{
    public ulong FrameIndex { get; init; }
    public ulong Timestamp { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int BytesPerRow { get; init; }
    public ReadOnlyMemory<byte> PixelData { get; init; }

    public static RawFramePayload Parse(byte[] payload, int payloadLength, int width, int height)
    {
        return Parse(payload.AsSpan(0, payloadLength), width, height);
    }

    public static RawFramePayload Parse(ReadOnlySpan<byte> payload, int width, int height)
    {
        if (payload.Length < ProtocolConstants.RawFrameMetadataSize)
            throw new ArgumentException("RAW frame payload is too short.", nameof(payload));

        if (payload.Length >= ProtocolConstants.RawFrameMetadataWithSize)
        {
            var payloadWidth = (int)BitConverter.ToUInt32(payload[16..20]);
            var payloadHeight = (int)BitConverter.ToUInt32(payload[20..24]);
            var bytesPerRow = (int)BitConverter.ToUInt32(payload[24..28]);

            if (payloadWidth > 0 &&
                payloadHeight > 0 &&
                bytesPerRow >= payloadWidth * 4 &&
                payload.Length == ProtocolConstants.RawFrameMetadataWithSize + bytesPerRow * payloadHeight)
            {
                return new RawFramePayload
                {
                    FrameIndex = BitConverter.ToUInt64(payload[..8]),
                    Timestamp = BitConverter.ToUInt64(payload[8..16]),
                    Width = payloadWidth,
                    Height = payloadHeight,
                    BytesPerRow = bytesPerRow,
                    PixelData = payload[ProtocolConstants.RawFrameMetadataWithSize..].ToArray(),
                };
            }
        }

        return new RawFramePayload
        {
            FrameIndex = BitConverter.ToUInt64(payload[..8]),
            Timestamp = BitConverter.ToUInt64(payload[8..16]),
            Width = width,
            Height = height,
            BytesPerRow = width > 0 ? width * 4 : 0,
            PixelData = payload[16..].ToArray(),
        };
    }
}

/// <summary>
/// Per-second numbers from the Mac server (STATS packet, kind 1). See StreamStats.swift.
/// </summary>
public readonly record struct ServerStatsPayload(
    int Width,
    int Height,
    bool IsHevc,
    float CaptureFps,
    float EncodeFps,
    float EncodeMsAvg,
    float EncodeMsMax,
    uint BitrateKbps,
    uint TargetBitrateKbps,
    uint SkippedForBackpressure,
    uint DroppedFrames,
    byte MaxQp,
    float EncoderMsAvg = 0,
    bool LowLatency = false,
    bool YuvCapture = false,
    bool HasV2Fields = false,
    bool ExtraDisplay = false)
{
    public const byte KindServerStats = 1;
    public const byte KindPing = 2;
    public const byte KindPong = 3;
    private const int SizeV1 = 44;
    private const int SizeV2 = 49;

    public static bool TryParse(ReadOnlySpan<byte> p, out ServerStatsPayload stats)
    {
        stats = default;
        if (p.Length < SizeV1 || p[0] != KindServerStats || p[1] < 1)
            return false;

        var hasV2 = p[1] >= 2 && p.Length >= SizeV2;

        stats = new ServerStatsPayload(
            Width: (int)BitConverter.ToUInt32(p[2..6]),
            Height: (int)BitConverter.ToUInt32(p[6..10]),
            IsHevc: p[10] == 2,
            CaptureFps: BitConverter.ToSingle(p[11..15]),
            EncodeFps: BitConverter.ToSingle(p[15..19]),
            EncodeMsAvg: BitConverter.ToSingle(p[19..23]),
            EncodeMsMax: BitConverter.ToSingle(p[23..27]),
            BitrateKbps: BitConverter.ToUInt32(p[27..31]),
            TargetBitrateKbps: BitConverter.ToUInt32(p[31..35]),
            SkippedForBackpressure: BitConverter.ToUInt32(p[35..39]),
            DroppedFrames: BitConverter.ToUInt32(p[39..43]),
            MaxQp: p[43],
            EncoderMsAvg: hasV2 ? BitConverter.ToSingle(p[44..48]) : 0,
            LowLatency: hasV2 && (p[48] & 1) != 0,
            YuvCapture: hasV2 && (p[48] & 2) != 0,
            HasV2Fields: hasV2,
            ExtraDisplay: hasV2 && (p[48] & 4) != 0);
        return true;
    }

    /// <summary>Ping/pong payload: kind byte + 8-byte token (the client's Stopwatch timestamp).</summary>
    public static byte[] CreatePing(long token)
    {
        var payload = new byte[9];
        payload[0] = KindPing;
        BitConverter.TryWriteBytes(payload.AsSpan(1), token);
        return payload;
    }

    public static bool TryParsePong(ReadOnlySpan<byte> p, out long token)
    {
        token = 0;
        if (p.Length != 9 || p[0] != KindPong)
            return false;
        token = BitConverter.ToInt64(p[1..9]);
        return true;
    }
}

/// <summary>
/// Builders for INPUT_EVENT (0x0040, client -> Mac) payloads. Little endian. Coordinates are
/// fractions (0..1) of the video picture, so they don't depend on window size or DPI; the Mac
/// maps them onto the streamed display. See Protocol/PROTOCOL.md.
/// </summary>
public static class MacInputPayload
{
    public const byte MouseMoveKind = 0x01;
    public const byte MouseDownKind = 0x02;
    public const byte MouseUpKind = 0x03;
    public const byte KeyDownKind = 0x04;
    public const byte KeyUpKind = 0x05;
    public const byte ScrollKind = 0x06;
    public const byte ReleaseAllKind = 0x07;
    public const byte HeartbeatKind = 0x08;
    public const byte OwnScreenEnterKind = 0x09;
    public const byte RelativeMoveKind = 0x0A;

    public static byte[] MouseMove(float x, float y)
    {
        var p = new byte[9];
        p[0] = MouseMoveKind;
        BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(1), x);
        BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(5), y);
        return p;
    }

    /// <param name="button">0 = left, 1 = right, 2 = middle.</param>
    public static byte[] MouseButton(bool down, byte button, byte clickCount, float x, float y)
    {
        var p = new byte[11];
        p[0] = down ? MouseDownKind : MouseUpKind;
        p[1] = button;
        p[2] = clickCount;
        BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(3), x);
        BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(7), y);
        return p;
    }

    /// <summary>Pixels; +dy scrolls up (like pushing the wheel away), +dx scrolls right.</summary>
    public static byte[] Scroll(int dx, int dy)
    {
        var p = new byte[9];
        p[0] = ScrollKind;
        BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(1), dx);
        BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(5), dy);
        return p;
    }

    /// <param name="macKeyCode">Mac virtual key code (kVK_*, 0-127); see Input/MacKeyMap.cs.</param>
    public static byte[] Key(bool down, ushort macKeyCode)
    {
        var p = new byte[3];
        p[0] = down ? KeyDownKind : KeyUpKind;
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(1), macKeyCode);
        return p;
    }

    public static byte[] ReleaseAll() => new[] { ReleaseAllKind };

    /// <summary>The mouse left this PC's monitor towards the Mac's own screen.</summary>
    /// <param name="side">Where the Mac is: 0 = left of the PC, 1 = right, 2 = above, 3 = below.</param>
    /// <param name="along">0..1 along that edge (top to bottom, or left to right).</param>
    public static byte[] OwnScreenEnter(byte side, float along)
    {
        var p = new byte[6];
        p[0] = OwnScreenEnterKind;
        p[1] = side;
        BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(2), along);
        return p;
    }

    /// <summary>Mouse movement on the Mac's own screen, in pixels of the video picture.</summary>
    public static byte[] RelativeMove(float dx, float dy)
    {
        var p = new byte[9];
        p[0] = RelativeMoveKind;
        BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(1), dx);
        BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(5), dy);
        return p;
    }

    public static byte[] Heartbeat() => new[] { HeartbeatKind };
}

/// <summary>
/// CLIPBOARD (0x0080) payloads, both directions. u8 kind: 0x01 sync on and 0x02 sync off
/// (PC -> Mac only), 0x03 text (u8 kind + UTF-8 text), 0x04 image (u8 kind + PNG file).
/// See Protocol/PROTOCOL.md.
/// </summary>
public static class ClipboardPayload
{
    public const byte SyncOnKind = 0x01;
    public const byte SyncOffKind = 0x02;
    public const byte TextKind = 0x03;
    public const byte ImageKind = 0x04;

    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    public static byte[] Sync(bool on) => new[] { on ? SyncOnKind : SyncOffKind };

    public static bool IsPng(ReadOnlySpan<byte> data) => data.Length > PngSignature.Length && data.StartsWith(PngSignature);

    public static byte[] Image(byte[] png)
    {
        var p = new byte[1 + png.Length];
        p[0] = ImageKind;
        Buffer.BlockCopy(png, 0, p, 1, png.Length);
        return p;
    }

    /// <summary>The PNG bytes of an image payload (only checks the kind, size and PNG signature;
    /// the caller decodes it, which validates the rest).</summary>
    public static bool TryReadImage(byte[] payload, int maxBytes, out byte[] png)
    {
        png = Array.Empty<byte>();
        if (payload.Length < 2 || payload[0] != ImageKind || payload.Length - 1 > maxBytes ||
            !IsPng(payload.AsSpan(1)))
            return false;
        png = payload[1..];
        return true;
    }

    public static byte[] Text(string text)
    {
        var count = System.Text.Encoding.UTF8.GetByteCount(text);
        var p = new byte[1 + count];
        p[0] = TextKind;
        System.Text.Encoding.UTF8.GetBytes(text, 0, text.Length, p, 1);
        return p;
    }

    public static bool TryReadText(byte[] payload, int maxBytes, out string text)
    {
        text = "";
        if (payload.Length < 2 || payload[0] != TextKind || payload.Length - 1 > maxBytes)
            return false;
        try
        {
            text = new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true).GetString(payload, 1, payload.Length - 1);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
