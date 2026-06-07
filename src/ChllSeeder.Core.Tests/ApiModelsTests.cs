using System.Text.Json;
using ChllSeeder.Core.Api;

namespace ChllSeeder.Core.Tests;

/// <summary>Verifies the STJ snake_case / lowercase-enum mapping and optional-field
/// defaults match the Rust serde wire format. A representative subset of types.rs.</summary>
public class ApiModelsTests
{
    private static T Roundtrip<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, ApiJson.Options), ApiJson.Options)!;

    private static T Parse<T>(string json) => JsonSerializer.Deserialize<T>(json, ApiJson.Options)!;

    [Fact]
    public void ServerInfo_SnakeCaseMapping()
    {
        var json = """{"ip":"1.2.3.4","bm_id":100,"short_name":"PF","name":"Comp PF","seeding_threshold":50,"game":"hll"}""";
        var s = Parse<ServerInfo>(json);
        Assert.Equal("1.2.3.4", s.Ip);
        Assert.Equal(100, s.BmId);
        Assert.Equal("PF", s.ShortName);
        Assert.Equal(50, s.SeedingThreshold);
    }

    [Fact]
    public void ServerInfo_GameDefaultsEmpty()
    {
        var s = Parse<ServerInfo>("""{"ip":"1.2.3.4","bm_id":1,"short_name":"S","name":"S","seeding_threshold":50}""");
        Assert.Equal("", s.Game);
    }

    [Fact]
    public void AuthProvider_SerializesLowercase()
    {
        Assert.Equal("\"steam\"", JsonSerializer.Serialize(AuthProvider.Steam, ApiJson.Options));
        Assert.Equal("\"discord\"", JsonSerializer.Serialize(AuthProvider.Discord, ApiJson.Options));
        Assert.Equal("\"guest\"", JsonSerializer.Serialize(AuthProvider.Guest, ApiJson.Options));
    }

    [Fact]
    public void AuthProvider_Default_IsSteam() => Assert.Equal(AuthProvider.Steam, default(AuthProvider));

    [Fact]
    public void AuthProvider_WireString()
    {
        Assert.Equal("steam", AuthProvider.Steam.ToWireString());
        Assert.Equal("discord", AuthProvider.Discord.ToWireString());
        Assert.Equal("guest", AuthProvider.Guest.ToWireString());
    }

    [Fact]
    public void UserInfo_MinimalDeserialize_Defaults()
    {
        var u = Parse<UserInfo>("""{"user_id":"99","username":"minimal"}""");
        Assert.Equal("99", u.UserId);
        Assert.Equal("minimal", u.Username);
        Assert.Equal(AuthProvider.Steam, u.AuthProvider);
        Assert.Null(u.DisplayName);
        Assert.Null(u.SteamId);
    }

    [Fact]
    public void HeartbeatResponse_ValidatedDefaultsFalse()
    {
        var h = Parse<HeartbeatResponse>(
            """{"session_id":"s","status":"active","heartbeat_count":0,"session_duration_secs":0}""");
        Assert.False(h.Validated);
    }

    [Fact]
    public void SeedingStatusResponse_HllvDefaultsNull()
    {
        var r = Parse<SeedingStatusResponse>("""{"hll":{"na":null,"eu":null},"updated_at":0}""");
        Assert.Null(r.Hllv);
        Assert.Null(r.Hll.Na);
    }

    [Fact]
    public void NextServerResponse_SessionIdDefaultsNull()
    {
        var json = """{"game":"hll","region":"na","index":0,"server":{"ip":"1.2.3.4","bm_id":1,"short_name":"S","name":"S","seeding_threshold":50},"all_exhausted":true}""";
        var r = Parse<NextServerResponse>(json);
        Assert.Null(r.SessionId);
        Assert.True(r.AllExhausted);
    }

    [Fact]
    public void RegisterResponse_SnakeCaseMapping()
    {
        var json = """{"user_id":"42","api_key":"key_abc123","username":"guest_12345","display_name":"Guest Player"}""";
        var r = Parse<RegisterResponse>(json);
        Assert.Equal("42", r.UserId);
        Assert.Equal("key_abc123", r.ApiKey);
        Assert.Equal("Guest Player", r.DisplayName);
    }

    [Fact]
    public void ServersResponse_Roundtrip()
    {
        var resp = Roundtrip(new ServersResponse
        {
            Hll = new RegionServers { Na = [], Eu = [] },
            Hllv = null,
            CachedAt = 1700000000,
        });
        Assert.Null(resp.Hllv);
        Assert.Equal(1700000000, resp.CachedAt);
    }

    [Fact]
    public void LinkedProvider_NoDisplayName()
    {
        var lp = Parse<LinkedProvider>("""{"provider":"discord","provider_id":"123456789","linked_at":0}""");
        Assert.Equal(AuthProvider.Discord, lp.Provider);
        Assert.Null(lp.DisplayName);
    }
}
