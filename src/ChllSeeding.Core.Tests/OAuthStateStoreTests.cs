using ChllSeeding.Core.Activation;

namespace ChllSeeding.Core.Tests;

public class OAuthStateStoreTests
{
    // Mirrors the Rust test_oauth_state_lifecycle (state/auth.rs), but per-instance
    // rather than sharing one global static.

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
