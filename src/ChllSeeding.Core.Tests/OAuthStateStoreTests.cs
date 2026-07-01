using ChllSeeding.Core.Activation;

namespace ChllSeeding.Core.Tests;

public class OAuthStateStoreTests
{
    // State lifecycle is per-instance rather than sharing one global static.

    [Fact]
    public void Validate_WithNoStateSet_ReturnsFalse()
    {
        var store = new OAuthStateStore();
        Assert.False(store.Validate("anything"));
    }

    [Fact]
    public void Validate_WithCorrectState_ReturnsTrue()
    {
        var store = new OAuthStateStore();
        store.Set("state_abc");
        Assert.True(store.Validate("state_abc"));
    }

    [Fact]
    public void Validate_IsSingleUse()
    {
        var store = new OAuthStateStore();
        store.Set("state_abc");
        Assert.True(store.Validate("state_abc"));
        // Consumed — a second validate fails even with the right value.
        Assert.False(store.Validate("state_abc"));
    }

    [Fact]
    public void Validate_WrongValue_ConsumesStoredState()
    {
        var store = new OAuthStateStore();
        store.Set("correct_state");
        Assert.False(store.Validate("wrong_state"));
        // The failed validation still consumed the stored state.
        Assert.False(store.Validate("correct_state"));
    }

    [Fact]
    public void Set_Overwrites_OnlyLatestValidates()
    {
        var store = new OAuthStateStore();
        store.Set("first");
        store.Set("second");
        Assert.False(store.Validate("first"));
    }

    [Fact]
    public void Validate_EmptyStringState_Roundtrips()
    {
        var store = new OAuthStateStore();
        store.Set("");
        Assert.True(store.Validate(""));
        Assert.False(store.Validate(""));
    }

    [Fact]
    public void Validate_LongAndSpecialCharStates()
    {
        var store = new OAuthStateStore();
        var longState = new string('x', 1000);
        store.Set(longState);
        Assert.True(store.Validate(longState));

        const string special = "state+with/special=chars&more?query#frag";
        store.Set(special);
        Assert.True(store.Validate(special));
    }

    // ── Pending-link guard (auth/link-callback CSRF) ─────────────────

    [Fact]
    public void ConsumePendingLink_WithNothingPending_ReturnsFalse()
    {
        var store = new OAuthStateStore();
        Assert.False(store.ConsumePendingLink("steam"));
    }

    [Fact]
    public void ConsumePendingLink_MatchingProvider_ReturnsTrue()
    {
        var store = new OAuthStateStore();
        store.SetPendingLink("discord");
        Assert.True(store.ConsumePendingLink("discord"));
    }

    [Fact]
    public void ConsumePendingLink_IsSingleUse()
    {
        var store = new OAuthStateStore();
        store.SetPendingLink("steam");
        Assert.True(store.ConsumePendingLink("steam"));
        // A replayed callback after the first consume is rejected.
        Assert.False(store.ConsumePendingLink("steam"));
    }

    [Fact]
    public void ConsumePendingLink_WrongProvider_ConsumesAndRejects()
    {
        var store = new OAuthStateStore();
        store.SetPendingLink("steam");
        Assert.False(store.ConsumePendingLink("discord"));
        // The mismatch still consumed the marker.
        Assert.False(store.ConsumePendingLink("steam"));
    }

    [Fact]
    public void PendingLink_And_State_AreIndependent()
    {
        var store = new OAuthStateStore();
        store.Set("login_state");
        store.SetPendingLink("steam");
        // Consuming one must not disturb the other.
        Assert.True(store.ConsumePendingLink("steam"));
        Assert.True(store.Validate("login_state"));
    }

    [Fact]
    public void GenerateState_Is32HexChars_AndUnique()
    {
        var a = OAuthStateStore.GenerateState();
        var b = OAuthStateStore.GenerateState();
        Assert.Equal(32, a.Length);
        Assert.All(a, c => Assert.Contains(c, "0123456789abcdef"));
        Assert.NotEqual(a, b);
    }
}
