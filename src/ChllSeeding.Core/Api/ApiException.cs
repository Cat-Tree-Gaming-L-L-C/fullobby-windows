namespace ChllSeeding.Core.Api;

/// <summary>
/// Raised when an API call returns a non-success status. The message follows the
/// Rust wire format ("API error: …" / "Update required: …") so it can be passed
/// straight to <see cref="ApiValidation.FriendlyError"/>.
/// </summary>
public sealed class ApiException(string message) : Exception(message);
