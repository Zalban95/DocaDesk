using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace DocaDesk.Core.Security;

/// <summary>
/// Certificate trust matrix (§7.2):
/// - Normal public CA (incl. Tailscale ts.net): default validation, no custom callback needed.
/// - Pinned + matching: accept.
/// - Pinned + mismatch: fail hard, no CA fallback.
/// - Unpinned self-signed: refuse.
/// Never return true unconditionally from a custom callback.
/// </summary>
public sealed class CertificatePolicy
{
    private readonly string? _pinnedSha256Hex;

    public CertificatePolicy(string? pinnedSha256Hex = null)
    {
        _pinnedSha256Hex = Normalize(pinnedSha256Hex);
    }

    public string? PinnedSha256Hex => _pinnedSha256Hex;

    public bool HasPin => !string.IsNullOrEmpty(_pinnedSha256Hex);

    public static string FingerprintSha256(X509Certificate2 cert)
    {
        var hash = SHA256.HashData(cert.RawData);
        return Convert.ToHexString(hash);
    }

    public bool Validate(
        X509Certificate2? certificate,
        X509Chain? chain,
        SslPolicyErrors sslPolicyErrors)
    {
        if (certificate is null)
            return false;

        var fp = FingerprintSha256(certificate);

        if (HasPin)
        {
            // Pin match wins even if CA would fail (explicit user pin at pairing).
            // Pin mismatch fails hard with no CA fallback.
            return string.Equals(fp, _pinnedSha256Hex, StringComparison.OrdinalIgnoreCase);
        }

        // No pin: only default CA trust. Self-signed (or any policy error) refused.
        return sslPolicyErrors == SslPolicyErrors.None;
    }

    private static string? Normalize(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return null;
        return hex.Replace(":", "", StringComparison.Ordinal)
            .Replace(" ", "", StringComparison.Ordinal)
            .ToUpperInvariant();
    }
}

/// <summary>Five cases from Wear brief / Desk §7.2 for tests.</summary>
public enum CertMatrixCase
{
    PublicCaOk,
    PublicCaPinnedMatch,
    PublicCaPinnedMismatch,
    SelfSignedPinnedMatch,
    SelfSignedUnpinnedRefuse,
}
