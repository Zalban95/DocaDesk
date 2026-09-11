namespace DocaDesk.Core;

/// <summary>App-level outcomes mapped from PROTOCOL.md error codes. Unmapped → Unexpected.</summary>
public abstract class DocaException : Exception
{
    public string? Code { get; }
    public int StatusCode { get; }

    protected DocaException(string message, string? code = null, int statusCode = 0, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
        StatusCode = statusCode;
    }
}

public sealed class InvalidTokenException : DocaException
{
    public InvalidTokenException(string? message = null, string? code = "invalid_token", int statusCode = 401)
        : base(message ?? "Invalid or expired token", code, statusCode) { }
}

public sealed class UnauthenticatedException : DocaException
{
    public UnauthenticatedException(string? message = null, string? code = "unauthenticated", int statusCode = 401)
        : base(message ?? "Not authenticated", code, statusCode) { }
}

public sealed class ScopeRequiredException : DocaException
{
    public IReadOnlyList<string> Required { get; }

    public ScopeRequiredException(IReadOnlyList<string>? required = null, string? message = null, string? code = "scope_required", int statusCode = 403)
        : base(message ?? "Required scope missing", code, statusCode)
    {
        Required = required ?? Array.Empty<string>();
    }
}

public sealed class ForbiddenException : DocaException
{
    public ForbiddenException(string? message = null, string? code = "forbidden", int statusCode = 403)
        : base(message ?? "Forbidden", code, statusCode) { }
}

public sealed class NotFoundException : DocaException
{
    public NotFoundException(string? message = null, string? code = "not_found", int statusCode = 404)
        : base(message ?? "Not found", code, statusCode) { }
}

public sealed class InvalidStateException : DocaException
{
    public InvalidStateException(string? message = null, string? code = "invalid_state", int statusCode = 409)
        : base(message ?? "Invalid resource state", code, statusCode) { }
}

public sealed class StaleSelectionException : DocaException
{
    public StaleSelectionException(string? message = null, string? code = "stale_selection", int statusCode = 409)
        : base(message ?? "Selection is stale", code, statusCode) { }
}

public sealed class PromptClosedException : DocaException
{
    public PromptClosedException(string? message = null, string? code = "prompt_closed", int statusCode = 409)
        : base(message ?? "Prompt is closed", code, statusCode) { }
}

public sealed class SelectionConflictException : DocaException
{
    public SelectionConflictException(string? message = null, string? code = "selection_conflict", int statusCode = 409)
        : base(message ?? "Selection conflict", code, statusCode) { }
}

public sealed class EtagMismatchException : DocaException
{
    public string? CurrentEtag { get; }
    public long? CurrentVersion { get; }

    public EtagMismatchException(string? currentEtag = null, long? currentVersion = null, string? message = null, string? code = "etag_mismatch", int statusCode = 412)
        : base(message ?? "ETag or version mismatch", code, statusCode)
    {
        CurrentEtag = currentEtag;
        CurrentVersion = currentVersion;
    }
}

public sealed class PayloadTooLargeException : DocaException
{
    public PayloadTooLargeException(string? message = null, string? code = "payload_too_large", int statusCode = 413)
        : base(message ?? "Payload too large", code, statusCode) { }
}

public sealed class UnsupportedMediaException : DocaException
{
    public UnsupportedMediaException(string? message = null, string? code = "unsupported_media", int statusCode = 415)
        : base(message ?? "Unsupported media type", code, statusCode) { }
}

public sealed class InvalidPairingException : DocaException
{
    public InvalidPairingException(string? message = null, string? code = "invalid_pairing", int statusCode = 400)
        : base(message ?? "Invalid pairing code", code, statusCode) { }
}

public sealed class InvalidRequestException : DocaException
{
    public InvalidRequestException(string? message = null, string? code = "invalid_request", int statusCode = 400)
        : base(message ?? "Invalid request", code, statusCode) { }
}

public sealed class CommandFailedException : DocaException
{
    public CommandFailedException(string? message = null, string? code = "command_failed", int statusCode = 500)
        : base(message ?? "Command failed", code, statusCode) { }
}

public sealed class RateLimitedException : DocaException
{
    public int? RetryAfterSec { get; }

    public RateLimitedException(int? retryAfterSec = null, string? message = null, string? code = "rate_limited", int statusCode = 429)
        : base(message ?? "Rate limited", code, statusCode)
    {
        RetryAfterSec = retryAfterSec;
    }
}

public sealed class NetworkException : DocaException
{
    public NetworkException(string? message = null, Exception? inner = null)
        : base(message ?? "Network error", code: null, statusCode: 0, inner) { }
}

public sealed class CertificateException : DocaException
{
    public CertificateException(string? message = null, Exception? inner = null)
        : base(message ?? "TLS certificate error", code: null, statusCode: 0, inner) { }
}

/// <summary>Unexpected / unmapped server error — code is logged, never swallowed.</summary>
public sealed class UnexpectedServerException : DocaException
{
    public UnexpectedServerException(string? message = null, string? code = null, int statusCode = 0)
        : base(message ?? "Unexpected server error", code, statusCode) { }
}
