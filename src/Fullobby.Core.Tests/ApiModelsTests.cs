using System.Text.Json;
using Fullobby.Core.Api;

namespace Fullobby.Core.Tests;

/// <summary>Verifies the STJ snake_case / lowercase-enum mapping and optional-field
/// defaults match the server's JSON wire format. A representative subset of the API models.</summary>
public class ApiModelsTests
{
    private static T Roundtrip<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, ApiJson.Options), ApiJson.Options)!;

    private static T Parse<T>(string json) => JsonSerializer.Deserialize<T>(json, ApiJson.Options)!;

    [Fact]
    public void ServerInfo_SnakeCaseMapping()
    {
        var json = """{"ip":"1.2.3.4","short_name":"PF","name":"Comp PF","seeding_threshold":50,"game":"hll"}""";
        var s = Parse<ServerInfo>(json);
        Assert.Equal("1.2.3.4", s.Ip);
        Assert.Equal("PF", s.ShortName);
        Assert.Equal(50, s.SeedingThreshold);
    }

    [Fact]
    public void ServerInfo_GameDefaultsEmpty()
    {
        var s = Parse<ServerInfo>("""{"ip":"1.2.3.4","short_name":"S","name":"S","seeding_threshold":50}""");
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
        Assert.Equal("epic", AuthProvider.Epic.ToWireString());
        Assert.Equal("xbox", AuthProvider.Xbox.ToWireString());
        Assert.Equal("guest", AuthProvider.Guest.ToWireString());
    }

    [Theory]
    [InlineData("\"epic\"", AuthProvider.Epic)]
    [InlineData("\"xbox\"", AuthProvider.Xbox)]
    [InlineData("\"discord\"", AuthProvider.Discord)]
    public void AuthProvider_DeserializesNewProviders(string json, AuthProvider expected)
    {
        var u = Parse<UserInfo>($$"""{"user_id":"1","username":"u","auth_provider":{{json}}}""");
        Assert.Equal(expected, u.AuthProvider);
    }

    [Fact]
    public void Platform_WireString()
    {
        // The API has no default platform — the client must declare it, lowercase.
        // Fully qualified: the enclosing Fullobby.Core.Platform namespace shadows the enum here.
        Assert.Equal("steam", Api.Platform.Steam.ToWireString());
        Assert.Equal("epic", Api.Platform.Epic.ToWireString());
        Assert.Equal("xbox", Api.Platform.Xbox.ToWireString());
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
        // A user the server said nothing about reaches nothing.
        Assert.False(u.IsAdmin);
        Assert.False(u.CanOperateServers);
        Assert.False(u.CanManageServers);
        Assert.False(u.CanManageNetworks);
    }

    /// <summary>
    /// The Manage tab is gated on these, so a naming mismatch would not throw — it would
    /// silently deserialize to false and hide the tab from exactly the people each
    /// widening exists to let in. Pin the wire names.
    /// </summary>
    [Fact]
    public void UserInfo_ScopedReachFlags_MapFromSnakeCase()
    {
        var u = Parse<UserInfo>(
            """
            {"user_id":"3","username":"vidaro","is_admin":false,
             "can_operate_servers":true,"can_manage_servers":true,"can_manage_networks":false}
            """);
        Assert.False(u.IsAdmin);
        Assert.True(u.CanOperateServers);
        Assert.True(u.CanManageServers);
        Assert.False(u.CanManageNetworks);
    }

    /// <summary>An operator manages nothing and operates something — the case the Manage
    /// tab used to hide from, and the reason it is gated on the wider flag.</summary>
    [Fact]
    public void UserInfo_OperatorOperatesButDoesNotManage()
    {
        var u = Parse<UserInfo>(
            """{"user_id":"7","username":"op","can_operate_servers":true}""");
        Assert.False(u.IsAdmin);
        Assert.True(u.CanOperateServers);
        Assert.False(u.CanManageServers);
        Assert.False(u.CanManageNetworks);
    }

    /// <summary>An org Admin is not a global Admin — the whole point of the split.</summary>
    [Fact]
    public void UserInfo_OrgAdminIsNotGlobalAdmin()
    {
        var u = Parse<UserInfo>(
            """{"user_id":"3","username":"vidaro","can_manage_servers":true}""");
        Assert.False(u.IsAdmin);
        Assert.True(u.CanManageServers);
    }

    [Fact]
    public void HeartbeatResponse_ValidatedDefaultsFalse()
    {
        var h = Parse<HeartbeatResponse>(
            """{"session_id":"s","status":"active","heartbeat_count":0,"session_duration_secs":0}""");
        Assert.False(h.Validated);
    }

    [Fact]
    public void SeedingStatusResponse_NetworksDefaultEmpty()
    {
        var r = Parse<SeedingStatusResponse>("""{"updated_at":0}""");
        Assert.Empty(r.Networks);
    }

    [Fact]
    public void SeedingStatusResponse_NetworkWithHllCandidate()
    {
        var json = """
            {"networks":[{"network_id":5,"network_tag":"chll","display_name":"Comp HLL","active":true,
            "default_seeding_threshold":75,
            "hll":{"game":"hll","index":2,"server":{"ip":"1.2.3.4","short_name":"S","name":"S","seeding_threshold":50,"game":"hll"},"db_id":99},
            "hll_phase":"cycling"}],"updated_at":7}
            """;
        var r = Parse<SeedingStatusResponse>(json);
        var n = Assert.Single(r.Networks);
        Assert.Equal(5, n.NetworkId);
        Assert.Equal("chll", n.NetworkTag);
        Assert.Equal("Comp HLL", n.DisplayName);
        Assert.True(n.Active);
        Assert.Null(n.NextActiveInSecs);
        Assert.Equal(75, n.DefaultSeedingThreshold);
        Assert.NotNull(n.Hll);
        Assert.Equal(2, n.Hll!.Index);
        Assert.Equal(99, n.Hll.DbId);
        Assert.Null(n.Hllv);
        Assert.Equal(NetworkPhase.Cycling, n.HllPhase);
        Assert.Null(n.HllvPhase);
        Assert.Equal(7, r.UpdatedAt);
    }

    [Theory]
    [InlineData("\"cycling\"", NetworkPhase.Cycling)]
    [InlineData("\"all_seeded\"", NetworkPhase.AllSeeded)]
    public void NetworkPhase_SnakeCaseMapping(string json, NetworkPhase expected)
    {
        var n = Parse<NetworkSeedingStatus>(
            "{\"network_id\":1,\"network_tag\":\"t\",\"active\":true,\"default_seeding_threshold\":75,\"hll_phase\":" + json + "}");
        Assert.Equal(expected, n.HllPhase);
    }

    [Fact]
    public void NetworkMembership_SnakeCaseMapping()
    {
        var m = Parse<NetworkMembership>(
            """{"id":11,"network_id":3,"name":"COMPHLL","display_name":"Comp HLL","discord_invite_url":"https://discord.gg/chll","priority":1,"joined_at":1700000000}""");
        Assert.Equal(11, m.Id);
        Assert.Equal(3, m.NetworkId);
        Assert.Equal("COMPHLL", m.Name);
        Assert.Equal("https://discord.gg/chll", m.DiscordInviteUrl);
        Assert.Equal("Comp HLL", m.DisplayName);
        Assert.Equal(1, m.Priority);
        Assert.Equal(1700000000, m.JoinedAt);
    }

    [Fact]
    public void NetworkMembership_NoDisplayName_IsNull()
    {
        var m = Parse<NetworkMembership>("""{"id":1,"network_id":2,"name":"pf","priority":0,"joined_at":0}""");
        Assert.Null(m.DisplayName);
        Assert.Null(m.DiscordInviteUrl);
    }

    [Fact]
    public void InvitePreview_SnakeCaseMapping()
    {
        var p = Parse<InvitePreview>(
            """{"id":5,"org_id":null,"network_id":3,"org_tag":null,"network_name":"Comp HLL","role":"member","label":"Discord #welcome","max_uses":100,"uses":7,"created_by":2,"created_by_name":"alice","created_at":1700000000,"expires_at":1700600000,"already":false}""");
        Assert.Equal(5, p.Id);
        Assert.Equal(3, p.NetworkId);
        Assert.Equal("Comp HLL", p.NetworkName);
        Assert.Equal("member", p.Role);
        Assert.True(p.IsPlayerLink);
        Assert.Equal("Discord #welcome", p.Label);
        Assert.Equal(100, p.MaxUses);
        Assert.Equal(7, p.Uses);
        Assert.Equal("alice", p.CreatedByName);
        Assert.Equal(1700600000, p.ExpiresAt);
        Assert.False(p.Already);
    }

    [Fact]
    public void InvitePreview_NoExpiryNoLimit_AndStaffRole()
    {
        var p = Parse<InvitePreview>(
            """{"id":1,"org_id":4,"network_id":null,"org_tag":"ABC","network_name":null,"role":"operator","label":null,"max_uses":null,"uses":0,"created_by":null,"created_by_name":null,"created_at":0,"expires_at":null,"already":true}""");
        Assert.Null(p.ExpiresAt);
        Assert.Null(p.MaxUses);
        Assert.Null(p.NetworkName);
        Assert.Equal("ABC", p.OrgTag);
        Assert.False(p.IsPlayerLink);
        Assert.True(p.Already);
    }

    [Fact]
    public void SeedingDirective_ActionLowercaseEnumAndDefaults()
    {
        var json = """{"action":"switch","target":{"game":"hll","index":1,"server":{"ip":"1.2.3.4","short_name":"S","name":"S","seeding_threshold":50,"game":"hll"},"db_id":7},"all_exhausted":false,"stagger_secs":0,"countdown_secs":30,"snooze_min_secs":60,"snooze_max_secs":1800,"poll_again_in_secs":15,"max_session_secs":18000,"config":{}}""";
        var d = Parse<SeedingDirective>(json);
        Assert.Equal(DirectiveAction.Switch, d.Action);
        Assert.False(d.ScheduledPause);   // omitted → default
        Assert.Null(d.NextActiveInSecs);
        Assert.Equal(1, d.Target!.Index);
        Assert.Equal(30, d.CountdownSecs);
        Assert.Equal(90, d.Config.StaggerMaxSecs); // config defaults applied
    }

    [Fact]
    public void SeedingDirective_NetworksStoppedIsAPauseWithNoResumeTime()
    {
        var json = """{"action":"stop","all_exhausted":false,"scheduled_pause":true,"networks_stopped":true,"stagger_secs":0,"countdown_secs":30,"snooze_min_secs":60,"snooze_max_secs":1800,"poll_again_in_secs":15,"max_session_secs":18000,"config":{}}""";
        var d = Parse<SeedingDirective>(json);
        Assert.True(d.ScheduledPause);
        Assert.True(d.NetworksStopped);
        Assert.Null(d.NextActiveInSecs);
        Assert.False(Parse<SeedingDirective>("""{"action":"stop","config":{}}""").NetworksStopped);
    }

    [Theory]
    [InlineData("\"seeded\"", SwitchReason.Seeded)]
    [InlineData("\"unavailable\"", SwitchReason.Unavailable)]
    [InlineData("\"higher_priority_ready\"", SwitchReason.HigherPriorityReady)]
    [InlineData("\"rotation_advanced\"", SwitchReason.RotationAdvanced)]
    [InlineData("\"network_transition\"", SwitchReason.NetworkTransition)]
    public void SwitchReason_SnakeCaseMapping(string json, SwitchReason expected)
    {
        var wrapped = "{\"action\":\"switch\",\"switch_reason\":" + json +
            ",\"all_exhausted\":false,\"stagger_secs\":0,\"countdown_secs\":30,\"snooze_min_secs\":60," +
            "\"snooze_max_secs\":1800,\"poll_again_in_secs\":15,\"max_session_secs\":18000,\"config\":{}}";
        var d = Parse<SeedingDirective>(wrapped);
        Assert.Equal(expected, d.SwitchReason);
    }

    [Fact]
    public void SeedingDirective_SwitchReasonOmitted_IsNull()
    {
        var json = """{"action":"stay","all_exhausted":false,"stagger_secs":0,"countdown_secs":30,"snooze_min_secs":60,"snooze_max_secs":1800,"poll_again_in_secs":60,"max_session_secs":18000,"config":{}}""";
        Assert.Null(Parse<SeedingDirective>(json).SwitchReason);
    }

    [Fact]
    public void SeedingDirective_JoinANetworkOmitted_DefaultsFalse()
    {
        var json = """{"action":"stay","all_exhausted":false,"stagger_secs":0,"countdown_secs":30,"snooze_min_secs":60,"snooze_max_secs":1800,"poll_again_in_secs":60,"max_session_secs":18000,"config":{}}""";
        Assert.False(Parse<SeedingDirective>(json).JoinANetwork);
    }

    [Fact]
    public void SeedingDirective_JoinANetwork_ParsesTrue()
    {
        // The beta hard gate: stop + join_a_network with no target.
        var json = """{"action":"stop","join_a_network":true,"all_exhausted":false,"stagger_secs":0,"countdown_secs":30,"snooze_min_secs":60,"snooze_max_secs":1800,"poll_again_in_secs":60,"max_session_secs":18000,"config":{}}""";
        var d = Parse<SeedingDirective>(json);
        Assert.Equal(DirectiveAction.Stop, d.Action);
        Assert.True(d.JoinANetwork);
        Assert.Null(d.Target);
    }

    [Theory]
    [InlineData("\"done\"", DayStatus.Done)]
    [InlineData("\"current\"", DayStatus.Current)]
    [InlineData("\"pending\"", DayStatus.Pending)]
    [InlineData("\"skipped\"", DayStatus.Skipped)]
    [InlineData("\"not_ready\"", DayStatus.NotReady)]
    [InlineData("\"missed_ready\"", DayStatus.MissedReady)]
    [InlineData("\"deferred\"", DayStatus.Deferred)]
    public void DayStatus_SnakeCaseMapping(string json, DayStatus expected)
    {
        var d = Parse<ServerDayStatus>(
            "{\"db_id\":1,\"name\":\"S\",\"short_name\":\"S\",\"status\":" + json + ",\"threshold\":50}");
        Assert.Equal(expected, d.Status);
    }

    [Fact]
    public void SeedingStatusResponse_NetworkDayArrays()
    {
        var json = """
            {"networks":[{"network_id":1,"network_tag":"chll","active":true,"default_seeding_threshold":75,
            "hll_day":[{"db_id":1,"name":"A","short_name":"A","org_tag":"chll","status":"not_ready","threshold":50,"window_start_ts":1700000000}]}],
            "updated_at":0}
            """;
        var r = Parse<SeedingStatusResponse>(json);
        var n = Assert.Single(r.Networks);
        Assert.Single(n.HllDay);
        Assert.Equal(DayStatus.NotReady, n.HllDay[0].Status);
        Assert.Equal("chll", n.HllDay[0].OrgTag);
        Assert.Equal(1700000000, n.HllDay[0].WindowStartTs);
        Assert.Empty(n.HllvDay); // omitted → default empty
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
            Hll = [],
            Hllv = null,
            CachedAt = 1700000000,
        });
        Assert.Null(resp.Hllv);
        Assert.Empty(resp.Hll);
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
