using System.Collections.ObjectModel;
using System.Diagnostics;
using ChllSeeding.App.Services;
using ChllSeeding.Core.Activation;
using ChllSeeding.Core.Api;
using ChllSeeding.Core.Config;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;

namespace ChllSeeding.App.ViewModels;

/// <summary>
/// Shared (singleton) view model behind the account section of Settings and the
/// onboarding wizard. Owns the authentication lifecycle — session restore, OAuth
/// login + deep-link callbacks, provider/Steam linking, display-name + API-key
/// management, account deletion — mirroring the Rust <c>state::auth</c> signals and
/// the <c>components/settings.rs</c> / <c>onboarding.rs</c> action helpers.
/// </summary>
public sealed partial class AccountViewModel : ObservableObject
{
    private readonly ILogger<AccountViewModel> _log;
    private readonly SeedingApiClient _api;
    private readonly AuthSession _auth;
    private readonly ConfigService _config;
    private readonly OAuthStateStore _oauthState;
    private readonly InAppToastService _toast;
    private readonly DispatcherQueue _dispatcher;

    // Simple per-action cooldowns (ms since boot of the next allowed call). Mirrors the
    // Rust state::cooldown guard that throttles rapid account mutations.
    private readonly Dictionary<string, long> _cooldowns = new(StringComparer.Ordinal);

    public AccountViewModel(
        ILogger<AccountViewModel> log,
        SeedingApiClient api,
        AuthSession auth,
        ConfigService config,
        OAuthStateStore oauthState,
        InAppToastService toast)
    {
        _log = log;
        _api = api;
        _auth = auth;
        _config = config;
        _oauthState = oauthState;
        _toast = toast;

        // First resolved on the UI thread (App.OnLaunched), so this captures the UI queue.
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        onboardingComplete = _config.GetBool("onboarding_complete");
        isGuest = _config.GetBool("guest_mode");
    }

    // ── Observable state (mirrors state::auth signals) ──────────────────────────

    [ObservableProperty]
    private bool authLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOnLeaderboard))]
    private bool isLoggedIn;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NameButtonText))]
    [NotifyPropertyChangedFor(nameof(CanRotateApiKey))]
    private bool isGuest;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    [NotifyPropertyChangedFor(nameof(HasAccountName))]
    [NotifyPropertyChangedFor(nameof(ShowOnLeaderboard))]
    [NotifyPropertyChangedFor(nameof(SteamLinked))]
    [NotifyPropertyChangedFor(nameof(DiscordLinked))]
    [NotifyPropertyChangedFor(nameof(IsSteamSignedIn))]
    [NotifyPropertyChangedFor(nameof(IsDiscordSignedIn))]
    private UserInfo? user;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOnboarding))]
    private bool onboardingComplete;

    /// <summary>Current onboarding step (0 sign-in, 1 link, 2 nickname, 3 done).</summary>
    [ObservableProperty]
    private int onboardingStep;

    /// <summary>The provider currently mid-flight in a login/link (disables buttons + spins).</summary>
    [ObservableProperty]
    private string? busyProvider;

    /// <summary>Linked auth providers (Steam / Discord / Guest) for the signed-in user,
    /// pre-formatted for display.</summary>
    public ObservableCollection<LinkedProviderRow> LinkedProviders { get; } = new();

    /// <summary>Linked Steam IDs (a user may attach several Steam accounts).</summary>
    public ObservableCollection<string> LinkedSteamIds { get; } = new();

    // ── Derived UI state ────────────────────────────────────────────────────────

    /// <summary>The onboarding overlay shows until the user completes (or skips) first-run.</summary>
    public bool ShowOnboarding => !OnboardingComplete;

    public bool HasAccountName => User is not null;

    public string DisplayName =>
        User?.DisplayName is { Length: > 0 } d ? d
        : User?.Username is { Length: > 0 } u ? u
        : "";

    /// <summary>Guests can only randomize their anonymous name; OAuth users pick one.</summary>
    public string NameButtonText => IsGuest ? "Randomize Name" : "Change Name";

    /// <summary>API-key rotation is a guest-only affordance.</summary>
    public bool CanRotateApiKey => IsGuest;

    /// <summary>The "Show on Leaderboard" toggle reflects the inverse of the opt-out flag.</summary>
    public bool ShowOnLeaderboard => IsLoggedIn && User is not null && !User.LeaderboardOptOut;

    /// <summary>True when a Steam account is linked (mirrors Rust <c>u.steam_id.is_some()</c>).</summary>
    public bool SteamLinked => User?.SteamId is { Length: > 0 };

    /// <summary>True when a Discord account is linked (mirrors Rust <c>u.discord_id.is_some()</c>).</summary>
    public bool DiscordLinked => User?.DiscordId is { Length: > 0 };

    /// <summary>True when the active sign-in provider is Steam (drives the "signed in" badge).</summary>
    public bool IsSteamSignedIn => User?.AuthProvider == AuthProvider.Steam;

    /// <summary>True when the active sign-in provider is Discord (drives the "signed in" badge).</summary>
    public bool IsDiscordSignedIn => User?.AuthProvider == AuthProvider.Discord;

    // ── Session restore (port of app.rs init_auth) ──────────────────────────────

    /// <summary>
    /// Restore auth from stored credentials on startup: try the JWT (skipping the
    /// network round-trip if both tokens are expired), fall back to the API key, and
    /// load linked providers + Steam IDs on success. Runs off the UI thread; all state
    /// writes marshal back. Non-fatal — a dead backend just leaves the user signed out.
    /// </summary>
    public async Task RestoreSessionAsync()
    {
        RunOnUi(() => AuthLoading = true);
        try
        {
            // JWT path: AuthSession already loaded persisted tokens at construction.
            var token = _auth.Token;
            var refresh = _auth.RefreshToken;
            if (token is { Length: > 0 } && refresh is { Length: > 0 })
            {
                if (JwtUtil.IsExpired(token) && JwtUtil.IsExpired(refresh))
                {
                    _log.LogInformation("Stored JWT tokens expired, clearing");
                    _auth.ClearTokens();
                }
                else if (await TryLoadMeAsync(guestSync: false).ConfigureAwait(false))
                {
                    _log.LogInformation("Auth restored from stored JWT tokens");
                    return;
                }
            }

            // API-key path (guest or programmatic).
            if (_auth.ApiKey is { Length: > 0 })
            {
                if (await TryLoadMeAsync(guestSync: true).ConfigureAwait(false))
                {
                    _log.LogInformation("Auth restored via API key");
                }
                else
                {
                    // Both JWT and API key failed (e.g. server DB reset) — clear stale creds so
                    // guest-OK endpoints aren't poisoned, and re-arm onboarding.
                    _log.LogWarning("Stored credentials rejected; resetting auth state");
                    ResetAllAuth(rearmOnboarding: true);
                }
            }
        }
        finally
        {
            RunOnUi(() => AuthLoading = false);
        }
    }

    /// <summary>Fetch /me and apply it. Returns false on failure (caller decides fallback).</summary>
    private async Task<bool> TryLoadMeAsync(bool guestSync)
    {
        try
        {
            var me = await _api.GetMeAsync().ConfigureAwait(false);
            RunOnUi(() =>
            {
                User = me;
                IsLoggedIn = true;
                if (guestSync)
                {
                    var guest = me.AuthProvider == AuthProvider.Guest;
                    IsGuest = guest;
                    _config.SetString("guest_mode", guest ? "true" : "false");
                }
            });
            await RefreshLinkedDataAsync().ConfigureAwait(false);
            return true;
        }
        catch (Exception e)
        {
            _log.LogInformation(e, "get_me failed during session restore");
            return false;
        }
    }

    // ── Login / guest (port of onboarding.rs StepSignIn) ────────────────────────

    /// <summary>Begin OAuth login: store a fresh CSRF state and open the system browser.</summary>
    [RelayCommand]
    public void Login(string provider)
    {
        var state = OAuthStateStore.GenerateState();
        _oauthState.Set(state);
        var url = ApiValidation.GetOAuthUrl(provider, state);
        if (url is null)
        {
            _log.LogError("Invalid OAuth provider '{Provider}'", provider);
            _toast.Error($"Invalid login provider: {provider}");
            return;
        }
        OpenBrowser(url, $"{provider} login");
    }

    /// <summary>Register a guest account (or reuse the bootstrapper's), then complete onboarding.
    /// Idempotent — if we already hold credentials, just adopt them.</summary>
    [RelayCommand]
    public async Task RegisterGuestAsync()
    {
        if (BusyProvider is not null)
        {
            return;
        }
        RunOnUi(() => BusyProvider = "guest");
        try
        {
            if (!_auth.IsAuthenticated)
            {
                var resp = await _api.RegisterGuestAsync().ConfigureAwait(false);
                _auth.SetApiKey(resp.ApiKey);
                _config.SetString("guest_mode", "true");
                _config.SetString("auth_provider", "guest");
                RunOnUi(() =>
                {
                    IsGuest = true;
                    IsLoggedIn = true;
                    User = new UserInfo
                    {
                        UserId = resp.UserId,
                        Username = resp.Username,
                        AuthProvider = AuthProvider.Guest,
                        DisplayName = resp.DisplayName,
                    };
                });
            }
            else
            {
                // Already have creds (bootstrapper registered silently) — sync from /me.
                await TryLoadMeAsync(guestSync: true).ConfigureAwait(false);
            }
            CompleteOnboarding();
        }
        catch (Exception e)
        {
            _log.LogError(e, "Guest registration failed");
            _toast.Error(ApiValidation.FriendlyError(e.Message));
        }
        finally
        {
            RunOnUi(() => BusyProvider = null);
        }
    }

    // ── Deep-link callbacks (invoked from App.HandleActivation, on the UI thread) ─

    /// <summary>Handle <c>chllseeding://auth/callback</c>: validate CSRF state, store the JWT,
    /// load the user, and finish onboarding. Port of the Rust auth-callback handler.</summary>
    public async Task HandleAuthCallbackAsync(string? state, string token, string refreshToken)
    {
        if (state is not null && !_oauthState.Validate(state))
        {
            _log.LogWarning("OAuth state mismatch — ignoring auth callback");
            _toast.Error("Login could not be verified. Please try again.");
            return;
        }

        RunOnUi(() => AuthLoading = true);
        try
        {
            _auth.SetTokens(token, refreshToken);
            if (await TryLoadMeAsync(guestSync: true).ConfigureAwait(false))
            {
                _log.LogInformation("Signed in via OAuth callback");
                _toast.Success("Signed in");
                // Mid first-run: continue the wizard (link → nickname → done). A re-login from
                // Settings (already onboarded) just signs in with no overlay.
                if (!OnboardingComplete)
                {
                    SetOnboardingStep(1);
                }
            }
            else
            {
                _auth.ClearTokens();
                _toast.Error("Sign-in failed. Please try again.");
            }
        }
        finally
        {
            RunOnUi(() => AuthLoading = false);
        }
    }

    /// <summary>Handle <c>chllseeding://auth/link-callback</c>: refresh linked providers + Steam IDs
    /// and the user record (linking can change account fields).</summary>
    public async Task HandleLinkCallbackAsync(string provider)
    {
        await RefreshLinkedDataAsync().ConfigureAwait(false);
        var wasGuest = IsGuest;
        var upgraded = false;
        try
        {
            var me = await _api.GetMeAsync().ConfigureAwait(false);
            // Linking can upgrade a guest to a permanent account — re-derive guest status so the UI
            // (API-key rotation, name button, onboarding) reflects it without a restart. Port of the
            // guest→permanent transition in state/events.rs LinkCallback.
            var guest = me.AuthProvider == AuthProvider.Guest;
            upgraded = wasGuest && !guest;
            RunOnUi(() =>
            {
                User = me;
                IsGuest = guest;
                _config.SetString("guest_mode", guest ? "true" : "false");
            });
        }
        catch (Exception e)
        {
            _log.LogInformation(e, "get_me after link callback failed");
        }
        _toast.Success(upgraded
            ? $"{Capitalize(provider)} linked — your account is now permanent!"
            : $"{Capitalize(provider)} linked");
    }

    // ── Account actions (port of components/settings.rs AuthAccountSection) ──────

    /// <summary>Guest: randomize an anonymous name. Used by the name button + onboarding.</summary>
    [RelayCommand]
    public async Task RandomizeNameAsync()
    {
        if (OnCooldown("randomize_name", 3))
        {
            return;
        }
        try
        {
            var updated = await _api.RandomizeDisplayNameAsync().ConfigureAwait(false);
            RunOnUi(() => User = updated);
            _toast.Success("Name randomized");
        }
        catch (Exception e)
        {
            _log.LogError(e, "Failed to randomize display name");
            _toast.Error(ApiValidation.FriendlyError(e.Message));
        }
    }

    /// <summary>OAuth user: set a new display name (validated client-side first).</summary>
    public async Task UpdateDisplayNameAsync(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
        {
            return;
        }
        var validationError = ApiValidation.ValidateDisplayName(trimmed);
        if (validationError is not null)
        {
            _toast.Error(validationError);
            return;
        }
        if (OnCooldown("update_name", 3))
        {
            return;
        }
        try
        {
            var updated = await _api.UpdateDisplayNameAsync(trimmed).ConfigureAwait(false);
            RunOnUi(() => User = updated);
            _toast.Success("Display name updated");
        }
        catch (Exception e)
        {
            _log.LogError(e, "Failed to update display name");
            _toast.Error(ApiValidation.FriendlyError(e.Message));
        }
    }

    /// <summary>Toggle public leaderboard visibility (the toggle is the inverse of opt-out).</summary>
    public async Task SetShowOnLeaderboardAsync(bool show)
    {
        var optOut = !show;
        try
        {
            var updated = await _api.UpdateLeaderboardOptOutAsync(optOut).ConfigureAwait(false);
            RunOnUi(() => User = updated);
            _toast.Success(optOut ? "Removed from leaderboard" : "Now visible on leaderboard");
        }
        catch (Exception e)
        {
            _log.LogError(e, "Failed to update leaderboard preference");
            _toast.Error(ApiValidation.FriendlyError(e.Message));
        }
    }

    /// <summary>Guest only: rotate the API key (the new key is persisted).</summary>
    [RelayCommand]
    public async Task RotateApiKeyAsync()
    {
        if (OnCooldown("rotate_api_key", 5))
        {
            return;
        }
        try
        {
            var newKey = await _api.RotateApiKeyAsync().ConfigureAwait(false);
            _auth.SetApiKey(newKey);
            _toast.Success("API key rotated");
        }
        catch (Exception e)
        {
            _log.LogError(e, "Failed to rotate API key");
            _toast.Error("Failed to rotate API key");
        }
    }

    [RelayCommand]
    public void SignOut()
    {
        ResetAllAuth(rearmOnboarding: false);
        _log.LogInformation("Logged out");
    }

    /// <summary>Permanently delete the account, then clear all local auth state and re-arm onboarding.</summary>
    [RelayCommand]
    public async Task DeleteAccountAsync()
    {
        if (OnCooldown("delete_account", 10))
        {
            return;
        }
        try
        {
            await _api.DeleteAccountAsync().ConfigureAwait(false);
            ResetAllAuth(rearmOnboarding: true);
            _toast.Success("Account deleted");
            _log.LogInformation("Account deleted");
        }
        catch (Exception e)
        {
            _log.LogError(e, "Failed to delete account");
            _toast.Error("Failed to delete account");
        }
    }

    /// <summary>Begin linking an additional provider: fetch the signed redirect URL + open the browser.</summary>
    [RelayCommand]
    public async Task LinkProviderAsync(string provider)
    {
        if (BusyProvider is not null)
        {
            return;
        }
        RunOnUi(() => BusyProvider = provider);
        try
        {
            var resp = await _api.GetLinkRedirectUrlAsync(provider).ConfigureAwait(false);
            OpenBrowser(resp.RedirectUrl, $"{provider} linking");
        }
        catch (Exception e)
        {
            _log.LogError(e, "Failed to start {Provider} linking", provider);
            _toast.Error($"Failed to start {Capitalize(provider)} linking");
        }
        finally
        {
            RunOnUi(() => BusyProvider = null);
        }
    }

    [RelayCommand]
    public async Task UnlinkProviderAsync(string provider)
    {
        if (OnCooldown("unlink_provider", 3))
        {
            return;
        }
        try
        {
            await _api.UnlinkProviderAsync(provider).ConfigureAwait(false);
            await RefreshLinkedDataAsync().ConfigureAwait(false);
            try
            {
                var me = await _api.GetMeAsync().ConfigureAwait(false);
                RunOnUi(() => User = me);
            }
            catch (Exception e) { _log.LogInformation(e, "get_me after unlink failed"); }
            _toast.Success("Provider unlinked");
        }
        catch (Exception e)
        {
            _log.LogError(e, "Failed to unlink provider");
            _toast.Error("Failed to unlink provider");
        }
    }

    [RelayCommand]
    public async Task RemoveSteamIdAsync(string steamId)
    {
        if (OnCooldown("remove_steam", 3))
        {
            return;
        }
        try
        {
            await _api.RemoveSteamIdAsync(steamId).ConfigureAwait(false);
            await RefreshSteamIdsAsync().ConfigureAwait(false);
            _toast.Success("Steam ID removed");
        }
        catch (Exception e)
        {
            _log.LogError(e, "Failed to remove Steam ID");
            _toast.Error("Failed to remove Steam ID");
        }
    }

    // ── Onboarding helpers ──────────────────────────────────────────────────────

    public void SetOnboardingStep(int step) => RunOnUi(() => OnboardingStep = step);

    /// <summary>Mark first-run complete (dismisses the overlay) and persist it.</summary>
    public void CompleteOnboarding()
    {
        RunOnUi(() => OnboardingComplete = true);
        _config.SetString("onboarding_complete", "true");
    }

    // ── Internals ───────────────────────────────────────────────────────────────

    private async Task RefreshLinkedDataAsync()
    {
        await RefreshSteamIdsAsync().ConfigureAwait(false);
        await RefreshProvidersAsync().ConfigureAwait(false);
    }

    private async Task RefreshSteamIdsAsync()
    {
        try
        {
            var entries = await _api.GetSteamIdsAsync().ConfigureAwait(false);
            var ids = entries.Select(e => e.SteamId).ToList();
            // Persist for the seeding backend's start-session analytics (the engine/VM has no account
            // context), mirroring Rust's stored "linked_steam_ids" session key.
            _config.Set("linked_steam_ids", ids);
            RunOnUi(() =>
            {
                LinkedSteamIds.Clear();
                foreach (var id in ids)
                {
                    LinkedSteamIds.Add(id);
                }
            });
        }
        catch (Exception e)
        {
            _log.LogInformation(e, "Failed to refresh Steam IDs");
        }
    }

    private async Task RefreshProvidersAsync()
    {
        try
        {
            var providers = await _api.GetLinkedProvidersAsync().ConfigureAwait(false);
            var canUnlink = providers.Count > 1;
            RunOnUi(() =>
            {
                LinkedProviders.Clear();
                foreach (var p in providers)
                {
                    LinkedProviders.Add(new LinkedProviderRow(p, canUnlink));
                }
                RaiseProviderFlags();
            });
        }
        catch (Exception e)
        {
            _log.LogInformation(e, "Failed to refresh linked providers");
        }
    }

    private void RaiseProviderFlags()
    {
        OnPropertyChanged(nameof(HasLinkedProviders));
        OnPropertyChanged(nameof(HasDiscordProvider));
        OnPropertyChanged(nameof(HasEpicProvider));
        OnPropertyChanged(nameof(HasXboxProvider));
    }

    /// <summary>True when at least one linked provider is shown (gates the section header).</summary>
    public bool HasLinkedProviders => LinkedProviders.Count > 0;

    /// <summary>True when Discord is already linked (hides the "Link Discord" button).</summary>
    public bool HasDiscordProvider => LinkedProviders.Any(p => p.Provider == "discord");

    /// <summary>True when Epic Games is already linked (hides the "Link Epic" button).</summary>
    public bool HasEpicProvider => LinkedProviders.Any(p => p.Provider == "epic");

    /// <summary>True when Xbox is already linked (hides the "Link Xbox" button).</summary>
    public bool HasXboxProvider => LinkedProviders.Any(p => p.Provider == "xbox");

    /// <summary>Clear every scrap of auth state (memory + persisted config) and reset signals.</summary>
    private void ResetAllAuth(bool rearmOnboarding)
    {
        _auth.ClearTokens();
        _auth.ClearApiKey();
        _config.Remove("linked_steam_ids");
        RunOnUi(() =>
        {
            User = null;
            IsLoggedIn = false;
            IsGuest = false;
            LinkedProviders.Clear();
            LinkedSteamIds.Clear();
            RaiseProviderFlags();
            if (rearmOnboarding)
            {
                OnboardingComplete = false;
                OnboardingStep = 0;
            }
        });
        if (rearmOnboarding)
        {
            _config.Remove("onboarding_complete");
        }
        _config.Remove("guest_mode");
    }

    private void OpenBrowser(string url, string what)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            _log.LogError(e, "Failed to open browser for {What}", what);
            _toast.Error("Failed to open browser");
        }
    }

    /// <summary>Returns true if the action is still cooling down; otherwise arms the cooldown.</summary>
    private bool OnCooldown(string key, int seconds)
    {
        var now = Environment.TickCount64;
        lock (_cooldowns)
        {
            if (_cooldowns.TryGetValue(key, out var until) && now < until)
            {
                return true;
            }
            _cooldowns[key] = now + seconds * 1000L;
            return false;
        }
    }

    private static string Capitalize(string s) =>
        s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private void RunOnUi(Action action)
    {
        if (_dispatcher.HasThreadAccess)
        {
            action();
        }
        else
        {
            _dispatcher.TryEnqueue(() => action());
        }
    }
}

/// <summary>A linked provider, formatted for the Settings list (label + per-row unlink flag).</summary>
public sealed class LinkedProviderRow
{
    public LinkedProviderRow(LinkedProvider p, bool canUnlink)
    {
        Provider = p.Provider.ToWireString();
        var name = p.Provider switch
        {
            AuthProvider.Steam => "Steam",
            AuthProvider.Discord => "Discord",
            AuthProvider.Epic => "Epic Games",
            AuthProvider.Xbox => "Xbox",
            AuthProvider.Guest => "Guest",
            _ => "Steam",
        };
        Label = p.DisplayName is { Length: > 0 } d ? $"{name} ({d})" : name;
        CanUnlink = canUnlink;
    }

    /// <summary>Lowercase wire name ("steam"/"discord"/"guest"), used as the unlink Tag.</summary>
    public string Provider { get; }
    public string Label { get; }
    public bool CanUnlink { get; }
}
