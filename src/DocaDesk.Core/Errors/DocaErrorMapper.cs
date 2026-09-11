using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocaDesk.Core;

public sealed class ErrorEnvelope
{
    [JsonPropertyName("error")]
    public ErrorBody? Error { get; set; }
}

public sealed class ErrorBody
{
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("required")]
    public List<string>? Required { get; set; }

    [JsonPropertyName("currentEtag")]
    public string? CurrentEtag { get; set; }

    [JsonPropertyName("currentVersion")]
    public long? CurrentVersion { get; set; }
}

/// <summary>Maps PROTOCOL.md error codes + HTTP status to <see cref="DocaException"/> subclasses.</summary>
public static class DocaErrorMapper
{
    public static DocaException FromHttp(int statusCode, string? body, IReadOnlyDictionary<string, string>? headers = null)
    {
        ErrorBody? err = null;
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                err = DocaJson.Deserialize<ErrorEnvelope>(body!)?.Error;
            }
            catch (JsonException)
            {
                // fall through with raw body
            }
        }

        var code = err?.Code;
        var message = err?.Message ?? (string.IsNullOrWhiteSpace(body) ? null : body);

        return Map(statusCode, code, message, err, headers);
    }

    public static DocaException Map(
        int statusCode,
        string? code,
        string? message = null,
        ErrorBody? err = null,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        // Prefer explicit protocol codes when present.
        switch (code)
        {
            case "invalid_token":
                return new InvalidTokenException(message, code, statusCode is 0 ? 401 : statusCode);
            case "unauthenticated":
                return new UnauthenticatedException(message, code, statusCode is 0 ? 401 : statusCode);
            case "scope_required":
                return new ScopeRequiredException(err?.Required, message, code, statusCode is 0 ? 403 : statusCode);
            case "forbidden":
            case "choice_not_available":
            case "sensors_rejected":
                return new ForbiddenException(message, code, statusCode is 0 ? 403 : statusCode);
            case "not_found":
            case "unknown_command":
            case "unknown_request":
            case "no_recipient":
                return new NotFoundException(message, code, statusCode is 0 ? 404 : statusCode);
            case "invalid_state":
                return new InvalidStateException(message, code, statusCode is 0 ? 409 : statusCode);
            case "stale_selection":
                return new StaleSelectionException(message, code, statusCode is 0 ? 409 : statusCode);
            case "prompt_closed":
                return new PromptClosedException(message, code, statusCode is 0 ? 409 : statusCode);
            case "selection_conflict":
                return new SelectionConflictException(message, code, statusCode is 0 ? 409 : statusCode);
            case "etag_mismatch":
                return new EtagMismatchException(err?.CurrentEtag, err?.CurrentVersion, message, code, statusCode is 0 ? 412 : statusCode);
            case "payload_too_large":
            case "vars_too_large":
            case "image_too_large":
            case "ext_too_large":
                return new PayloadTooLargeException(message, code, statusCode is 0 ? 413 : statusCode);
            case "unsupported_media":
                return new UnsupportedMediaException(message, code, statusCode is 0 ? 415 : statusCode);
            case "invalid_pairing":
                return new InvalidPairingException(message, code, statusCode is 0 ? 400 : statusCode);
            case "bad_json":
            case "invalid_request":
            case "invalid_params":
            case "invalid_prompt":
            case "invalid_choice":
            case "invalid_outcome":
            case "invalid_selection":
            case "invalid_confirmation":
            case "invalid_profile":
            case "invalid_artifact":
            case "invalid_alert":
            case "invalid_device":
                return new InvalidRequestException(message, code, statusCode is 0 ? 400 : statusCode);
            case "command_failed":
            case "internal":
                return new CommandFailedException(message, code, statusCode is 0 ? 500 : statusCode);
            case "rate_limited":
            {
                int? retry = null;
                if (headers is not null && headers.TryGetValue("Retry-After", out var ra) && int.TryParse(ra, out var sec))
                    retry = sec;
                return new RateLimitedException(retry, message, code, statusCode is 0 ? 429 : statusCode);
            }
            case "sensors_unavailable":
                return new InvalidStateException(message, code, statusCode is 0 ? 409 : statusCode);
        }

        return statusCode switch
        {
            401 => new InvalidTokenException(message, code ?? "invalid_token", statusCode),
            403 => new ForbiddenException(message, code ?? "forbidden", statusCode),
            404 => new NotFoundException(message, code ?? "not_found", statusCode),
            409 => new InvalidStateException(message, code ?? "invalid_state", statusCode),
            412 => new EtagMismatchException(err?.CurrentEtag, err?.CurrentVersion, message, code ?? "etag_mismatch", statusCode),
            413 => new PayloadTooLargeException(message, code ?? "payload_too_large", statusCode),
            415 => new UnsupportedMediaException(message, code ?? "unsupported_media", statusCode),
            429 => new RateLimitedException(null, message, code ?? "rate_limited", statusCode),
            >= 400 and < 500 => new InvalidRequestException(message, code ?? "invalid_request", statusCode),
            >= 500 => new CommandFailedException(message, code ?? "internal", statusCode),
            _ => new UnexpectedServerException(message, code, statusCode),
        };
    }
}
