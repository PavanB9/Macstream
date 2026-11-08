using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using MacstreamViewer.Diagnostics;
using MacstreamViewer.Protocol;

namespace MacstreamViewer.Security;

/// <summary>
/// The viewer's side of the security handshake (matches Mac/.../ServerHandshake.swift).
/// Runs right after the TCP connection opens, before HELLO; returns the encrypted stream.
/// </summary>
public static class ClientHandshake
{
    private const byte StatusKnown = 0, StatusPairingRequired = 1, StatusRefused = 2;
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan UserTimeout = TimeSpan.FromSeconds(125);

    /// <param name="confirmPairing">
    /// First connection only: shows the code and asks the user whether the Mac shows the same
    /// one. Pairing needs a yes on both machines; null means no.
    /// </param>
    public static async Task<EncryptedStream> RunAsync(Stream net, PairingStore store, string deviceName,
        Func<string, Task<bool>>? confirmPairing, int maxIncomingRecord, CancellationToken ct)
    {
        if (store.LoadError != null)
            throw new MacstreamAuthException(store.LoadError, retryable: false);
        using var clientStatic = store.OpenClientKey();
        using var clientEphemeral = MacstreamCrypto.NewKey();
        var clientStaticPublic = store.ClientPublic;

        // u8 version, static key, ephemeral key, nonce, u8 nameLen, name
        var name = Utf8Prefix(deviceName, MacstreamCrypto.MaxNameBytes);
        var clientHello = MacstreamCrypto.Concat(
            new[] { MacstreamCrypto.Version },
            clientStaticPublic,
            MacstreamCrypto.ExportPublic(clientEphemeral),
            RandomNumberGenerator.GetBytes(MacstreamCrypto.NonceSize),
            new[] { (byte)name.Length },
            name);
        await WritePacketAsync(net, PacketType.AuthClientHello, clientHello, ct);

        // u8 version, u8 status, server static key, server ephemeral key, nonce, [commitment]
        var serverHello = await ReadPacketAsync(net, PacketType.AuthServerHello, StepTimeout, ct);
        const int fixedLength = 2 + 2 * MacstreamCrypto.PublicKeySize + MacstreamCrypto.NonceSize;
        if (serverHello.Length < fixedLength || serverHello[0] != MacstreamCrypto.Version)
            throw new MacstreamAuthException("The Mac runs a different Macstream version; update both.", retryable: false);
        var status = serverHello[1];
        var serverStatic = serverHello[2..(2 + MacstreamCrypto.PublicKeySize)];
        var serverEphemeral = serverHello[(2 + MacstreamCrypto.PublicKeySize)..(2 + 2 * MacstreamCrypto.PublicKeySize)];

        byte[] clientPairNonce = Array.Empty<byte>(), serverPairNonce = Array.Empty<byte>();
        var newlyPaired = false;
        switch (status)
        {
            case StatusKnown when serverHello.Length == fixedLength:
                var known = store.MacPublic;
                if (known == null || !CryptographicOperations.FixedTimeEquals(known, serverStatic))
                    throw new MacstreamAuthException(
                        "This Mac is not the one this PC paired with. To pair again, click Forget this Mac in Settings "
                        + "(toolbar or tray), then Connect.", retryable: false);
                break;

            case StatusPairingRequired when serverHello.Length == fixedLength + 32:
                // Already paired with a Mac: only that same Mac may ask to pair again (e.g. after
                // unpair.sh). A different key could be a device posing as the Mac, so stop.
                var pinned = store.MacPublic;
                if (pinned != null && !CryptographicOperations.FixedTimeEquals(pinned, serverStatic))
                    throw new MacstreamAuthException(
                        "A different device is asking this PC to pair. If you reinstalled Macstream on the Mac, "
                        + "click Forget this Mac in Settings (toolbar or tray) and connect again.", retryable: false);
                var commitment = serverHello[fixedLength..];
                clientPairNonce = RandomNumberGenerator.GetBytes(MacstreamCrypto.NonceSize);
                await WritePacketAsync(net, PacketType.PairNonce, clientPairNonce, ct);
                serverPairNonce = await ReadPacketAsync(net, PacketType.PairReveal, StepTimeout, ct);
                if (serverPairNonce.Length != MacstreamCrypto.NonceSize ||
                    !CryptographicOperations.FixedTimeEquals(commitment,
                        MacstreamCrypto.Commitment(serverStatic, clientStaticPublic, serverPairNonce)))
                    throw new MacstreamAuthException("Pairing failed: the Mac's answer did not check out. Try again.", retryable: false);

                var code = MacstreamCrypto.PairingCode(
                    MacstreamCrypto.Concat(clientHello, serverHello, clientPairNonce, serverPairNonce));
                DiagLog.Write($"Pairing with the Mac: waiting for the user on both machines (code {code})");

                // Both sides must say yes. The PC's own check matters: without it, a device
                // pretending to be the Mac could simply answer "allowed" itself.
                var resultTask = ReadPacketAsync(net, PacketType.PairResult, UserTimeout, ct);
                bool confirmed;
                try
                {
                    // Bounded like the Mac's alert: an unanswered question means no.
                    confirmed = confirmPairing != null && await confirmPairing(code).WaitAsync(UserTimeout, ct);
                }
                catch (TimeoutException)
                {
                    confirmed = false;
                }
                if (!confirmed)
                {
                    _ = resultTask.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                    throw new MacstreamAuthException("Pairing cancelled on this PC.", retryable: false);
                }
                var result = await resultTask;
                if (result.Length != 1 || result[0] != 1)
                    throw new MacstreamAuthException("Pairing was not allowed on the Mac.", retryable: false);
                newlyPaired = true;
                break;

            case StatusRefused:
                throw new MacstreamAuthException(
                    "The Mac is showing another pairing request, or one was just refused. Try again in a few seconds.",
                    retryable: true);

            default:
                throw new MacstreamAuthException("The Mac sent an unexpected handshake reply.", retryable: false);
        }

        using var serverStaticKey = MacstreamCrypto.ImportPublic(serverStatic);
        using var serverEphemeralKey = MacstreamCrypto.ImportPublic(serverEphemeral);
        var transcript = MacstreamCrypto.Concat(clientHello, serverHello, clientPairNonce, serverPairNonce);
        var keys = MacstreamCrypto.SessionKeys(
            ss: MacstreamCrypto.SharedSecret(clientStatic, serverStaticKey),
            ee: MacstreamCrypto.SharedSecret(clientEphemeral, serverEphemeralKey),
            es: MacstreamCrypto.SharedSecret(clientEphemeral, serverStaticKey),
            se: MacstreamCrypto.SharedSecret(clientStatic, serverEphemeralKey),
            transcript);
        DiagLog.Write("Security handshake done: paired Mac, encrypted connection");
        var stream = new EncryptedStream(net, sendKey: keys.ClientToServer, receiveKey: keys.ServerToClient, maxIncomingRecord);
        if (newlyPaired)
        {
            // Remember this Mac only once it has proved it holds the matching keys: the first
            // record from it decrypts. A plaintext "allowed" alone is never trusted.
            stream.FirstRecordAuthenticated += () => store.RememberMac(serverStatic, "Mac");
        }
        return stream;
    }

    private static byte[] Utf8Prefix(string text, int maxBytes)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length <= maxBytes)
            return bytes;
        // Cut on a character boundary.
        var length = maxBytes;
        while (length > 0 && (bytes[length] & 0xC0) == 0x80)
            length--;
        return bytes[..length];
    }

    private static async Task WritePacketAsync(Stream net, PacketType type, byte[] payload, CancellationToken ct)
    {
        var header = new PacketHeader
        {
            Magic = ProtocolConstants.Magic,
            Version = ProtocolConstants.Version,
            Type = type,
            PayloadLength = (uint)payload.Length,
        }.Encode();
        await net.WriteAsync(MacstreamCrypto.Concat(header, payload), ct);
        await net.FlushAsync(ct);
    }

    private static async Task<byte[]> ReadPacketAsync(Stream net, PacketType expected, TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            var header = new byte[ProtocolConstants.HeaderSize];
            await net.ReadExactlyAsync(header, timeoutCts.Token);
            if (!PacketHeader.TryDecode(header, out var decoded) || decoded.Type != expected || decoded.PayloadLength > 1024)
                throw new MacstreamAuthException(
                    "The Mac did not answer the security handshake. Update the Mac server (git pull, build-server.sh).",
                    retryable: false);
            var payload = new byte[decoded.PayloadLength];
            await net.ReadExactlyAsync(payload, timeoutCts.Token);
            return payload;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new MacstreamAuthException(expected == PacketType.PairResult
                ? "Nobody clicked Allow on the Mac in time."
                : "The Mac did not answer the security handshake in time.", retryable: expected != PacketType.PairResult);
        }
        catch (EndOfStreamException)
        {
            throw new MacstreamAuthException(expected == PacketType.PairResult
                ? "Pairing was not allowed on the Mac."
                : "The Mac closed the connection during the security handshake.", retryable: expected != PacketType.PairResult);
        }
    }
}
