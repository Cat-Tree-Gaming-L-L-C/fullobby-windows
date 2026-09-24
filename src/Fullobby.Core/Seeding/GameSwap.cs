using Fullobby.Core.Games;

namespace Fullobby.Core.Seeding;

/// <summary>What has to happen to games already open before a seed of <see cref="Target"/> can
/// launch.</summary>
public enum GameSwapKind
{
    /// <summary>Nothing is open: launch straight away.</summary>
    None,

    /// <summary>The target game itself is open (started by hand, so not on the seeding server):
    /// close it and relaunch it onto the server.</summary>
    Relaunch,

    /// <summary>A different game is open: close it and start the target instead.</summary>
    Swap,
}

/// <summary>A decision from <see cref="GameSwap.Plan"/>: the games to close (every open one) and
/// the words to put in front of the player.</summary>
public sealed record GameSwapPlan(GameSwapKind Kind, IReadOnlyList<GameDefinition> ToClose, GameDefinition Target)
{
    /// <summary>The open games' names, joined for a sentence ("Hell Let Loose").</summary>
    public string ClosingNames => string.Join(" and ", ToClose.Select(g => g.DisplayName));

    private string IsAre => ToClose.Count > 1 ? "are" : "is";

    /// <summary>Title for the confirm dialog.</summary>
    public string Title => Kind == GameSwapKind.Swap ? "Swap games?" : "Game Running";

    /// <summary>The question a manual Seed asks before closing anything.</summary>
    public string ConfirmMessage => Kind switch
    {
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
        GameSwapKind.Swap => $"{ClosingNames} {IsAre} open — Seed Now closes it and starts {Target.DisplayName}.",
        GameSwapKind.Relaunch => $"{Target.DisplayName} is open — Seed Now restarts it on the seeding server.",
        _ => "",
    };

    /// <summary>Why a manual Seed stopped when the player kept their game.</summary>
    public string DeclinedMessage => Kind == GameSwapKind.Swap
        ? $"{Target.DisplayName} needs seeding — close {ClosingNames} (or press Seed and choose Yes) to seed it."
        : $"Close {Target.DisplayName} (or press Seed and choose Yes) to start seeding.";

    /// <summary>Desktop notification when an unanswered auto-seed left the player's game alone.</summary>
    public string SkippedNotice => Kind == GameSwapKind.Swap
        ? $"{Target.DisplayName} needs seeding, but {ClosingNames} was open, so auto-seed left it alone. " +
          "Press Seed to swap games."
        : $"Auto-seed didn't start because {Target.DisplayName} was already open. Press Seed to restart it on the seeding server.";
}

/// <summary>
/// Deciding what to do about games already open when a seed is about to launch. The directive
/// picks the game across everything installed, so the game in priority may not be the one the
/// player has open — a hand-launched HLL when Vietnam needs seeding. Closing someone's game is
/// never done without their say-so: a manual Seed asks, and an auto-seed swaps only when the
/// "Seed now?" prompt was answered; unattended, it leaves the game alone and says so.
/// </summary>
public static class GameSwap
{
    /// <summary>The swap needed to launch <paramref name="target"/> given the games open now
    /// (<paramref name="running"/>, in any order). Pure; public for unit coverage.</summary>
    public static GameSwapPlan Plan(GameDefinition target, IReadOnlyList<GameDefinition> running)
    {
        if (running.Count == 0)
        {
            return new GameSwapPlan(GameSwapKind.None, [], target);
        }
        var kind = running.All(g => g.Id == target.Id) ? GameSwapKind.Relaunch : GameSwapKind.Swap;
        return new GameSwapPlan(kind, running, target);
    }
}
