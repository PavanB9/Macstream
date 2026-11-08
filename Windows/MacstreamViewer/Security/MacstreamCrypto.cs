using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace MacstreamViewer.Security;

/// <summary>
/// Pairing/encryption primitives shared with the Mac (Mac/.../SecureChannel.swift). Both sides
/// must compute byte-for-byte the same values; see Protocol/PROTOCOL.md, "Security handshake".
/// </summary>
public static class MacstreamCrypto
{
    public const byte Version = 1;
    public const int PublicKeySize = 65;   // X9.63 uncompressed P-256 point
    public const int NonceSize = 32;
    public const int MaxNameBytes = 64;

    public static byte[] Sha256(params byte[][] parts)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var part in parts)
            hash.AppendData(part);
        return hash.GetHashAndReset();
    }

    public static byte[] Label(string text) => Encoding.UTF8.GetBytes(text);

    public static byte[] Commitment(byte[] serverStatic, byte[] clientStatic, byte[] serverPairNonce) =>
        Sha256(Label("macstream commit v1"), serverStatic, clientStatic, serverPairNonce);

    /// <summary>The 6-digit code both screens show, e.g. "042 917".</summary>
    public static string PairingCode(byte[] transcript)
    {
        var h = Sha256(Label("macstream pairing code v1"), transcript);
        var value = BinaryPrimitives.ReadUInt32BigEndian(h) % 1_000_000;
        return $"{value / 1000:D3} {value % 1000:D3}";
    }

    public static (byte[] ClientToServer, byte[] ServerToClient) SessionKeys(
        byte[] ss, byte[] ee, byte[] es, byte[] se, byte[] transcript)
    {
        var salt = Sha256(Label("macstream session v1"), transcript);
        var ikm = Concat(ss, ee, es, se);
        var okm = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 64, salt, Label("macstream v1 keys"));
        CryptographicOperations.ZeroMemory(ikm);
        return (okm[..32], okm[32..]);
    }

    public static ECDiffieHellman NewKey() => ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

    /// <summary>0x04 | X | Y, the format CryptoKit calls x963Representation.</summary>
    public static byte[] ExportPublic(ECDiffieHellman key)
    {
        var p = key.ExportParameters(includePrivateParameters: false);
        return Concat(new byte[] { 0x04 }, p.Q.X!, p.Q.Y!);
    }

    /// <summary>Imports a peer's public key; throws if it is not a valid P-256 point.</summary>
    public static ECDiffieHellman ImportPublic(byte[] x963)
    {
        if (x963.Length != PublicKeySize || x963[0] != 0x04)
            throw new CryptographicException("Not an uncompressed P-256 public key.");
        var parameters = new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = x963[1..33], Y = x963[33..65] },
        };
        parameters.Validate();
        return ECDiffieHellman.Create(parameters);
    }

    public static byte[] SharedSecret(ECDiffieHellman privateKey, ECDiffieHellman publicKey) =>
        privateKey.DeriveRawSecretAgreement(publicKey.PublicKey);

    public static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(p => p.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }
        return result;
    }
}

/// <summary>The Mac refused this PC, pairing failed, or the Mac is not the one paired with.</summary>
public sealed class MacstreamAuthException : Exception
{
    public MacstreamAuthException(string message, bool retryable) : base(message) => Retryable = retryable;

    /// <summary>False when trying again automatically would not help (pairing refused...).</summary>
    public bool Retryable { get; }
}
