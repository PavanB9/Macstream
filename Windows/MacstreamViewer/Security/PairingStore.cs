using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using MacstreamViewer.Diagnostics;

namespace MacstreamViewer.Security;

/// <summary>
/// This PC's pairing key and the Mac it is paired with. Stored in the project folder at
/// .local\pairing\windows-pairing.dat, encrypted with Windows DPAPI for the current user
/// (so the file is useless to other users or other PCs). "Forget this Mac" in Settings
/// (or deleting that file) makes the PC pair again.
/// </summary>
public sealed class PairingStore
{
    private static readonly byte[] Entropy = MacstreamCrypto.Label("Macstream pairing v1");
    private static readonly Lazy<PairingStore> SharedStore = new(() => new PairingStore(DefaultPath()));

    private readonly string _path;
    private readonly object _lock = new();
    private byte[] _clientPrivate;      // P-256 private scalar (D)
    private byte[] _clientPublic;       // X9.63
    private byte[]? _macPublic;         // X9.63, null until paired
    private string? _macName;

    public static PairingStore Shared => SharedStore.Value;

    /// <summary>
    /// Set when the pairing file exists but cannot be read (damaged, or from another Windows
    /// user). Then nothing is overwritten and no connection is made (fail closed): silently
    /// making a new key would also forget which Mac this PC trusts.
    /// </summary>
    public string? LoadError { get; private set; }

    private sealed record Saved(int V, string ClientPrivate, string ClientPublic, string? MacPublic, string? MacName);

    private PairingStore(string path)
    {
        _path = path;
        if (TryLoad(out var saved))
        {
            _clientPrivate = Convert.FromBase64String(saved.ClientPrivate);
            _clientPublic = Convert.FromBase64String(saved.ClientPublic);
            _macPublic = saved.MacPublic == null ? null : Convert.FromBase64String(saved.MacPublic);
            _macName = saved.MacName;
            return;
        }

        using var key = MacstreamCrypto.NewKey();
        _clientPrivate = key.ExportParameters(true).D!;
        _clientPublic = MacstreamCrypto.ExportPublic(key);
        if (File.Exists(_path))
        {
            LoadError = $"The pairing file {_path} can't be read (damaged, or made by another Windows user). "
                + "For safety Macstream won't connect. Click Forget this Mac in Settings to pair this PC with the Mac again.";
            DiagLog.Write(LoadError);
            return;
        }
        Save();
        DiagLog.Write($"Created this PC's pairing key ({_path})");
    }

    public byte[] ClientPublic
    {
        get { lock (_lock) return _clientPublic; }
    }

    public byte[]? MacPublic
    {
        get { lock (_lock) return _macPublic; }
    }

    /// <summary>Short fingerprint of the paired Mac's key ("AB12 CD34"), or null when not paired.
    /// The Mac's menu bar shows the same for its own key.</summary>
    public string? MacFingerprint => MacPublic is { } key ? Fingerprint(key) : null;

    /// <summary>First 4 bytes of SHA-256 of an X9.63 public key, as "AB12 CD34" (display only).</summary>
    public static string Fingerprint(byte[] publicKey)
    {
        var hex = Convert.ToHexString(SHA256.HashData(publicKey), 0, 4);
        return $"{hex[..4]} {hex[4..]}";
    }

    /// <summary>
    /// Forgets the paired Mac and makes a new key for this PC, so the next connection asks
    /// for the 6-digit code on both machines again. (A new key is needed because the Mac
    /// would otherwise still recognise this PC while this PC no longer knows the Mac.)
    /// Also repairs an unreadable pairing file. Disconnect first.
    /// </summary>
    public void Forget()
    {
        using var key = MacstreamCrypto.NewKey();
        lock (_lock)
        {
            CryptographicOperations.ZeroMemory(_clientPrivate);
            _clientPrivate = key.ExportParameters(true).D!;
            _clientPublic = MacstreamCrypto.ExportPublic(key);
            _macPublic = null;
            _macName = null;
            LoadError = null;
            Save();
        }
        DiagLog.Write("Forgot the paired Mac; made a new key for this PC (next connection pairs again)");
    }

    /// <summary>A fresh ECDiffieHellman holding this PC's private key (dispose it after use).</summary>
    public ECDiffieHellman OpenClientKey()
    {
        lock (_lock)
            return OpenClientKeyLocked();
    }

    private ECDiffieHellman OpenClientKeyLocked()
    {
        var parameters = new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = _clientPrivate,
            Q = new ECPoint { X = _clientPublic[1..33], Y = _clientPublic[33..65] },
        };
        return ECDiffieHellman.Create(parameters);
    }

    public void RememberMac(byte[] macPublic, string name)
    {
        if (LoadError != null)
            return;
        lock (_lock)
        {
            _macPublic = macPublic;
            _macName = name;
            Save();
        }
        DiagLog.Write("Paired with the Mac");
    }

    private void Save()
    {
        var saved = new Saved(1,
            Convert.ToBase64String(_clientPrivate),
            Convert.ToBase64String(_clientPublic),
            _macPublic == null ? null : Convert.ToBase64String(_macPublic),
            _macName);
        var plain = JsonSerializer.SerializeToUtf8Bytes(saved);
        try
        {
            var protectedBytes = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllBytes(temp, protectedBytes);
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            DiagLog.Write($"Could not save the pairing file ({ex.Message}); this PC will have to pair again next time");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private bool TryLoad(out Saved saved)
    {
        saved = null!;
        if (!File.Exists(_path))
            return false;
        try
        {
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy, DataProtectionScope.CurrentUser);
            saved = JsonSerializer.Deserialize<Saved>(plain)!;
            CryptographicOperations.ZeroMemory(plain);
            return saved is { V: 1, ClientPrivate: not null, ClientPublic: not null };
        }
        catch (Exception ex)
        {
            DiagLog.Write($"Pairing file unreadable: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// The project's .local\pairing folder (found by walking up from the app to the folder
    /// holding global.json), or the app folder if the app runs somewhere else.
    /// </summary>
    private static string DefaultPath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "global.json")) &&
                Directory.Exists(Path.Combine(dir.FullName, "tools")))
                return Path.Combine(dir.FullName, ".local", "pairing", "windows-pairing.dat");
        }
        return Path.Combine(AppContext.BaseDirectory, "windows-pairing.dat");
    }
}
