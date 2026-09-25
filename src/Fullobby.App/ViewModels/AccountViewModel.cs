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

        onboardingComplete = _config.GetBool(ConfigKeys.OnboardingComplete);
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
    [NotifyPropertyChangedFor(nameof(CanReachManagePanel))]
    private UserInfo? user;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOnboarding))]
    [NotifyPropertyChangedFor(nameof(ShowReauthOnly))]
    [NotifyPropertyChangedFor(nameof(SeedingBlocked))]
    private bool onboardingComplete;

    /// <summary>An invite token handed over by a <c>fullobby://invite</c> deep link, waiting for a
    /// join prompt to prefill with it: the onboarding network step, or the join dialog once the user
    /// is past onboarding (MainWindow). Taken once with <see cref="TakePendingInvite"/>; nothing is
    /// joined until the user confirms, and it is never written to config.</summary>
    [ObservableProperty]
    private string? pendingInvite;

    /// <summary>Current onboarding step (0 sign-in, 1 network, 2 link, 3 nickname, 4 done).</summary>
    [ObservableProperty]
    private int onboardingStep;

    /// <summary>
    /// The limited-beta hard gate: an already-onboarded user is holding zero network memberships,
    /// so nothing can be seeded until they join one. Set on session restore and when a directive
    /// arrives with <c>join_a_network</c>; cleared the moment a membership is confirmed.
    ///
    /// Surfaced as a banner over the running app, NOT as the onboarding overlay. It used to re-open
    /// that full-bleed overlay, which replaced the entire shell — Settings, the leaderboard and the
    /// sign-out button included — for a configured user whose only problem was a missing
    /// membership. Since the gate arms on any transient membership loss (a network rebuilt or
    /// renamed server-side, a blipped fetch), that read as being thrown back into onboarding.
    /// Seeding is what the gate exists to stop, and the server stops it regardless — the client's
    /// job is to say so and offer the fix, not to take the app away.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SeedingBlocked))]
    private bool networkGateActive;

    /// <summary>
    /// The user has completed onboarding but their stored credentials are gone or were rejected,
    /// so they need to sign in again — and nothing more.
    ///
    /// This used to be expressed by clearing <c>onboarding_complete</c>, which conflated "we do not
    /// know who you are" with "you have never set this app up" and walked a configured user back
    /// through the entire five-step wizard. Credentials are lost for ordinary reasons — the API
    /// rejecting a session after a backend reset, the 14-day stale-guest sweep, a DPAPI blob that
    /// will not decrypt on this profile — and none of them mean the setup is gone. Onboarding state
    /// now survives; only the sign-in step comes back.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOnboarding))]
    [NotifyPropertyChangedFor(nameof(ShowReauthOnly))]
    [NotifyPropertyChangedFor(nameof(SeedingBlocked))]
    private bool reauthRequired;

    /// <summary>True when the user belongs to at least one seeding network (gates onboarding
    /// Continue and the seeding directive).</summary>
    [ObservableProperty]
    private bool hasNetworkMembership;

    /// <summary>The user's network memberships in priority order (top = preferred).</summary>
    public ObservableCollection<NetworkMembershipRow> Networks { get; } = new();

    /// <summary>The provider currently mid-flight in a login/link (disables buttons + spins).</summary>
    [ObservableProperty]
    private string? busyProvider;

    /// <summary>Why a link was refused because the identity belongs to — or is owed to — another
    /// account: the "already linked to a different user" conflict, and the staff-permissions guard
    /// that stops an admin's Discord being grafted onto a fresh account. Null when there is none.
    ///
    /// Held as state rather than raised as a toast because the answer is an action, not an
    /// acknowledgement: the user has to leave this account and sign in as the other one, and a
    /// toast that vanishes in four seconds cannot offer that. This is the case that was stranding
    /// admins — they were told "already linked to a different user" (or, before the API carried a
    /// message on the permissions guard, the literal word "Forbidden") with no way to act on it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLinkConflict))]
    private string? linkConflictMessage;

    /// <summary>The provider the outstanding <see cref="LinkConflictMessage"/> is about, so the
    /// recovery button knows which sign-in to start.</summary>
    [ObservableProperty]
    private string? linkConflictProvider;

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
    /// comes back as a bare sign-in prompt when a configured user's credentials are gone. The
    /// network gate is deliberately NOT here — see <see cref="NetworkGateActive"/>.</summary>
    public bool ShowOnboarding => !OnboardingComplete || ReauthRequired;

    /// <summary>
    /// Nothing may be seeded right now: the user is mid-onboarding, needs to sign in again, or
    /// holds no network membership.
    ///
    /// The unattended paths need this as one question. They used to ask <c>ShowOnboarding</c>,
    /// which happened to cover the network gate only because the gate re-opened the overlay —
    /// so decoupling the two would have quietly let an auto-seed fire for a user with no network
    /// had this not been made explicit.
    /// </summary>
    public bool SeedingBlocked => ShowOnboarding || NetworkGateActive;

    /// <summary>The overlay is up only to ask a configured user to sign in again — so it shows the
    /// sign-in step and nothing else: no step counter, no wizard to walk, and "welcome back"
    /// rather than "welcome".</summary>
    public bool ShowReauthOnly => ReauthRequired && OnboardingComplete;

    /// <summary>True while a link conflict is waiting to be resolved (drives the recovery panel in
    /// the onboarding wizard and in Settings).</summary>
    public bool HasLinkConflict => LinkConflictMessage is { Length: > 0 };

    public bool HasAccountName => User is not null;

    /// <summary>Whether the signed-in user holds a <b>global</b> Admin grant
    /// (server-computed on <c>/me</c>, provider-agnostic).</summary>
    public bool IsAdminUser => User?.IsAdmin == true;

    /// <summary>Whether to show the embedded Manage tab: the user can reach at least one
    /// surface the panel offers.
    /// <para>Deliberately wider than <see cref="IsAdminUser"/>, and widened twice. It first
    /// gated on a <i>global</i> grant, hiding the tab from every org Admin — the
    /// people the server surface is built for. It then gated on the <c>CanManage*</c>
    /// flags, which still hid it from <b>operators</b>, whose whole job (answering ready
    /// checks, taking a downed server out of the rotation, setting its seed window) the
    /// panel now serves. The panel itself decides what to show once it loads; this only
    /// decides whether the door exists.</para></summary>
    public bool CanReachManagePanel =>
        User is { } u
        && (u.IsAdmin || u.CanOperateServers || u.CanManageServers || u.CanManageNetworks);

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
                    // Server actively rejected the key (guest swept after 14 idle days, account
                    // deleted, backend reset) — clear stale creds so guest-OK endpoints aren't
                    // poisoned, then ask for a sign-in. Not a re-onboard: the rejection says
                    // nothing about whether this install was configured.
                    _log.LogWarning("Stored credentials rejected; sign-in required");
                    RequireReauth();
                    return;
                }

                // Transient failure (rate-limit / outage / network): keep the stored guest key and
                // onboarding state. Discarding it here would strand a valid guest into a re-register
                // that the server rate-limits — the recurring "can't Continue as Guest" trap.
                _log.LogInformation("get_me failed transiently during restore; keeping stored credentials");
                return;
            }

            // No stored credentials at all. A completed-onboarding flag with no backing identity
            // isn't real (an older build's skip button, a wiped server, or — the common one — a
            // DPAPI blob that won't decrypt on this profile), so an unverified user must not land
            // on the main screen. Ask for a sign-in, but keep the onboarding state: this install
            // WAS configured, and re-running the wizard over it is the "had to onboard again after
            // updating" complaint. Gated on hadStoredCreds so a transient failure to load a genuine
            // session doesn't bounce the user here — a real guest keeps an api_key and a real OAuth
            // user keeps a JWT, so this only fires when the config truly holds neither.
            if (!hadStoredCreds && OnboardingComplete)
            {
                _log.LogInformation("No stored credentials; sign-in required");
                RequireReauth();
            }
        }
        finally
        {
            RunOnUi(() => AuthLoading = false);
        }
    }

    /// <summary>
    /// Drop the unusable credentials and ask the user to sign in, preserving everything else about
    /// their setup.
    ///
    /// The counterpart to <see cref="ResetAllAuth"/> with <c>rearmOnboarding: true</c>, which is now
    /// reserved for the cases where starting over is genuinely what the user asked for (account
    /// deletion, "use a different account"). Losing a session is not one of them.
    /// </summary>
    private void RequireReauth()
    {
        ResetAllAuth(rearmOnboarding: false);
        RunOnUi(() =>
        {
            ReauthRequired = true;
            // The overlay opens on the sign-in step; with ShowReauthOnly set, that is all it shows.
            OnboardingStep = 0;
        });
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
            EnterNetworkStepForNewAccount();
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

    /// <summary>
    /// Route a just-created account to the network step, which is the limited-beta hard gate — it
    /// has no memberships yet by definition.
    ///
    /// Which mechanism holds the overlay open depends on how we got here. Mid-wizard, the step
    /// alone does it. For a user who had already onboarded and was only asked to sign in again,
    /// <c>onboarding_complete</c> is still set, so the step would leave the overlay closed and let
    /// a membership-less account straight through — the gate has to be armed explicitly.
    /// </summary>
    private void EnterNetworkStepForNewAccount()
    {
        if (OnboardingComplete)
        {
            OpenNetworkGate();
        }
        else
        {
            SetOnboardingStep(1);
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
            ReauthRequired = false; // they are signed in again, whatever brought them here
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
                // Whatever sent the user to the sign-in step, they are past it.
                RunOnUi(() => ReauthRequired = false);
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
            EnterNetworkStepForNewAccount();
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
                if (!TryRaiseLinkConflict(provider, e))
                {
                    _toast.Error(ApiValidation.FriendlyError(e.Message));
                }
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
            ClearLinkConflict();
            var resp = await _api.GetLinkRedirectUrlAsync(provider).ConfigureAwait(false);
            // Mark the link as pending only once we're about to hand off to the browser, so a forged
            // link-callback the user never initiated is rejected (see HandleLinkCallbackAsync).
            _oauthState.SetPendingLink(provider);
            OpenBrowser(resp.RedirectUrl, $"{provider} linking");
        }
        catch (Exception e)
        {
            _log.LogError(e, "Failed to start {Provider} linking", provider);
            if (!TryRaiseLinkConflict(provider, e))
            {
                _toast.Error($"Failed to start {Capitalize(provider)} linking");
            }
        }
        finally
        {
            RunOnUi(() => BusyProvider = null);
        }
    }

    /// <summary>
    /// Turn a link failure that means "this identity belongs to another account" into the
    /// recovery panel, and report whether it did.
    ///
    /// Both refusals arrive as 409 Conflict: the identity is already in <c>user_providers</c> under
    /// a different user, or it holds staff permission grants and this account is not a global admin.
    /// Either way the fix is the same and the message already says so — the user has to sign in
    /// with that provider instead of linking it, which is precisely what
    /// <see cref="SwitchAccountAsync"/> does. 403 is accepted too so an older API build (which
    /// returned a bare Forbidden for the permissions guard) still lands here rather than toasting
    /// the word "Forbidden".
    /// </summary>
    private bool TryRaiseLinkConflict(string provider, Exception e)
    {
        if (e is not ApiException { StatusCode: HttpStatusCode.Conflict or HttpStatusCode.Forbidden })
        {
            return false;
        }
        var message = ApiValidation.FriendlyError(e.Message);
        // A bare "Forbidden" body (pre-guard-message API) carries no explanation of its own.
        if (message.Equals("Forbidden", StringComparison.OrdinalIgnoreCase))
        {
            message = $"That {Capitalize(provider)} account belongs to another Fullobby account.";
        }
        RunOnUi(() =>
        {
            LinkConflictProvider = provider;
            LinkConflictMessage = message;
        });
        return true;
    }

    /// <summary>Dismiss the link-conflict panel. Called when the user acts on it, and whenever the
    /// wizard moves, so a stale conflict can't follow them to another step.</summary>
    public void ClearLinkConflict() => RunOnUi(() =>
    {
        LinkConflictMessage = null;
        LinkConflictProvider = null;
    });

    /// <summary>
    /// Abandon the current account and sign in with <paramref name="provider"/> instead — the
    /// resolution to a link conflict.
    ///
    /// Clears local auth before opening the browser, so the callback lands on a clean slate and
    /// takes the login path (which resolves the existing account) rather than the link path that
    /// just refused. Nothing of value is discarded: the account being left is a guest or a
    /// sign-in from moments ago, and the server sweeps abandoned guests on its own.
    ///
    /// Onboarding is re-armed only when it had not finished — i.e. this came from the wizard,
    /// where step 0 is where the user needs to land. Invoked from Settings by an established user
    /// it must not re-arm, or switching accounts would walk them through all five steps again.
    /// </summary>
    [RelayCommand]
    public void SwitchAccount(string provider)
    {
        _log.LogInformation("Leaving the current account to sign in with {Provider}", provider);
        ClearLinkConflict();
        ResetAllAuth(rearmOnboarding: !OnboardingComplete);
        Login(provider);
    }

    /// <summary>
    /// Return the wizard to the sign-in step, discarding whatever account the current run created.
    ///
    /// The wizard is otherwise one-way (0 → 1 → 2 → 3 → 4) and its overlay covers the whole shell,
    /// including Settings and its Sign Out button — so someone who picked the wrong identity at
    /// step 0 had no route back to it at all. That is what left admins stranded on a fresh account
    /// after the client lost its session.
    /// </summary>
    [RelayCommand]
    public void StartOver()
    {
        _log.LogInformation("Restarting onboarding at the sign-in step");
        ClearLinkConflict();
        ResetAllAuth(rearmOnboarding: true);
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

    /// <summary>Shown when an org or staff link is pasted into the app. Those are accepted on the
    /// web page, where an org Admin picks which org to bring in.</summary>
    public const string StaffInviteMessage =
        "This link is for org staff, not players. Open it in a web browser while signed in.";

    /// <summary>Look up an invite link before joining. Returns the preview, or a user-facing error:
    /// for a malformed paste, a dead link (the server's own "ask for a new one" message) or an org
    /// or staff link, which the app doesn't accept. When the preview says the account is already a
    /// member, the membership list is refreshed so the network step can move on.</summary>
    public async Task<(InvitePreview? Preview, string? Error)> PreviewInviteAsync(string link)
    {
        var token = InviteLink.ParseToken(link);
        if (token is null)
        {
            return (null, link.Trim().Length == 0
                ? "Paste the invite link you were sent."
                : "That doesn't look like an invite link. Paste the whole link you were sent.");
        }
        if (OnCooldown("preview_invite", 2))
        {
            return (null, "Please wait a moment before trying again.");
        }
        try
        {
            var preview = await _api.PreviewInviteAsync(token).ConfigureAwait(false);
            if (!preview.IsPlayerLink)
            {
                return (null, StaffInviteMessage);
            }
            if (preview.Already)
            {
                await RefreshNetworksAsync().ConfigureAwait(false);
            }
            return (preview, null);
        }
        catch (Exception e)
        {
            return (null, InviteError(e, "preview"));
        }
    }

    /// <summary>Accept a player invite link. Returns null on success, else a user-facing error. The
    /// token is sent and forgotten — never stored client-side.</summary>
    public async Task<string?> AcceptInviteAsync(string link)
    {
        var token = InviteLink.ParseToken(link);
        if (token is null)
        {
            return "That doesn't look like an invite link. Paste the whole link you were sent.";
        }
        if (OnCooldown("accept_invite", 2))
        {
            return "Please wait a moment before trying again.";
        }
        try
        {
            var accepted = await _api.AcceptInviteAsync(token).ConfigureAwait(false);
            await RefreshNetworksAsync().ConfigureAwait(false);
            var name = accepted.NetworkName ?? "the network";
            _toast.Success(accepted.Already ? $"You're already in {name}" : $"Joined {name}");
            return null;
        }
        catch (Exception e)
        {
            return InviteError(e, "accept");
        }
    }

    /// <summary>Hold a deep-linked invite token for whichever join prompt shows next.</summary>
    public void OfferInvite(string token) => RunOnUi(() => PendingInvite = token);

    /// <summary>Take the pending deep-linked invite, if any, so it prefills only one prompt.</summary>
    public string? TakePendingInvite()
    {
        var token = PendingInvite;
        PendingInvite = null;
        return token;
    }

    /// <summary>"Join Comp HLL?" — the question an invite preview asks.</summary>
    public static string InviteQuestion(InvitePreview preview) =>
        preview.Already
            ? $"You're already in {preview.NetworkName ?? "this network"}."
            : $"Join {preview.NetworkName ?? "this network"}?";

    /// <summary>Who sent the link and its note, for under the question. Empty when neither is set.</summary>
    public static string InviteDetail(InvitePreview preview)
    {
        var parts = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(preview.CreatedByName))
        {
            parts.Add($"Invited by {preview.CreatedByName}");
        }
        if (!string.IsNullOrWhiteSpace(preview.Label))
        {
            parts.Add($"“{preview.Label}”");
        }
        return string.Join(" · ", parts);
    }

    /// <summary>Map an invite preview/accept failure to what the user sees. Every dead link is one
    /// uniform 400 by design; its message already says what to do, so it's shown as-is.</summary>
    private string InviteError(Exception e, string what)
    {
        switch (e)
        {
            case ApiException { StatusCode: HttpStatusCode.TooManyRequests }:
                return "Too many attempts — please wait a minute and try again.";
            case ApiException { StatusCode: HttpStatusCode.BadRequest } api:
                var message = ApiValidation.FriendlyError(api.Message);
                return message.StartsWith("API error", StringComparison.Ordinal)
                    ? "This invite is no longer valid — ask for a new one."
                    : message;
            default:
                _log.LogError(e, "Failed to {What} invite", what);
                return "Couldn't reach the seeding service. Please try again.";
        }
    }

    /// <summary>Join a network by name + code — the legacy way in, kept for networks that still
    /// hand out codes. Returns null on success, else a user-facing error. The code is sent and
    /// forgotten — never stored client-side.</summary>
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

    /// <summary>Raise the join-a-network banner for a user who has already completed onboarding.
    /// Used on session restore with zero memberships and when a directive arrives flagged
    /// <c>join_a_network</c>. Cleared by <see cref="ApplyMemberships"/> once one is confirmed.</summary>
    public void OpenNetworkGate() => RunOnUi(() => NetworkGateActive = true);

    /// <summary>Advance past the wizard's network step: guests finish here (they skip link and
    /// nickname), OAuth users continue to linking. The post-onboarding gate no longer routes
    /// through the wizard at all — it is a banner, so there is no step to advance from.</summary>
    public void ContinueFromNetworkStep()
    {
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
            _log.LogInformation("No network memberships; raising the join-a-network banner");
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
        // A confirmed membership lowers the banner on its own: it exists to make the user join,
        // and they are joined. Without this, a gate armed by a stale or blipped check stayed up
        // until the user went looking for a way to dismiss it.
        if (HasNetworkMembership && NetworkGateActive)
        {
            _log.LogInformation("Membership confirmed; lowering the join-a-network banner");
            NetworkGateActive = false;
        }
    });

    // ── Onboarding helpers ──────────────────────────────────────────────────────

    /// <summary>Move the wizard to <paramref name="step"/>. Drops any outstanding link conflict —
    /// it is about the step being left, and a panel that followed the user forward would offer to
    /// abandon an account they have since resolved.</summary>
    public void SetOnboardingStep(int step) => RunOnUi(() =>
    {
        LinkConflictMessage = null;
        LinkConflictProvider = null;
        OnboardingStep = step;
    });

    /// <summary>Mark first-run complete (dismisses the overlay) and persist it.
    ///
    /// Deliberately does NOT lower the network gate. That used to be necessary because the gate and
    /// the wizard shared one overlay, but the gate tracks whether the account holds a membership —
    /// a fact finishing the wizard doesn't change. It clears itself in <see cref="ApplyMemberships"/>
    /// as soon as one is confirmed. (Completing normally implies a membership anyway: the wizard's
    /// network step won't let anyone past without one.)</summary>
    public void CompleteOnboarding()
    {
        RunOnUi(() => OnboardingComplete = true);
        _config.SetString(ConfigKeys.OnboardingComplete, "true");
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
            // The conflict was about the account being torn down here.
            LinkConflictMessage = null;
            LinkConflictProvider = null;
            // Callers that want the sign-in prompt set this straight after (RequireReauth);
            // clearing it here keeps a stale prompt off an explicit sign-out or start-over.
            ReauthRequired = false;
            if (rearmOnboarding)
            {
                OnboardingComplete = false;
                OnboardingStep = 0;
            }
        });
        if (rearmOnboarding)
        {
            _config.Remove(ConfigKeys.OnboardingComplete);
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
