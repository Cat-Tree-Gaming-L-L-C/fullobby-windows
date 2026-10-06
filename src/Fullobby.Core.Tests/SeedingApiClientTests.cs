using System.Net;
using System.Text;
using System.Text.Json;
using Fullobby.Core.Api;

namespace Fullobby.Core.Tests;

/// <summary>
/// start-session has no server default for <c>platform</c>: a request without it 422s, so no
/// session or heartbeat exists and the server can't notice a client stuck on the main menu
/// or signal a restart-and-rejoin.
/// </summary>
public sealed class SeedingApiClientTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? Path { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Path = request.RequestUri?.AbsolutePath;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"session_id":"s1"}""", Encoding.UTF8, "application/json"),
            };
        }
    }

    [Fact]
    public async Task StartSession_SendsSteamPlatform()
    {
        var handler = new CapturingHandler();
        var client = new SeedingApiClient(new HttpClient(handler));

        var res = await client.StartSessionAsync("hllv", 0, steamId: "76561198000000001");

        Assert.EndsWith("/api/seeding/start-session", handler.Path);
        Assert.NotNull(handler.Body);
        using var doc = JsonDocument.Parse(handler.Body);
        Assert.Equal("steam", doc.RootElement.GetProperty("platform").GetString());
        Assert.Equal("s1", res.SessionId);
    }
}
