using DocaDesk.Core.Logging;
using DocaDesk.Core.Models;
using DocaDesk.Core.Push;
using DocaDesk.Core.Security;

namespace DocaDesk.Tests;

public class CursorAndSelectionTests
{
    [Fact]
    public async Task Cursor_persists_across_store_instances()
    {
        var path = Path.Combine(Path.GetTempPath(), "docadesk-cursor-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            var a = new FileCursorStore(path);
            await a.SaveAsync(42);
            var b = new FileCursorStore(path);
            Assert.Equal(42, await b.LoadAsync());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Seq_gaps_are_tolerated_when_advancing()
    {
        var store = new MemoryCursorStore();
        await store.SaveAsync(10);
        await store.SaveAsync(15); // gap 11-14
        Assert.Equal(15, await store.LoadAsync());
        await store.SaveAsync(12); // older — must not retreat
        Assert.Equal(15, await store.LoadAsync());
    }

    [Fact]
    public void SelectionId_idempotent_until_back()
    {
        var t = new SelectionIdTracker();
        var first = t.Ensure();
        Assert.Equal(first, t.Current);
        Assert.Equal(first, t.Ensure());
        var afterBack = t.AfterBack();
        Assert.NotEqual(first, afterBack);
        Assert.Equal(afterBack, t.Current);
    }

    [Fact]
    public void Heartbeat_watchdog_expires_after_2x_heartbeat_on_fake_clock()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2020-01-01T00:00:00Z"));
        var wd = new HeartbeatWatchdog(clock);
        wd.Configure(heartbeatSec: 25);
        Assert.Equal(TimeSpan.FromSeconds(50), wd.Timeout);
        Assert.False(wd.IsExpired);
        clock.Advance(TimeSpan.FromSeconds(49));
        Assert.False(wd.IsExpired);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(wd.IsExpired);
        wd.MarkTraffic();
        Assert.False(wd.IsExpired);
    }
}

public class CredentialAndRedactionTests
{
    [Fact]
    public async Task Credential_round_trip_memory()
    {
        var store = new MemoryCredentialStore();
        await store.SaveAsync(CredentialKeys.DeviceToken, "doca_dev_abc.secret");
        Assert.Equal("doca_dev_abc.secret", await store.LoadAsync(CredentialKeys.DeviceToken));
        await store.DeleteAsync(CredentialKeys.DeviceToken);
        Assert.Null(await store.LoadAsync(CredentialKeys.DeviceToken));
    }

    [Fact]
    public async Task Credential_round_trip_dpapi()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var dir = Path.Combine(Path.GetTempPath(), "docadesk-cred-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new DpapiCredentialStore(dir);
            await store.SaveAsync("t", "doca_x.y");
            Assert.Equal("doca_x.y", await store.LoadAsync("t"));
            await store.DeleteAsync("t");
            Assert.Null(await store.LoadAsync("t"));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Logger_redacts_token_mcp_path_and_clipboard()
    {
        var s = RedactingLogger.Redact(
            "Authorization: Bearer doca_dev_abc.secretTOKEN and /mcp/abcdefghijklmnopqrstuvwxyz012345 clipboard: hunter2 password: x");
        Assert.DoesNotContain("secretTOKEN", s);
        Assert.DoesNotContain("abcdefghijklmnopqrstuvwxyz012345", s);
        Assert.Contains("[REDACTED]", s);
        Assert.DoesNotContain("hunter2", s);
    }
}

public class CertificateMatrixTests
{
    private static System.Security.Cryptography.X509Certificates.X509Certificate2 SelfSigned()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var req = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=docadesk-test", rsa, System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    [Fact]
    public void Public_ca_ok_when_no_policy_errors_and_no_pin()
    {
        using var cert = SelfSigned();
        var policy = new CertificatePolicy(null);
        // Without pin, SslPolicyErrors.None is required — simulate "public CA ok"
        Assert.True(policy.Validate(cert, null, System.Net.Security.SslPolicyErrors.None));
    }

    [Fact]
    public void Public_ca_with_errors_refused_when_unpinned()
    {
        using var cert = SelfSigned();
        var policy = new CertificatePolicy(null);
        Assert.False(policy.Validate(cert, null, System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public void Pinned_match_accepts_even_with_chain_errors()
    {
        using var cert = SelfSigned();
        var fp = CertificatePolicy.FingerprintSha256(cert);
        var policy = new CertificatePolicy(fp);
        Assert.True(policy.Validate(cert, null, System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public void Pinned_mismatch_fails_hard_no_ca_fallback()
    {
        using var cert = SelfSigned();
        var policy = new CertificatePolicy(new string('A', 64));
        Assert.False(policy.Validate(cert, null, System.Net.Security.SslPolicyErrors.None));
    }

    [Fact]
    public void Self_signed_unpinned_refused()
    {
        using var cert = SelfSigned();
        var policy = new CertificatePolicy(null);
        Assert.False(policy.Validate(cert, null, System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch
            | System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors));
    }
}

public class CaptureSelectorTests
{
    private sealed class FakeBackend : DocaDesk.Capture.ICaptureBackend
    {
        public bool GraphicsCaptureSupported { get; init; }
        public bool CanCaptureWindow(string windowId) => windowId != "legacy";
        public bool PreferPrintWindowFallback(string windowId) => windowId == "legacy";
    }

    [Fact]
    public void Prefers_graphics_capture_then_printwindow_then_unavailable()
    {
        var ok = new DocaDesk.Capture.CaptureBackendSelector(new FakeBackend { GraphicsCaptureSupported = true });
        Assert.Equal(DocaDesk.Capture.CaptureBackendSelector.Path.GraphicsCapture, ok.Select("chrome"));
        Assert.Equal(DocaDesk.Capture.CaptureBackendSelector.Path.PrintWindow, ok.Select("legacy"));

        var none = new DocaDesk.Capture.CaptureBackendSelector(new FakeBackend { GraphicsCaptureSupported = false });
        Assert.Equal(DocaDesk.Capture.CaptureBackendSelector.Path.Unavailable, none.Select("chrome"));
    }
}
