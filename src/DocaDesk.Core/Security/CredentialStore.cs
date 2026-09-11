using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace DocaDesk.Core.Security;

public interface ICredentialStore
{
    Task SaveAsync(string key, string value, CancellationToken cancellationToken = default);
    Task<string?> LoadAsync(string key, CancellationToken cancellationToken = default);
    Task DeleteAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>
/// DPAPI CurrentUser-scoped credential store under %LOCALAPPDATA%\DocaDesk\credentials\.
/// Prefer this over plaintext files; Windows Credential Manager wrapper can replace later.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiCredentialStore : ICredentialStore
{
    private readonly string _dir;

    public DpapiCredentialStore(string? rootDirectory = null)
    {
        _dir = rootDirectory
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DocaDesk",
                "credentials");
        Directory.CreateDirectory(_dir);
    }

    public Task SaveAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var plain = Encoding.UTF8.GetBytes(value);
        var protectedBytes = ProtectedData.Protect(plain, optionalEntropy: null, DataProtectionScope.CurrentUser);
        var path = PathFor(key);
        File.WriteAllBytes(path, protectedBytes);
        return Task.CompletedTask;
    }

    public Task<string?> LoadAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var path = PathFor(key);
        if (!File.Exists(path))
            return Task.FromResult<string?>(null);

        var protectedBytes = File.ReadAllBytes(path);
        var plain = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
        return Task.FromResult<string?>(Encoding.UTF8.GetString(plain));
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var path = PathFor(key);
        if (File.Exists(path))
            File.Delete(path);
        return Task.CompletedTask;
    }

    private string PathFor(string key)
    {
        var safe = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        return Path.Combine(_dir, safe + ".bin");
    }
}

/// <summary>In-memory store for tests and non-Windows hosts.</summary>
public sealed class MemoryCredentialStore : ICredentialStore
{
    private readonly Dictionary<string, string> _map = new(StringComparer.Ordinal);

    public Task SaveAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        _map[key] = value;
        return Task.CompletedTask;
    }

    public Task<string?> LoadAsync(string key, CancellationToken cancellationToken = default)
    {
        _map.TryGetValue(key, out var v);
        return Task.FromResult(v);
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        _map.Remove(key);
        return Task.CompletedTask;
    }
}

public static class CredentialKeys
{
    public const string DeviceToken = "device.token";
    public const string McpPathSecret = "mcp.path.secret";
    public const string CertPinSha256 = "tls.pin.sha256";
    public const string ServerUrl = "server.url";
}
