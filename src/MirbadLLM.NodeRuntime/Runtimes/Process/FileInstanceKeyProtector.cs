using System.Security.Cryptography;
using System.Text;
using MirbadLLM.NodeRuntime.Abstractions;

namespace MirbadLLM.NodeRuntime.Runtimes.Process;

/// <summary>
/// The per-instance API key at rest on a node without DPAPI (macOS; docs/macos/implementation-plan.md M3): AES-256-GCM
/// under a random key kept in <c>{state}/instance-keys.key</c>, created on first use with mode 0600 (the agent's state
/// folder is 0700 and its own). The protected form is <c>v1:</c> + base64(nonce ‖ ciphertext ‖ tag), so
/// <c>processes.json</c> never holds a clear key and a copy of it alone (a backup, a support bundle) reveals none. A daemon
/// account has no unlocked login keychain, hence a file. Clear key bytes are zeroed after use.
/// </summary>
public sealed class FileInstanceKeyProtector : IInstanceKeyProtector
{
    /// <summary>The key file's name in the state directory.</summary>
    public const string KeyFileName = "instance-keys.key";

    private const string Prefix = "v1:";
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    private readonly string _keyPath;
    private readonly Lazy<byte[]> _key;

    /// <summary>Protects with the key in <paramref name="stateDirectory"/> (created when missing).</summary>
    public FileInstanceKeyProtector(string stateDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(stateDirectory);
        _keyPath = Path.Combine(stateDirectory, KeyFileName);
        _key = new Lazy<byte[]>(LoadOrCreateKey, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <inheritdoc />
    public string Protect(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        byte[] clear = Encoding.UTF8.GetBytes(key);
        byte[] blob = new byte[NonceBytes + clear.Length + TagBytes];
        try
        {
            Span<byte> nonce = blob.AsSpan(0, NonceBytes);
            RandomNumberGenerator.Fill(nonce);
            using var aes = new AesGcm(_key.Value, TagBytes);
            aes.Encrypt(nonce, clear, blob.AsSpan(NonceBytes, clear.Length), blob.AsSpan(NonceBytes + clear.Length, TagBytes));
            return Prefix + Convert.ToBase64String(blob);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
        }
    }

    /// <inheritdoc />
    public string Unprotect(string protectedKey)
    {
        ArgumentNullException.ThrowIfNull(protectedKey);
        if (!protectedKey.StartsWith(Prefix, StringComparison.Ordinal))
        {
            throw new CryptographicException("The protected key is not in the v1 format.");
        }

        byte[] blob;
        try
        {
            blob = Convert.FromBase64String(protectedKey[Prefix.Length..]);
        }
        catch (FormatException ex)
        {
            throw new CryptographicException("The protected key is not valid base64.", ex);
        }

        if (blob.Length < NonceBytes + TagBytes)
        {
            throw new CryptographicException("The protected key is too short.");
        }

        int length = blob.Length - NonceBytes - TagBytes;
        byte[] clear = new byte[length];
        try
        {
            using var aes = new AesGcm(_key.Value, TagBytes);
            aes.Decrypt(blob.AsSpan(0, NonceBytes), blob.AsSpan(NonceBytes, length), blob.AsSpan(NonceBytes + length, TagBytes), clear);
            return Encoding.UTF8.GetString(clear);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
        }
    }

    private byte[] LoadOrCreateKey()
    {
        if (File.Exists(_keyPath))
        {
            byte[] existing = File.ReadAllBytes(_keyPath);
            return existing.Length == 32 ? existing : throw new CryptographicException($"{_keyPath} does not hold a 256-bit key.");
        }

        byte[] key = RandomNumberGenerator.GetBytes(32);
        Directory.CreateDirectory(Path.GetDirectoryName(_keyPath)!);
        string temp = _keyPath + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Options = FileOptions.WriteThrough };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        if (File.Exists(temp))
        {
            File.Delete(temp);
        }

        using (var file = new FileStream(temp, options))
        {
            file.Write(key);
            file.Flush(flushToDisk: true);
        }

        try
        {
            File.Move(temp, _keyPath, overwrite: false);
        }
        catch (IOException) when (File.Exists(_keyPath))
        {
            // Another instance created it first: use that one.
            File.Delete(temp);
            return File.ReadAllBytes(_keyPath);
        }

        return key;
    }
}
