using System.Text;
using System.Text.Json;
using Fullobby.Core.Security;
using Microsoft.Extensions.Logging;

namespace Fullobby.Core.Config;

/// <summary>
/// In-memory config store backed by an atomically-written JSON file under
/// <c>%APPDATA%\com.fullobby.app\config.json</c>.
///
/// Deliberate design choices:
/// <list type="bullet">
/// <item>The old-directory migration (<c>migrate_v1_paths</c>) is dropped — clean break.</item>
/// <item>Sensitive keys are encrypted/decrypted transparently here rather than at call sites.</item>
/// </list>
/// Registered as a DI singleton; constructed once at startup.
/// </summary>
public sealed class ConfigService
{
    /// <summary>Throttle window: at most one disk write per this interval; the rest are
    /// coalesced and flushed by the next out-of-window write or <see cref="FlushPendingSaves"/>.</summary>
    private const int SaveDebounceMs = 500;

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly object _gate = new();
    private readonly Dictionary<string, JsonElement> _config = new(StringComparer.Ordinal);
    private readonly string _configPath;
    private readonly ILogger<ConfigService> _log;

    /// <summary>Sensitive keys held in memory but deliberately excluded from the file, because DPAPI
    /// could not protect them. The session keeps working; nothing unprotected reaches the disk.</summary>
    private readonly HashSet<string> _unprotectedKeys = new(StringComparer.Ordinal);

    private long _lastSaveMs;
    private bool _savePending;

    /// <summary>Raised when a secret could not be encrypted and so will not be persisted. The shell
    /// surfaces this — otherwise the user is silently signed out on the next launch with no reason.</summary>
    public event Action? SecretProtectionUnavailable;

    /// <summary>True once any secret has failed protection this session.</summary>
    public bool HasUnprotectedSecrets
    {
        get { lock (_gate) { return _unprotectedKeys.Count > 0; } }
    }

    /// <summary>Production constructor — uses the branded config directory.</summary>
    public ConfigService(ILogger<ConfigService> log) : this(log, Branding.ConfigDir) { }

    /// <summary>Testable constructor — points the store at an arbitrary directory.</summary>
    public ConfigService(ILogger<ConfigService> log, string configDir)
    {
        _log = log;
        _configPath = Path.Combine(configDir, "config.json");

        try
        {
            Directory.CreateDirectory(configDir);
        }
        catch (Exception e)
        {
            _log.LogError(e, "Failed to create config directory {Dir}", configDir);
        }

        // Idempotent: covers fresh dirs and dirs whose permissions were changed.
        DirectoryHardening.RestrictToCurrentUser(configDir, _log);

        Load();
        MigratePlaintextSecrets();
    }

    // ── Public API ────────────────────────────────────────────────────────

    /// <summary>Get a string value, transparently decrypting sensitive keys.
    /// Returns <c>null</c> when absent, not a JSON string, or — for a sensitive key — when the
    /// stored ciphertext cannot be decrypted on this machine/profile (see
    /// <see cref="DpapiProtector.MaybeDecrypt"/>).</summary>
    public string? GetString(string key)
    {
        JsonElement el;
        lock (_gate)
        {
            if (!_config.TryGetValue(key, out el))
            {
                return null;
            }
        }
        if (el.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        var decrypted = DpapiProtector.MaybeDecrypt(key, el.GetString()!);
        if (decrypted is null)
        {
            // An undecryptable secret is reported the same way an unprotectable one is: the user
            // is about to be asked to sign in again and would otherwise have no way to know why.
            _log.LogError(
                "Could not decrypt config value for key '{Key}' on this profile — treating it as "
                + "absent. Sign-in will be required again.", key);
            SecretProtectionUnavailable?.Invoke();
        }
        return decrypted;
    }

    /// <summary>Get a strongly-typed value, or <paramref name="fallback"/> when absent or undeserializable.</summary>
    public T? Get<T>(string key, T? fallback = default)
    {
        JsonElement el;
        lock (_gate)
        {
            if (!_config.TryGetValue(key, out el))
            {
                return fallback;
            }
        }
        try
        {
            return el.Deserialize<T>() ?? fallback;
        }
        catch (JsonException)
        {
            return fallback;
        }
    }

    /// <summary>Convenience for the many config flags stored as the strings "true"/"false".</summary>
    public bool GetBool(string key, bool fallback = false)
    {
        var s = GetString(key);
        return s switch
        {
            "true" => true,
            "false" => false,
            _ => fallback,
        };
    }

    /// <summary>Set a string value (encrypted at rest for sensitive keys) and trigger a throttled save.</summary>
    public void SetString(string key, string value) => Set(key, value);

    /// <summary>Set any serializable value and trigger a throttled save to disk.</summary>
    public void Set<T>(string key, T value)
    {
        if (!IsValidKey(key))
        {
            _log.LogError("Invalid config key: '{Key}' (empty, too long, or contains null bytes)", key);
            throw new ArgumentException("Invalid config key", nameof(key));
        }

        JsonElement element;
        var unprotected = false;
        if (value is string s && DpapiProtector.IsSensitive(key))
        {
            // Fail closed: if DPAPI can't protect the value we keep it in memory so the current
            // session carries on, but it is excluded from the file (see SaveToDisk). Writing it in
            // the clear would leave a long-lived refresh token readable by any same-user process,
            // defeating both the CurrentUser scope and the app-specific entropy above it.
            unprotected = !DpapiProtector.TryEncrypt(key, s, out var protectedValue);
            if (unprotected)
            {
                _log.LogError(
                    "DPAPI encryption failed for sensitive key '{Key}' — it will NOT be saved to " +
                    "config.json. The current session keeps working; sign-in will be required again " +
                    "on the next launch.", key);
            }
            element = JsonSerializer.SerializeToElement(protectedValue);
        }
        else
        {
            element = JsonSerializer.SerializeToElement(value);
        }

        bool shouldSave;
        long now = Environment.TickCount64;
        lock (_gate)
        {
            _config[key] = element;
            if (unprotected)
            {
                _unprotectedKeys.Add(key);
            }
            else
            {
                _unprotectedKeys.Remove(key);
            }
            if (now - _lastSaveMs >= SaveDebounceMs)
            {
                _lastSaveMs = now;
                _savePending = false;
                shouldSave = true;
            }
            else
            {
                _savePending = true;
                shouldSave = false;
            }
        }

        if (shouldSave)
        {
            SaveToDisk();
        }
        if (unprotected)
        {
            SecretProtectionUnavailable?.Invoke();
        }
    }

    /// <summary>Remove a key. Goes through the same throttle as <see cref="Set{T}"/> — sign-out
    /// issues several removals back to back, and writing each one through synchronously meant six
    /// serialize + fsync + rename cycles in a row. Callers needing durability right away follow up
    /// with <see cref="FlushPendingSaves"/>, as the auth paths already do.</summary>
    public void Remove(string key)
    {
        bool shouldSave;
        long now = Environment.TickCount64;
        lock (_gate)
        {
            if (!_config.Remove(key))
            {
                return;
            }
            _unprotectedKeys.Remove(key);
            if (now - _lastSaveMs >= SaveDebounceMs)
            {
                _lastSaveMs = now;
                _savePending = false;
                shouldSave = true;
            }
            else
            {
                _savePending = true;
                shouldSave = false;
            }
        }
        if (shouldSave)
        {
            SaveToDisk();
        }
    }

    /// <summary>Flush any save coalesced by the throttle. Called on shutdown, and after
    /// writes that must be durable immediately (auth credentials, onboarding state).</summary>
    public void FlushPendingSaves()
    {
        bool pending;
        lock (_gate)
        {
            pending = _savePending;
            _savePending = false;
        }
        if (pending)
        {
            SaveToDisk();
            _log.LogDebug("Flushed pending config save");
        }
    }

    // ── Internals ─────────────────────────────────────────────────────────

    private void Load()
    {
        if (!File.Exists(_configPath))
        {
            _log.LogInformation("No config file found, starting with empty config");
            return;
        }

        try
        {
            var content = File.ReadAllText(_configPath);
            var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(content);
            if (data is not null)
            {
                lock (_gate)
                {
                    foreach (var (k, v) in data)
                    {
                        _config[k] = v;
                    }
                }
                _log.LogInformation("Config loaded from {Path}", _configPath);
            }
        }
        catch (JsonException e)
        {
            _log.LogWarning(e, "Config parse error, starting with empty config");
        }
        catch (IOException e)
        {
            _log.LogWarning(e, "Config read error, starting with empty config");
        }
    }

    /// <summary>Re-encrypt any sensitive values still stored as plaintext (seamless
    /// migration from pre-encryption app versions).</summary>
    private void MigratePlaintextSecrets()
    {
        var changed = false;
        var unavailable = false;
        lock (_gate)
        {
            foreach (var key in DpapiProtector.SensitiveKeys)
            {
                if (_config.TryGetValue(key, out var el)
                    && el.ValueKind == JsonValueKind.String)
                {
                    var val = el.GetString()!;
                    if (val.Length > 0 && !DpapiProtector.IsEncrypted(val))
                    {
                        // TryEncrypt (not Encrypt): a DPAPI failure must not throw out of the
                        // constructor and crash startup.
                        if (!DpapiProtector.TryEncrypt(key, val, out var encrypted))
                        {
                            // Can't protect a value a pre-encryption version left here in the clear.
                            // Keep it in memory so this session still works, and mark it so the save
                            // below rewrites the file without it — scrubbing the exposed plaintext
                            // rather than leaving it there hoping a later attempt succeeds.
                            _unprotectedKeys.Add(key);
                            changed = true;
                            unavailable = true;
                            _log.LogError(
                                "DPAPI unavailable — removing unprotected value for key '{Key}' from " +
                                "config.json. Sign-in will be required again on the next launch.", key);
                            continue;
                        }
                        _config[key] = JsonSerializer.SerializeToElement(encrypted);
                        changed = true;
                        _log.LogInformation("Re-encrypted plaintext config value for key '{Key}'", key);
                    }
                }
            }
        }

        if (changed)
        {
            SaveToDisk();
        }
        if (unavailable)
        {
            SecretProtectionUnavailable?.Invoke();
        }
    }

    private void SaveToDisk()
    {
        // Snapshot under the lock, serialize outside it. Serializing the whole document inline held
        // the gate against every concurrent Get/Set from the SSE, heartbeat, and UI threads.
        Dictionary<string, JsonElement> snapshot;
        lock (_gate)
        {
            if (_unprotectedKeys.Count == 0)
            {
                snapshot = new Dictionary<string, JsonElement>(_config, StringComparer.Ordinal);
            }
            else
            {
                // Secrets DPAPI couldn't protect are held in memory only — never written.
                snapshot = new Dictionary<string, JsonElement>(_config.Count, StringComparer.Ordinal);
                foreach (var (k, v) in _config)
                {
                    if (!_unprotectedKeys.Contains(k))
                    {
                        snapshot[k] = v;
                    }
                }
            }
        }

        var json = JsonSerializer.Serialize(snapshot, WriteOptions);
        try
        {
            AtomicFile.WriteAllBytes(_configPath, Encoding.UTF8.GetBytes(json));
        }
        catch (Exception e)
        {
            _log.LogError(e, "Failed to save config to {Path}", _configPath);
        }
    }


    // ── Validation (public for unit coverage) ──────

    /// <summary>A config key must be non-empty, ≤255 chars, and free of null bytes.</summary>
    public static bool IsValidKey(string key) =>
        key.Length is > 0 and <= 255 && !key.Contains('\0');
}
