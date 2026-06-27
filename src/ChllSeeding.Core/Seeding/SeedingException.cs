namespace ChllSeeding.Core.Seeding;

/// <summary>Raised when a seeding launch fails (e.g. the game could not be opened within the
/// retry window). Callers treat this as a recoverable start failure and may spawn the launch
/// watcher to retry.</summary>
public sealed class SeedingException : Exception
{
    public SeedingException(string message) : base(message) { }
    public SeedingException(string message, Exception inner) : base(message, inner) { }
}
