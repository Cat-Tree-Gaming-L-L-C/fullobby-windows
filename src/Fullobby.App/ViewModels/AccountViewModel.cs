using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net;
using Fullobby.App.Services;
using Fullobby.Core;
using Fullobby.Core.Activation;
using Fullobby.Core.Api;
using Fullobby.Core.Config;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;

namespace Fullobby.App.ViewModels;

/// <summary>
/// Shared (singleton) view model behind the account section of Settings and the
/// onboarding wizard. Owns the authentication lifecycle — session restore, OAuth
/// login + deep-link callbacks, provider/Steam linking, display-name + API-key
/// management, account deletion.
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

    // Simple per-action cooldowns (ms since boot of the next allowed call). Throttles
    // rapid account mutations.
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

    // ── Observable state (auth signals) ──────────────────────────

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
    [NotifyPropertyChangedFor(nameof(IsAdminUser))]
    private UserInfo? user;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOnboarding))]
    private bool onboardingComplete;

    /// <summary>Current onboarding step (0 sign-in, 1 network, 2 link, 3 nickname, 4 done).</summary>
    [ObservableProperty]
    private int onboardingStep;

    /// <summary>Re-opens the onboarding overlay at the network step for an already-onboarded user
    /// with zero network memberships (the limited-beta hard gate) — set on session restore and when
    /// a directive arrives with <c>join_a_network</c>. Cleared once the user holds a membership.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOnboarding))]
    private bool networkGateActive;

    /// <summary>True when the user belongs to at least one seeding network (gates onboarding
    /// Continue and the seeding directive).</summary>
    [ObservableProperty]
    private bool hasNetworkMembership;

    /// <summary>The user's network memberships in priority order (top = preferred).</summary>
    public ObservableCollection<NetworkMembershipRow> Networks { get; } = new();

    /// <summary>The provider currently mid-flight in a login/link (disables buttons + spins).</summary>
    [ObservableProperty]
    private string? busyProvider;

    /// <summary>Linked auth providers (Steam / Discord / Guest) for the signed-in user,
    /// pre-formatted for display.</summary>
    public ObservableCollection<LinkedProviderRow> LinkedProviders { get; } = new();

    /// <summary>Keep the seeding backend's Steam id in sync with the signed-in user. A user has
    /// at most one Steam account (like Discord); it lives on the /me record as <c>SteamId</c>.</summary>
    partial void OnUserChanged(UserInfo? value)
    {
        var steamId = value?.SteamId;
        if (steamId is { Length: > 0 })
        {
            _config.SetString("linked_steam_id", steamId);
        }
        else
        {
            _config.Remove("linked_steam_id");
        }
    }

    // ── Derived UI state ────────────────────────────────────────────────────────

    /// <summary>The onboarding overlay shows until the user completes (or skips) first-run, and
    /// re-opens (at the network step) while the limited-beta network gate is armed.</summary>
    public bool ShowOnboarding => !OnboardingComplete || NetworkGateActive;

    public bool HasAccountName => User is not null;

    /// <summary>Whether the signed-in user holds a global Admin grant (server-computed
    /// on <c>/me</c>, provider-agnostic) — shows the embedded Admin tab.</summary>
    public bool IsAdminUser => User?.IsAdmin == true;

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

    /// <summary>True when a Steam account is linked.</summary>
    public bool SteamLinked => User?.SteamId is { Length: > 0 };

    /// <summary>True when a Discord account is linked.</summary>
    public bool DiscordLinked => User?.DiscordId is { Length: > 0 };

    /// <summary>True when the active sign-in provider is Steam (drives the "signed in" badge).</summary>
    public bool IsSteamSignedIn => User?.AuthProvider == AuthProvider.Steam;

    /// <summary>True when the active sign-in provider is Discord (drives the "signed in" badge).</summary>
    public bool IsDiscordSignedIn => User?.AuthProvider == AuthProvider.Discord;

    // ── Session restore ──────────────────────────────

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
            var hadStoredCreds = (token is { Length: > 0 } && refresh is { Length: > 0 })
                || _auth.ApiKey is { Length: > 0 };
            if (token is { Length: > 0 } && refresh is { Length: > 0 })
            {
                // The refresh token is an opaque UUID, not a JWT — its validity cannot be
                // judged locally. (An earlier build ran it through JwtUtil.IsExpired, which
                // reads any dotless string as "expired", so every launch >15 minutes after
                // the last one deleted a valid 14-day session before making a single
                // request.) Always ask the server: an expired JWT just 401s /me and
                // AuthHandler refreshes it in place; a genuinely dead session is cleared by
                // AuthRefresher on a definitive refresh rejection — never preemptively here.
                if (await TryLoadMeAsync(guestSync: false).ConfigureAwait(false) == MeLoadResult.Ok)
                {
                    _log.LogInformation("Auth restored from stored JWT tokens");
                    EnsureOnboardingComplete();
                    await EnforceNetworkGateAsync().ConfigureAwait(false);
                    return;
                }
                // Not Ok: if the refresh was definitively rejected, AuthRefresher already
                // cleared the tokens; otherwise (transient) they stay for a later retry, and
                // hadStoredCreds keeps the onboarding overlay from re-arming below.
            }

            // API-key path (guest or programmatic).
            if (_auth.ApiKey is { Length: > 0 })
            {
                var meResult = await TryLoadMeAsync(guestSync: true).ConfigureAwait(false);
                if (meResult == MeLoadResult.Ok)
                {
                    _log.LogInformation("Auth restored via API key");
                    EnsureOnboardingComplete();
                    await EnforceNetworkGateAsync().ConfigureAwait(false);
                    return;
                }
                if (meResult == MeLoadResult.Rejected)
                {
                    // Server actively rejected the key (guest deleted / DB reset) — clear stale creds
                    // so guest-OK endpoints aren't poisoned, and re-arm onboarding.
                    _log.LogWarning("Stored credentials rejected; resetting auth state");
                    ResetAllAuth(rearmOnboarding: true);
                    return;
                }

                // Transient failure (rate-limit / outage / network): keep the stored guest key and
                // onboarding state. Discarding it here would strand a valid guest into a re-register
                // that the server rate-limits — the recurring "can't Continue as Guest" trap.
                _log.LogInformation("get_me failed transiently during restore; keeping stored credentials");
                return;
            }

            // No stored credentials at all. A completed-onboarding flag with no backing
            // identity isn't real (e.g. left over from an older build's skip button, or the
            // server was wiped while the client kept its config) — re-arm onboarding so an
            // unverified user can't land on the main screen. Gated on hadStoredCreds so a
            // transient failure to load a genuine session doesn't bounce the user to onboarding:
            // a real guest keeps an api_key and a real OAuth user keeps a JWT, so this only
            // fires when the config truly holds neither.
            if (!hadStoredCreds && OnboardingComplete)
            {
                _log.LogInformation("No stored credentials; re-arming onboarding");
                ResetAllAuth(rearmOnboarding: true);
            }
        }
        finally
        {
            RunOnUi(() => AuthLoading = false);
        }
    }

    /// <summary>Outcome of a <c>/me</c> load. Lets callers treat a genuine credential rejection
    /// (clear the session) differently from a transient failure (keep it and retry later) — the
    /// distinction that stops a flaky/rate-limited backend from discarding a valid guest.</summary>
    private enum MeLoadResult { Ok, Rejected, Transient }

    /// <summary>Fetch /me and apply it. <see cref="MeLoadResult.Rejected"/> means the server
    /// actively refused the credentials (401/403); <see cref="MeLoadResult.Transient"/> means the
    /// call failed for some other reason (429 rate-limit, 5xx, timeout, network) and the credentials
    /// should be kept.</summary>
    private async Task<MeLoadResult> TryLoadMeAsync(bool guestSync)
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
            return MeLoadResult.Ok;
        }
        catch (ApiException e) when (e.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            _log.LogInformation(e, "get_me rejected stored credentials");
            return MeLoadResult.Rejected;
        }
        catch (Exception e)
        {
            // Rate-limit / outage / network — do NOT treat as a rejection, or a transient blip would
            // throw away a still-valid guest and force a (rate-limited) re-registration.
            _log.LogInformation(e, "get_me failed transiently");
            return MeLoadResult.Transient;
        }
    }

    // ── Login / guest ────────────────────────

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
    /// Idempotent — if we already hold credentials, just adopt them. If the server requires a
    /// verified Cloudflare Turnstile token (403), instead of failing this opens the system browser
    /// to <c>/register-challenge</c>; registration completes later via
    /// <see cref="HandleRegisterCallbackAsync"/> once the challenge is solved.</summary>
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
            // If we already hold credentials (a guest restored from a prior run), adopt them rather
            // than registering again — the server rate-limits guest registration, so a needless
            // re-register is how "Continue as Guest" gets stuck. Only a genuine rejection drops them;
            // a transient blip keeps them (an outage must not force a re-register).
            if (_auth.IsAuthenticated)
            {
                var meResult = await TryLoadMeAsync(guestSync: true).ConfigureAwait(false);
                if (meResult != MeLoadResult.Rejected)
                {
                    await CompleteOnboardingOrRequireNetworkAsync().ConfigureAwait(false);
                    return;
                }
                _auth.ClearTokens();
                _auth.ClearApiKey();
                RunOnUi(() =>
                {
                    IsLoggedIn = false;
                    IsGuest = false;
                    User = null;
                });
            }

            RegisterResponse resp;
            try
            {
                resp = await _api.RegisterGuestAsync().ConfigureAwait(false);
            }
            catch (ApiException e) when (e.StatusCode == HttpStatusCode.Forbidden)
            {
                BeginRegisterChallenge();
                return;
            }
            ApplyGuestRegistration(resp);
            // A brand-new account has no memberships — the network step is the beta hard gate.
            SetOnboardingStep(1);
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

    /// <summary>Apply a successful <c>RegisterResponse</c> to auth/UI state (shared by the direct
    /// path and the post-challenge retry in <see cref="HandleRegisterCallbackAsync"/>).</summary>
    private void ApplyGuestRegistration(RegisterResponse resp)
    {
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

    /// <summary>Store a fresh CSRF state and open the system browser to the Turnstile challenge
    /// page for guest registration.</summary>
    private void BeginRegisterChallenge()
    {
        var state = OAuthStateStore.GenerateState();
        _oauthState.Set(state);
        _toast.Info("Please complete the verification check in your browser to continue.");
        OpenBrowser(ApiValidation.GetRegisterChallengeUrl(state), "guest verification");
    }

    // ── Deep-link callbacks (invoked from App.HandleActivation, on the UI thread) ─

    /// <summary>Handle <c>fullobby://auth/callback</c>: validate CSRF state, store the JWT,
    /// load the user, and finish onboarding.</summary>
    public async Task HandleAuthCallbackAsync(string? state, string token, string refreshToken)
    {
        if (state is null || !_oauthState.Validate(state))
        {
            _log.LogWarning("OAuth state missing or mismatched — ignoring auth callback");
            _toast.Error("Login could not be verified. Please try again.");
            return;
        }

        RunOnUi(() => AuthLoading = true);
        try
        {
            _auth.SetTokens(token, refreshToken);
            var meResult = await TryLoadMeAsync(guestSync: true).ConfigureAwait(false);
            if (meResult == MeLoadResult.Transient)
            {
                // The tokens are seconds old and almost certainly fine — the /me load hit a
                // blip (rate limit / outage). One short retry before deciding anything;
                // discarding freshly minted credentials here forced a pointless re-login.
                await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                meResult = await TryLoadMeAsync(guestSync: true).ConfigureAwait(false);
            }
            if (meResult == MeLoadResult.Ok)
            {
                _log.LogInformation("Signed in via OAuth callback");
                _toast.Success("Signed in");
                // Mid first-run: continue the wizard (network → link → nickname → done). A re-login
                // from Settings (already onboarded) just signs in — unless the account holds no
                // network membership, in which case the beta gate re-opens the portal.
                if (!OnboardingComplete)
                {
                    SetOnboardingStep(1);
                }
                else
                {
                    await EnforceNetworkGateAsync().ConfigureAwait(false);
                }
            }
            else if (meResult == MeLoadResult.Rejected)
            {
                _auth.ClearTokens();
                _toast.Error("Sign-in failed. Please try again.");
            }
            else
            {
                // Still transient: keep the valid tokens — the session completes on the next
                // request or restart instead of bouncing the user back to the browser.
                _log.LogWarning("get_me still failing after OAuth sign-in; keeping tokens");
                _toast.Error("Signed in, but the service is busy — your account will finish loading shortly.");
            }
        }
        finally
        {
            RunOnUi(() => AuthLoading = false);
        }
    }

    /// <summary>Handle <c>fullobby://auth/register-callback</c>: validate CSRF state, retry
    /// guest registration with the solved Turnstile token, then complete onboarding.</summary>
    public async Task HandleRegisterCallbackAsync(string? state, string token)
    {
        if (state is null || !_oauthState.Validate(state))
        {
            _log.LogWarning("Register-challenge state missing or mismatched — ignoring callback");
            _toast.Error("Verification could not be confirmed. Please try again.");
            return;
        }

        RunOnUi(() => BusyProvider = "guest");
        try
        {
            var resp = await _api.RegisterGuestAsync(turnstileToken: token).ConfigureAwait(false);
            ApplyGuestRegistration(resp);
            // A brand-new account has no memberships — the network step is the beta hard gate.
            SetOnboardingStep(1);
        }
        catch (Exception e)
        {
            _log.LogError(e, "Guest registration failed after Turnstile challenge");
            _toast.Error(ApiValidation.FriendlyError(e.Message));
        }
        finally
        {
            RunOnUi(() => BusyProvider = null);
        }
    }

    /// <summary>Handle <c>fullobby://auth/link-callback</c>: commit the staged link, then
    /// refresh linked providers and the user record (linking can change account fields).</summary>
    /// <remarks>A fresh link arrives STAGED — the API commits nothing at the OAuth callback and
    /// requires <c>POST /api/auth/link-confirm</c> from the initiating user's session (link-CSRF
    /// defense). No extra dialog here: the pending-link marker consumed below proves this client
    /// started the flow moments ago, and the server independently refuses a confirm from any
    /// other user. An already-linked identity arrives committed (<paramref name="stagedCode"/>
    /// null) and just needs the refresh.</remarks>
    public async Task HandleLinkCallbackAsync(string provider, string? stagedCode = null)
    {
        if (!_oauthState.ConsumePendingLink(provider))
        {
            _log.LogWarning("Link callback for {Provider} did not match a pending link — ignoring", provider);
            return;
        }

        if (stagedCode is not null)
        {
            try
            {
                await _api.ConfirmLinkAsync(stagedCode).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _log.LogError(e, "Failed to confirm staged {Provider} link", provider);
                _toast.Error(ApiValidation.FriendlyError(e.Message));
                return;
            }
        }

        await RefreshLinkedDataAsync().ConfigureAwait(false);
        var wasGuest = IsGuest;
        var upgraded = false;
        try
        {
            var me = await _api.GetMeAsync().ConfigureAwait(false);
            // Linking can upgrade a guest to a permanent account — re-derive guest status so the UI
            // (API-key rotation, name button, onboarding) reflects it without a restart, matching the
            // guest→permanent transition on link callback.
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

    // ── Account actions ──────

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
            // Mark the link as pending only once we're about to hand off to the browser, so a forged
            // link-callback the user never initiated is rejected (see HandleLinkCallbackAsync).
            _oauthState.SetPendingLink(provider);
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

    // ── Seeding networks (limited-beta gate + membership management) ────────────

    /// <summary>Refresh the membership list from the API. Returns false on a network/API failure
    /// (state left unchanged) so callers can fail open instead of locking the user out.</summary>
    public async Task<bool> RefreshNetworksAsync()
    {
        try
        {
            var list = await _api.GetMyNetworksAsync().ConfigureAwait(false);
            ApplyMemberships(list);
            return true;
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Failed to fetch network memberships");
            return false;
        }
    }

    /// <summary>Join a network by name + code. Returns null on success, else a user-facing error.
    /// The code is sent and forgotten — never stored client-side.</summary>
    public async Task<string?> JoinNetworkAsync(string name, string code)
    {
        name = name.Trim();
        code = code.Trim();
        if (name.Length == 0 || code.Length == 0)
        {
            return "Enter a network name and join code.";
        }
        if (OnCooldown("join_network", 2))
        {
            return "Please wait a moment before trying again.";
        }
        try
        {
            await _api.JoinNetworkAsync(name, code).ConfigureAwait(false);
            await RefreshNetworksAsync().ConfigureAwait(false);
            _toast.Success("Joined network");
            return null;
        }
        catch (ApiException e) when (e.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return "Too many attempts — please wait a few minutes and try again.";
        }
        catch (ApiException e) when (e.StatusCode == HttpStatusCode.BadRequest)
        {
            // The API returns one uniform error for unknown tag or wrong code (by design).
            return "Invalid network or join code.";
        }
        catch (Exception e)
        {
            _log.LogError(e, "Failed to join network");
            return "Couldn't reach the seeding service. Please try again.";
        }
    }

    /// <summary>Leave a network (already confirmed by the caller).</summary>
    public async Task LeaveNetworkAsync(NetworkMembershipRow row)
    {
        try
        {
            await _api.LeaveNetworkAsync(row.NetworkId).ConfigureAwait(false);
            await RefreshNetworksAsync().ConfigureAwait(false);
            _toast.Success($"Left {row.Label}");
        }
        catch (Exception e)
        {
            _log.LogError(e, "Failed to leave network");
            _toast.Error("Failed to leave the network");
        }
    }

    /// <summary>Move a membership up (-1) or down (+1) in the priority order and push the full
    /// new order to the API (which echoes the authoritative list back).</summary>
    public async Task MoveNetworkAsync(NetworkMembershipRow row, int delta)
    {
        var index = Networks.IndexOf(row);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= Networks.Count)
        {
            return;
        }
        var ordered = Networks.Select(n => n.NetworkId).ToList();
        (ordered[index], ordered[target]) = (ordered[target], ordered[index]);
        try
        {
            var list = await _api.SetNetworkPrioritiesAsync(ordered).ConfigureAwait(false);
            ApplyMemberships(list);
        }
        catch (Exception e)
        {
            _log.LogError(e, "Failed to reorder networks");
            _toast.Error("Failed to reorder networks");
        }
    }

    /// <summary>Re-open the onboarding overlay at the network step (the limited-beta portal) for a
    /// user who has already completed onboarding. Used on session restore with zero memberships and
    /// when a directive arrives flagged <c>join_a_network</c>.</summary>
    public void OpenNetworkGate() => RunOnUi(() =>
    {
        OnboardingStep = 1;
        NetworkGateActive = true;
    });

    /// <summary>Advance past the network step: a re-armed gate just dismisses the overlay; during
    /// first-run, guests finish here (they skip link/nickname) and OAuth users continue to linking.</summary>
    public void ContinueFromNetworkStep()
    {
        if (OnboardingComplete)
        {
            RunOnUi(() => NetworkGateActive = false);
            return;
        }
        if (IsGuest)
        {
            CompleteOnboarding();
        }
        else
        {
            SetOnboardingStep(2);
        }
    }

    /// <summary>Arm the network gate when an authenticated user holds zero memberships. Fails open:
    /// if the membership list can't be fetched, assume membership is fine and continue — a network
    /// blip must never lock a valid user out.</summary>
    private async Task EnforceNetworkGateAsync()
    {
        if (!await RefreshNetworksAsync().ConfigureAwait(false))
        {
            _log.LogInformation("Membership check failed; assuming membership OK (fail open)");
            return;
        }
        if (!HasNetworkMembership)
        {
            _log.LogInformation("No network memberships; opening the join-a-network portal");
            OpenNetworkGate();
        }
    }

    /// <summary>Finish the wizard for an adopted guest: the network gate still applies, so route to
    /// the network step when the account holds no memberships (fail open on fetch errors).</summary>
    private async Task CompleteOnboardingOrRequireNetworkAsync()
    {
        var fetched = await RefreshNetworksAsync().ConfigureAwait(false);
        if (fetched && !HasNetworkMembership)
        {
            SetOnboardingStep(1);
            return;
        }
        CompleteOnboarding();
    }

    private void ApplyMemberships(List<NetworkMembership> list) => RunOnUi(() =>
    {
        Networks.Clear();
        foreach (var m in list)
        {
            Networks.Add(new NetworkMembershipRow(m));
        }
        HasNetworkMembership = Networks.Count > 0;
        // A confirmed membership releases a re-armed gate on its own: the portal
        // exists to make the user join, and they are joined — without this, a gate
        // armed by a stale or blipped check sat over the app until the user found
        // the Done button. Mid-wizard (!OnboardingComplete) the wizard still owns
        // the overlay; only the post-onboarding portal is released here.
        if (HasNetworkMembership && NetworkGateActive && OnboardingComplete)
        {
            _log.LogInformation("Membership confirmed; releasing the join-a-network portal");
            NetworkGateActive = false;
        }
    });

    // ── Onboarding helpers ──────────────────────────────────────────────────────

    public void SetOnboardingStep(int step) => RunOnUi(() => OnboardingStep = step);

    /// <summary>Mark first-run complete (dismisses the overlay) and persist it.</summary>
    public void CompleteOnboarding()
    {
        RunOnUi(() =>
        {
            OnboardingComplete = true;
            NetworkGateActive = false;
        });
        _config.SetString("onboarding_complete", "true");
        // Durable now, not after the 500 ms debounce — losing this flag to a crash
        // re-runs the whole wizard for an onboarded user.
        _config.FlushPendingSaves();
    }

    /// <summary>A working session that restored on startup means the user has already onboarded at
    /// least once — so mark it complete if the flag isn't set. This heals the case where the
    /// completion flag was lost (e.g. a crash before the throttled config write flushed) but the
    /// credentials survived, which would otherwise show a valid returning guest onboarding again.</summary>
    private void EnsureOnboardingComplete()
    {
        if (!OnboardingComplete)
        {
            _log.LogInformation("Session restored without the onboarding flag; marking onboarding complete");
            CompleteOnboarding();
        }
    }

    // ── Internals ───────────────────────────────────────────────────────────────

    private async Task RefreshLinkedDataAsync()
    {
        await RefreshProvidersAsync().ConfigureAwait(false);
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
        _config.Remove("linked_steam_id");
        RunOnUi(() =>
        {
            User = null;
            IsLoggedIn = false;
            IsGuest = false;
            LinkedProviders.Clear();
            RaiseProviderFlags();
            Networks.Clear();
            HasNetworkMembership = false;
            NetworkGateActive = false;
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
        // Removals are throttled; sign-out state must survive an immediate kill.
        _config.FlushPendingSaves();

        // The admin panel signs in as its own WebView2 session, which persists in its profile
        // directory independently of the tokens cleared above — so without this a signed-out user's
        // panel session survives on disk. Best-effort: WebView2 holds the directory open while the
        // Admin tab is live, and the delete simply fails then.
        try
        {
            if (Directory.Exists(Branding.WebViewProfileDir))
            {
                Directory.Delete(Branding.WebViewProfileDir, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(e, "Could not clear the admin panel WebView2 profile on sign-out");
        }
    }

    /// <summary>Open an OAuth/link URL in the system browser. The sign-in and link flows take their
    /// URL from the API response, so it is validated as an absolute http(s) URL before it reaches
    /// <c>UseShellExecute</c> — otherwise a malicious or compromised API could return a
    /// <c>file://</c>/UNC path or a local protocol handler and have the client launch it.</summary>
    private void OpenBrowser(string url, string what)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var target)
            || !(string.Equals(target.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
                 || string.Equals(target.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)))
        {
            _log.LogError("Refused a non-web {What} URL from the API", what);
            _toast.Error("Failed to open browser");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(target.AbsoluteUri) { UseShellExecute = true });
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

/// <summary>A seeding-network membership, formatted for the onboarding and Settings lists.</summary>
public sealed class NetworkMembershipRow
{
    public NetworkMembershipRow(NetworkMembership m)
    {
        Id = m.Id;
        NetworkId = m.NetworkId;
        Label = m.DisplayName is { Length: > 0 } d ? d : m.Name;
        DiscordInviteUrl = m.DiscordInviteUrl;
    }

    /// <summary>Membership id (display only).</summary>
    public long Id { get; }
    /// <summary>Network id — the unit of the leave and priorities-reorder calls.</summary>
    public long NetworkId { get; }
    public string Label { get; }
    /// <summary>The network's lead Discord invite; null while its lead is cleared.</summary>
    public string? DiscordInviteUrl { get; }
}
