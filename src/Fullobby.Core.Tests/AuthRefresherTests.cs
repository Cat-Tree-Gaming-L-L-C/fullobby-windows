using System.Net;
using System.Text;
using Fullobby.Core.Api;
using Fullobby.Core.Config;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fullobby.Core.Tests;

/// <summary>
/// The refresh failure semantics that ended sessions in the field: only a definitive
/// 401/403 may clear the stored tokens — a transient 429/5xx (or a malformed body)
/// must keep them, or every backend blip becomes a forced re-login and, with no
/// credentials left on disk, a re-armed onboarding wizard on the next launch.
/// </summary>
public sealed class AuthRefresherTests : IDisposable
{
    private readonly string _dir;
    private readonly ConfigService _config;
    private readonly AuthSession _session;

    public AuthRefresherTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "fullobby_test_auth_" + Guid.NewGuid().ToString("N"));
        _config = new ConfigService(NullLogger<ConfigService>.Instance, _dir);
        _session = new AuthSession(_config);
        _session.SetTokens("old.jwt.token", "old-refresh-uuid");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private AuthRefresher Refresher(HttpStatusCode status, string body) =>
        new(new StubFactory(new StubHandler(status, body)),
            _session,
            NullLogger<AuthRefresher>.Instance);

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task TransientStatus_KeepsTokens(HttpStatusCode status)
    {
        var ok = await Refresher(status, """{"error":"busy"}""").RefreshAsync("old.jwt.token");

        Assert.False(ok);
        Assert.Equal("old.jwt.token", _session.Token);
        Assert.Equal("old-refresh-uuid", _session.RefreshToken);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task DefinitiveRejection_ClearsTokens(HttpStatusCode status)
    {
        var ok = await Refresher(status, """{"error":"Unauthorized"}""").RefreshAsync("old.jwt.token");

        Assert.False(ok);
        Assert.Null(_session.Token);
        Assert.Null(_session.RefreshToken);
    }

    [Fact]
    public async Task Success_RotatesBothTokens()
    {
        var ok = await Refresher(
                HttpStatusCode.OK,
                """{"token":"new.jwt.token","refresh_token":"new-refresh-uuid"}""")
            .RefreshAsync("old.jwt.token");

        Assert.True(ok);
        Assert.Equal("new.jwt.token", _session.Token);
        Assert.Equal("new-refresh-uuid", _session.RefreshToken);
    }

    [Fact]
    public async Task Success_WithEmptyRefreshField_KeepsOldRefreshToken()
    {
        var ok = await Refresher(HttpStatusCode.OK, """{"token":"new.jwt.token"}""")
            .RefreshAsync("old.jwt.token");

        Assert.True(ok);
        Assert.Equal("new.jwt.token", _session.Token);
        Assert.Equal("old-refresh-uuid", _session.RefreshToken);
    }

    [Fact]
    public async Task MalformedSuccessBody_KeepsTokens()
    {
        var ok = await Refresher(HttpStatusCode.OK, "not json at all").RefreshAsync("old.jwt.token");

        Assert.False(ok);
        Assert.Equal("old.jwt.token", _session.Token);
        Assert.Equal("old-refresh-uuid", _session.RefreshToken);
    }

    [Fact]
    public async Task AlreadyRefreshedByAnotherCaller_ShortCircuits()
    {
        // Handler would clear tokens (401), but the token no longer matches what the
        // caller saw — someone else already rotated, so nothing must be sent at all.
        var ok = await Refresher(HttpStatusCode.Unauthorized, """{"error":"Unauthorized"}""")
            .RefreshAsync("token-from-before-the-other-refresh");

        Assert.True(ok);
        Assert.Equal("old.jwt.token", _session.Token);
    }

    /// <summary>Rotation must be durable immediately: the two config writes land inside
    /// the 500 ms debounce window, so without an explicit flush the on-disk refresh
    /// token stays the previous (already-consumed) one until shutdown — and a crash
    /// restores a dead token, costing the whole session.</summary>
    [Fact]
    public void SetTokens_PersistsRotatedRefreshTokenImmediately()
    {
        _session.SetTokens("newer.jwt.token", "newer-refresh-uuid");

        var reopened = new ConfigService(NullLogger<ConfigService>.Instance, _dir);
        Assert.Equal("newer-refresh-uuid", reopened.GetString("auth_refresh_token"));
        Assert.Equal("newer.jwt.token", reopened.GetString("auth_token"));
    }
}
