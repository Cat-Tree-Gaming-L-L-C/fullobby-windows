using Fullobby.Core.Games;

namespace Fullobby.Core.Seeding;

/// <summary>What has to happen to games already open before a seed of <see cref="GameSwapPlan.Target"/>
/// can launch.</summary>
public enum GameSwapKind
{
    /// <summary>Nothing is open: launch straight away.</summary>
    None,

    /// <summary>The target game itself is open (started by hand, so not on the seeding server):
    /// close it and relaunch it onto the server.</summary>
    Relaunch,

    /// <summary>Something else is open — our other game, or another Unreal Engine game that would
    /// likely stop the target launching: close it and start the target.</summary>
    Swap,
}

/// <summary>How the player answered a close-before-seeding question.</summary>
public enum GameSwapChoice
{
    /// <summary>Close what's open and seed.</summary>
    Close,

    /// <summary>Seed without closing anything (offered only for other Unreal games, where the check
    /// can be wrong — see <see cref="GameSwapPlan.AllowContinueAnyway"/>).</summary>
    ContinueAnyway,

    /// <summary>Don't seed.</summary>
    Cancel,
}

/// <summary>A decision from <see cref="GameSwap.Plan"/>: what to close (every open game of ours in
/// <see cref="ToClose"/>, other Unreal games in <see cref="Foreign"/>) and the words to put in front
/// of the player.</summary>
public sealed record GameSwapPlan(
    GameSwapKind Kind, IReadOnlyList<GameDefinition> ToClose, GameDefinition Target, IReadOnlyList<ForeignGame> Foreign)
{
    /// <summary>Everything to close, joined for a sentence ("Hell Let Loose and Wardogs").</summary>
    public string ClosingNames =>
        string.Join(" and ", ToClose.Select(g => g.DisplayName).Concat(Foreign.Select(f => f.DisplayName)).Distinct());

    private int ClosingCount => ToClose.Count + Foreign.Count;

    private string IsAre => ClosingCount > 1 ? "are" : "is";

    private string ItThem => ClosingCount > 1 ? "them" : "it";

    /// <summary>Whether another Unreal game (not ours) is in the way.</summary>
    public bool HasForeign => Foreign.Count > 0;

    /// <summary>Whether "Continue anyway" is offered: only when the sole obstacle is another Unreal
    /// game — that detection can be wrong (an editor, a tool on the engine), and the player knows
    /// better. Our own games genuinely have to close for the launch to land on the server.</summary>
    public bool AllowContinueAnyway => HasForeign && ToClose.Count == 0;

    /// <summary>Title for the confirm dialog.</summary>
    public string Title => Kind switch
    {
        GameSwapKind.Swap when HasForeign => $"Close {ClosingNames}?",
        GameSwapKind.Swap => "Swap games?",
        _ => "Game Running",
    };

    /// <summary>The question a manual Seed asks before closing anything.</summary>
    public string ConfirmMessage => Kind switch
    {
        GameSwapKind.Swap when HasForeign =>
            $"{ClosingNames} {IsAre} open. Unreal Engine games like {Target.DisplayName} usually fail to " +
            $"launch while another one is running. Close {ClosingNames} and start seeding {Target.DisplayName}?",
        GameSwapKind.Swap =>
            $"{ClosingNames} {IsAre} open, but {Target.DisplayName} is the game that needs seeding right now. " +
            $"Close {ClosingNames} and start seeding {Target.DisplayName}?",
        GameSwapKind.Relaunch =>
            $"{Target.DisplayName} is currently running. Close the game to continue?",
        _ => "",
    };

    /// <summary>One line for the "Seed now?" prompt: what answering Seed Now will do.</summary>
    public string PromptNotice => Kind switch
    {
        GameSwapKind.Swap when HasForeign =>
            $"{ClosingNames} {IsAre} open — {Target.DisplayName} usually won't launch alongside {ItThem}. " +
            $"Seed Now closes {ItThem}.",
        GameSwapKind.Swap => $"{ClosingNames} {IsAre} open — Seed Now closes {ItThem} and starts {Target.DisplayName}.",
        GameSwapKind.Relaunch => $"{Target.DisplayName} is open — Seed Now restarts it on the seeding server.",
        _ => "",
    };

    /// <summary>Why a manual Seed stopped when the player kept their game.</summary>
    public string DeclinedMessage => Kind switch
    {
        GameSwapKind.Swap when HasForeign =>
            $"{Target.DisplayName} usually won't launch while {ClosingNames} {IsAre} open — close {ItThem} to seed.",
        GameSwapKind.Swap =>
            $"{Target.DisplayName} needs seeding — close {ClosingNames} (or press Seed and choose Yes) to seed it.",
        _ => $"Close {Target.DisplayName} (or press Seed and choose Yes) to start seeding.",
    };

    /// <summary>Desktop notification when an unanswered auto-seed left the player's games alone.</summary>
    public string SkippedNotice => Kind switch
    {
        GameSwapKind.Swap when HasForeign =>
            $"{Target.DisplayName} needs seeding, but {ClosingNames} {IsAre} open and would likely stop it " +
            $"launching, so auto-seed left {ItThem} alone. Close {ItThem} and press Seed.",
        GameSwapKind.Swap =>
            $"{Target.DisplayName} needs seeding, but {ClosingNames} was open, so auto-seed left it alone. " +
            "Press Seed to swap games.",
        _ => $"Auto-seed didn't start because {Target.DisplayName} was already open. " +
             "Press Seed to restart it on the seeding server.",
    };

    /// <summary>The swap nudge's banner line.</summary>
    public string NudgeMessage => HasForeign
        ? $"{Target.DisplayName} needs seeding, but it usually won't launch while {ClosingNames} {IsAre} open. " +
          $"Close {ItThem} and seed?"
        : $"{Target.DisplayName} needs seeding. Swap from {ClosingNames}?";

    /// <summary>The swap nudge's notification body.</summary>
    public string NudgeBody => HasForeign
        ? $"{ClosingNames} {IsAre} open, and {Target.DisplayName} usually won't launch alongside {ItThem}. " +
          $"Close {ClosingNames} and start seeding?"
        : $"You're in {ClosingNames}. Swap closes it and starts seeding {Target.DisplayName}.";

    /// <summary>The swap nudge's accept button.</summary>
    public string NudgeAcceptLabel => HasForeign ? "Close and seed" : "Swap";

    /// <summary>What this plan would close, as comparable keys (our games by id, others by exe).</summary>
    public IReadOnlySet<string> CloseKeys =>
        ToClose.Select(g => $"game:{g.Id}")
            .Concat(Foreign.Select(f => $"exe:{f.ExeName.ToLowerInvariant()}"))
            .ToHashSet();

    /// <summary>Whether the player's earlier go-ahead (<paramref name="consent"/>, the plan they were
    /// shown) covers this fresh plan: everything this would close was named then, and — when
    /// <paramref name="sameTarget"/> — it's still the game they agreed to seed. Something opened
    /// since, or a different target, isn't what they agreed to.</summary>
    public bool IsCoveredBy(GameSwapPlan consent, bool sameTarget) =>
        (!sameTarget || consent.Target.Id == Target.Id) && CloseKeys.IsSubsetOf(consent.CloseKeys);
}

/// <summary>
/// Deciding what to do about games already open when a seed is about to launch. The directive
/// picks the game across everything installed, so the game in priority may not be the one the
/// player has open — a hand-launched HLL when Vietnam needs seeding — and any other Unreal Engine
/// game open will likely stop ours launching at all. Closing someone's game is never done without
/// their say-so: a manual Seed asks, an auto-seed closes only what its answered "Seed now?" prompt
/// named, and the nudge's button is its own go-ahead. Unattended, games are left alone.
/// </summary>
public static class GameSwap
{
    /// <summary>The swap needed to launch <paramref name="target"/> given our games open now
    /// (<paramref name="running"/>) and other Unreal games open (<paramref name="foreign"/>). Pure;
    /// public for unit coverage.</summary>
    public static GameSwapPlan Plan(
        GameDefinition target, IReadOnlyList<GameDefinition> running, IReadOnlyList<ForeignGame>? foreign = null)
    {
        foreign ??= [];
        if (running.Count == 0 && foreign.Count == 0)
        {
            return new GameSwapPlan(GameSwapKind.None, [], target, []);
        }
        var kind = foreign.Count == 0 && running.All(g => g.Id == target.Id)
            ? GameSwapKind.Relaunch
            : GameSwapKind.Swap;
        return new GameSwapPlan(kind, running, target, foreign);
    }
}
