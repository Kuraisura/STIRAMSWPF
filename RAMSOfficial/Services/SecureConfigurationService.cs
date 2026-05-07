using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace RAMSOfficial.Services;

/// <summary>
/// Encrypts and decrypts configuration values using Windows DPAPI
/// (Data Protection API). Values are bound to the current Windows user
/// account and cannot be decrypted by another user or on another machine.
///
/// A08 – Obfuscation Ready:
///   • API endpoints and keys are never stored as plain-text strings in the binary.
///   • At rest the values live in a DPAPI-encrypted file under %LOCALAPPDATA%.
///   • At runtime the decrypted value exists only for the duration of the call.
///
/// Usage (one-time admin setup):
///   SecureConfigurationService.StoreEncryptedValue("ApiBaseUrl", "https://api.example.com");
///
/// Usage (runtime):
///   var url = SecureConfigurationService.GetDecryptedValue("ApiBaseUrl");
/// </summary>
public static class SecureConfigurationService
{
    private static readonly string ConfigDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RAMSOfficial");

    private static readonly string ConfigPath = Path.Combine(ConfigDirectory, "config.enc");

    private static readonly object _lock = new();

    /// <summary>
    /// Encrypt <paramref name="value"/> with DPAPI and persist it under <paramref name="key"/>.
    /// </summary>
    public static void StoreEncryptedValue(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        var plainBytes = Encoding.UTF8.GetBytes(value);
        var cipherBytes = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
        var base64 = Convert.ToBase64String(cipherBytes);

        // Zero out the plain-text byte array immediately
        Array.Clear(plainBytes, 0, plainBytes.Length);

        lock (_lock)
        {
            Directory.CreateDirectory(ConfigDirectory);
            var entries = LoadEntries();
            entries[key] = base64;
            WriteEntries(entries);
        }

        Debug.WriteLine($"[SecureConfig] Stored encrypted value for key '{key}'.");
    }

    /// <summary>
    /// Retrieve and decrypt the value stored under <paramref name="key"/>.
    /// Returns <c>null</c> if the key does not exist.
    /// </summary>
    public static string? GetDecryptedValue(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        Dictionary<string, string> entries;
        lock (_lock)
        {
            entries = LoadEntries();
        }

        if (!entries.TryGetValue(key, out var base64))
            return null;

        try
        {
            var cipherBytes = Convert.FromBase64String(base64);
            var plainBytes = ProtectedData.Unprotect(cipherBytes, null, DataProtectionScope.CurrentUser);
            var result = Encoding.UTF8.GetString(plainBytes);

            // Zero out the decrypted byte array
            Array.Clear(plainBytes, 0, plainBytes.Length);
            return result;
        }
        catch (CryptographicException ex)
        {
            Debug.WriteLine($"[SecureConfig] Decryption failed for key '{key}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Returns <c>true</c> if a value has been stored for <paramref name="key"/>.
    /// </summary>
    public static bool HasValue(string key)
    {
        lock (_lock)
        {
            return LoadEntries().ContainsKey(key);
        }
    }

    // ────────────────── private helpers ──────────────────

    private static Dictionary<string, string> LoadEntries()
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(ConfigPath))
            return dict;

        foreach (var line in File.ReadAllLines(ConfigPath))
        {
            var separatorIndex = line.IndexOf('|');
            if (separatorIndex > 0 && separatorIndex < line.Length - 1)
            {
                var k = line[..separatorIndex];
                var v = line[(separatorIndex + 1)..];
                dict[k] = v;
            }
        }

        return dict;
    }

    private static void WriteEntries(Dictionary<string, string> entries)
    {
        var lines = entries.Select(kv => $"{kv.Key}|{kv.Value}");
        File.WriteAllLines(ConfigPath, lines);
    }
}
