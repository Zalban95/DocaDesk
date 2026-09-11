using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using DocaDesk.Core.Logging;
using DocaDesk.Core.Models;
using DocaDesk.Core.Security;

namespace DocaDesk.Core.Net;

public sealed class DocaClientOptions
{
    public required Uri BaseAddress { get; init; }
    public string? Token { get; set; }
    public string ClientName { get; init; } = "DocaDesk";
    public string ClientVersion { get; init; } = "0.1.0";
    public CertificatePolicy CertificatePolicy { get; init; } = new();
    public IDocaLogger? Logger { get; init; }
}

/// <summary>
/// Protocol HTTP client. One long-lived SocketsHttpHandler; separate HttpClient for stream (infinite timeout).
/// </summary>
public sealed class DocaClient : IAsyncDisposable
{
    private readonly DocaClientOptions _options;
    private readonly SocketsHttpHandler _handler;
    private readonly HttpClient _api;
    private readonly HttpClient _stream;
    private readonly IDocaLogger _log;

    public DocaClient(DocaClientOptions options)
    {
        _options = options;
        _log = options.Logger ?? new RedactingLogger();

        _handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(30),
            SslOptions =
            {
                RemoteCertificateValidationCallback = ValidateCertificate,
            },
        };

        _api = new HttpClient(_handler, disposeHandler: false)
        {
            BaseAddress = options.BaseAddress,
            Timeout = TimeSpan.FromSeconds(100),
        };
        _api.DefaultRequestHeaders.TryAddWithoutValidation("X-Doca-Client", $"{options.ClientName}/{options.ClientVersion}");

        _stream = new HttpClient(_handler, disposeHandler: false)
        {
            BaseAddress = options.BaseAddress,
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _stream.DefaultRequestHeaders.TryAddWithoutValidation("X-Doca-Client", $"{options.ClientName}/{options.ClientVersion}");
    }

    public string? Token
    {
        get => _options.Token;
        set => _options.Token = value;
    }

    public Uri BaseAddress => _options.BaseAddress;

    public HttpClient ApiHttp => _api;
    public HttpClient StreamHttp => _stream;

    private bool ValidateCertificate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        var cert2 = certificate as X509Certificate2 ?? (certificate is not null ? new X509Certificate2(certificate) : null);
        var ok = _options.CertificatePolicy.Validate(cert2, chain, errors);
        if (!ok)
            _log.Warn($"TLS validation failed: {errors}; pin={_options.CertificatePolicy.HasPin}");
        return ok;
    }

    private void ApplyAuth(HttpRequestMessage req)
    {
        if (!string.IsNullOrEmpty(_options.Token))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.Token);
    }

    public async Task<PairCompleteResponse> PairCompleteAsync(PairCompleteRequest body, CancellationToken ct = default)
    {
        var json = DocaJson.Serialize(body);
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/devices/pair/complete")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        // No auth on pair complete
        using var res = await _api.SendAsync(req, ct).ConfigureAwait(false);
        var text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
            throw DocaErrorMapper.FromHttp((int)res.StatusCode, text);
        return DocaJson.Deserialize<PairCompleteResponse>(text)
            ?? throw new UnexpectedServerException("Empty pair response");
    }

    public async Task<CapabilitiesDocument> GetCapabilitiesAsync(CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/capabilities");
        ApplyAuth(req);
        using var res = await _api.SendAsync(req, ct).ConfigureAwait(false);
        var text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
            throw DocaErrorMapper.FromHttp((int)res.StatusCode, text);
        return DocaJson.Deserialize<CapabilitiesDocument>(text)
            ?? throw new UnexpectedServerException("Empty capabilities");
    }

    public async Task<(ProfileResponse? Body, string? ETag, bool NotModified)> GetProfileAsync(string? etag = null, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/devices/me/profile");
        ApplyAuth(req);
        if (!string.IsNullOrEmpty(etag))
            req.Headers.TryAddWithoutValidation("If-None-Match", etag);

        using var res = await _api.SendAsync(req, ct).ConfigureAwait(false);
        if (res.StatusCode == HttpStatusCode.NotModified)
            return (null, etag, true);

        var text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
            throw DocaErrorMapper.FromHttp((int)res.StatusCode, text);

        var newEtag = res.Headers.ETag?.Tag;
        var body = DocaJson.Deserialize<ProfileResponse>(text);
        return (body, newEtag, false);
    }

    public async Task<AckResponse> AckAsync(long seq, string? ackUrl = null, CancellationToken ct = default)
    {
        var path = string.IsNullOrEmpty(ackUrl) ? "/api/v1/events/ack" : ackUrl;
        var json = DocaJson.Serialize(new AckRequest { Seq = seq });
        using var req = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        ApplyAuth(req);
        using var res = await _api.SendAsync(req, ct).ConfigureAwait(false);
        var text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
            throw DocaErrorMapper.FromHttp((int)res.StatusCode, text);
        return DocaJson.Deserialize<AckResponse>(text) ?? new AckResponse();
    }

    public async Task<EventsPollResponse> PollEventsAsync(long since, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/events?since={since}");
        ApplyAuth(req);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var res = await _api.SendAsync(req, ct).ConfigureAwait(false);
        var text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
            throw DocaErrorMapper.FromHttp((int)res.StatusCode, text);
        return DocaJson.Deserialize<EventsPollResponse>(text) ?? new EventsPollResponse();
    }

    public async Task<HttpResponseMessage> OpenEventStreamAsync(long since, CancellationToken ct = default)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/events?since={since}");
        ApplyAuth(req);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        // Caller owns disposal of the response (must keep stream open).
        return await _stream.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
    }

    public async Task<PromptDocument?> GetPromptAsync(string id, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/prompts/{Uri.EscapeDataString(id)}");
        ApplyAuth(req);
        using var res = await _api.SendAsync(req, ct).ConfigureAwait(false);
        var text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
            throw DocaErrorMapper.FromHttp((int)res.StatusCode, text);
        return DocaJson.Deserialize<PromptDocument>(text);
    }

    public async Task<HttpStatusCode> SelectAsync(string promptId, SelectionRequest body, CancellationToken ct = default)
    {
        var json = DocaJson.Serialize(body);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/prompts/{Uri.EscapeDataString(promptId)}/select")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        ApplyAuth(req);
        using var res = await _api.SendAsync(req, ct).ConfigureAwait(false);
        var text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
            throw DocaErrorMapper.FromHttp((int)res.StatusCode, text);
        return res.StatusCode;
    }

    public async Task ConfirmAsync(string promptId, ConfirmRequest body, CancellationToken ct = default)
    {
        var json = DocaJson.Serialize(body);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/prompts/{Uri.EscapeDataString(promptId)}/confirm")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        ApplyAuth(req);
        using var res = await _api.SendAsync(req, ct).ConfigureAwait(false);
        var text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
            throw DocaErrorMapper.FromHttp((int)res.StatusCode, text);
    }

    public async Task PostSensorSamplesAsync(
        IReadOnlyList<SensorSample> samples,
        string? requestId = null,
        CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?> { ["samples"] = samples };
        if (!string.IsNullOrEmpty(requestId))
            body["requestId"] = requestId;
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/sensors/samples")
        {
            Content = new StringContent(DocaJson.Serialize(body), Encoding.UTF8, "application/json"),
        };
        ApplyAuth(req);
        using var res = await _api.SendAsync(req, ct).ConfigureAwait(false);
        var text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
            throw DocaErrorMapper.FromHttp((int)res.StatusCode, text);
    }

    public async Task<MediaUploadResponse> UploadMediaAsync(
        Stream content,
        string fileName,
        string contentType,
        object? meta,
        string? uploadUrl = null,
        CancellationToken ct = default)
    {
        var path = string.IsNullOrEmpty(uploadUrl) ? "/api/v1/media" : uploadUrl;
        using var form = new MultipartFormDataContent();
        var streamContent = new StreamContent(content);
        streamContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(streamContent, "file", fileName);
        if (meta is not null)
            form.Add(new StringContent(DocaJson.Serialize(meta), Encoding.UTF8, "application/json"), "meta");

        using var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = form };
        ApplyAuth(req);
        // Do not set Content-Type manually — multipart boundary must be set by HttpClient.
        using var res = await _api.SendAsync(req, ct).ConfigureAwait(false);
        var text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
            throw DocaErrorMapper.FromHttp((int)res.StatusCode, text);
        return DocaJson.Deserialize<MediaUploadResponse>(text)
            ?? throw new UnexpectedServerException("Empty media response");
    }

    public async ValueTask DisposeAsync()
    {
        _api.Dispose();
        _stream.Dispose();
        _handler.Dispose();
        await ValueTask.CompletedTask.ConfigureAwait(false);
    }
}
