using DocaDesk.Core;

namespace DocaDesk.Tests;

public class ErrorMappingTests
{
    [Theory]
    [InlineData(401, "invalid_token", typeof(InvalidTokenException))]
    [InlineData(401, "unauthenticated", typeof(UnauthenticatedException))]
    [InlineData(403, "scope_required", typeof(ScopeRequiredException))]
    [InlineData(403, "forbidden", typeof(ForbiddenException))]
    [InlineData(404, "not_found", typeof(NotFoundException))]
    [InlineData(404, "unknown_command", typeof(NotFoundException))]
    [InlineData(409, "invalid_state", typeof(InvalidStateException))]
    [InlineData(409, "stale_selection", typeof(StaleSelectionException))]
    [InlineData(409, "prompt_closed", typeof(PromptClosedException))]
    [InlineData(409, "selection_conflict", typeof(SelectionConflictException))]
    [InlineData(412, "etag_mismatch", typeof(EtagMismatchException))]
    [InlineData(413, "payload_too_large", typeof(PayloadTooLargeException))]
    [InlineData(413, "image_too_large", typeof(PayloadTooLargeException))]
    [InlineData(415, "unsupported_media", typeof(UnsupportedMediaException))]
    [InlineData(400, "invalid_pairing", typeof(InvalidPairingException))]
    [InlineData(400, "bad_json", typeof(InvalidRequestException))]
    [InlineData(500, "command_failed", typeof(CommandFailedException))]
    [InlineData(500, "internal", typeof(CommandFailedException))]
    [InlineData(429, "rate_limited", typeof(RateLimitedException))]
    public void Known_codes_map_to_typed_exceptions(int status, string code, Type expected)
    {
        var body = "{\"error\":{\"code\":\"" + code + "\",\"message\":\"m\",\"required\":[\"read:*\"],\"currentEtag\":\"\\\"v3\\\"\",\"currentVersion\":3}}";
        var ex = DocaErrorMapper.FromHttp(status, body);
        Assert.IsType(expected, ex);
        Assert.Equal(code, ex.Code);
    }

    [Fact]
    public void Unmapped_code_degrades_to_unexpected_or_status_bucket()
    {
        var ex = DocaErrorMapper.FromHttp(418, """{"error":{"code":"teapot","message":"no"}}""");
        Assert.IsType<InvalidRequestException>(ex); // 4xx bucket
        Assert.Equal("teapot", ex.Code);
    }

    [Fact]
    public void Scope_required_carries_required_list()
    {
        var ex = (ScopeRequiredException)DocaErrorMapper.FromHttp(403,
            """{"error":{"code":"scope_required","required":["media:upload"]}}""");
        Assert.Contains("media:upload", ex.Required);
    }

    [Fact]
    public void Etag_mismatch_carries_current()
    {
        var ex = (EtagMismatchException)DocaErrorMapper.FromHttp(412,
            """{"error":{"code":"etag_mismatch","currentEtag":"\"v9\"","currentVersion":9}}""");
        Assert.Equal("\"v9\"", ex.CurrentEtag);
        Assert.Equal(9, ex.CurrentVersion);
    }
}
