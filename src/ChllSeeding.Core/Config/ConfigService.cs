using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ChllSeeding.Core.Security;
using Microsoft.Extensions.Logging;

namespace ChllSeeding.Core.Config;

/// <summary>
/// In-memory config store backed by an atomically-written JSON file under
/// <c>%APPDATA%\org.comphll.chllseeding\config.json</c>.
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

    private long _lastSaveMs;
    private bool _savePending;

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
        RestrictDirectoryAcl(configDir);

        Load();
        MigratePlaintextSecrets();
    }

    // ── Public API ────────────────────────────────────────────────────────

    /// <summary>Get a string value, transparently decrypting sensitive keys.
    /// Returns <c>null</c> when absent or not a JSON string.</summary>
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
        return DpapiProtector.MaybeDecrypt(key, el.GetString()!);
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
        if (value is string s && DpapiProtector.IsSensitive(key))
        {
            var protectedValue = DpapiProtector.MaybeEncrypt(key, s);
            if (s.Length > 0 && !DpapiProtector.IsEncrypted(protectedValue))
            {
                _log.LogWarning(
                    "DPAPI encryption failed for sensitive key '{Key}' — storing it as plaintext in " +
                    "config.json. The value stays usable but is no longer protected at rest.", key);
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

    /// <summary>Remove a key (and persist). No-op when the key is absent.</summary>
    public void Remove(string key)
    {
        bool removed;
        lock (_gate)
        {
            removed = _config.Remove(key);
        }
        if (removed)
        {
            SaveToDisk();
        }
    }

    /// <summary>Flush any save coalesced by the throttle. Call on shutdown so nothing is lost.</summary>
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
            _log.LogInformation("Flushed pending config save on shutdown");
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
                        _config[key] = JsonSerializer.SerializeToElement(DpapiProtector.Encrypt(val));
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
    }

    private void SaveToDisk()
    {
        string json;
        lock (_gate)
        {
            json = JsonSerializer.Serialize(_config, WriteOptions);
        }
        try
        {
            AtomicFile.WriteAllBytes(_configPath, Encoding.UTF8.GetBytes(json));
        }
        catch (Exception e)
        {
            _log.LogError(e, "Failed to save config to {Path}", _configPath);
        }
    }

    /// <summary>Restrict the config directory's ACL to the current user only
    /// (disable inheritance, grant the user full control). Port of
    /// <c>restrict_directory_acl</c> — shells out to <c>icacls</c>.</summary>
    private void RestrictDirectoryAcl(string path)
    {
        if (!RunIcacls(path, "/inheritance:r"))
        {
            return;
        }

        var username = Environment.UserName;
        if (!IsSafeUsername(username))
        {
            _log.LogWarning("USERNAME is empty or contains unexpected characters — skipping ACL grant");
            return;
        }

        if (RunIcacls(path, "/grant:r", $"{username}:F"))
        {
            _log.LogInformation("Config directory ACLs restricted to current user");
        }
    }

    private bool RunIcacls(string path, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("icacls")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add(path);
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                _log.LogWarning("Failed to start icacls");
                return false;
            }
            // Drain BOTH redirected streams before waiting: icacls writes a "Successfully processed
            // N files" line to stdout, and reading only stderr can deadlock if stdout fills its pipe.
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEnd();
            stdoutTask.GetAwaiter().GetResult();
            proc.WaitForExit();
            if (proc.ExitCode != 0)
            {
                _log.LogWarning("icacls {Args} returned non-zero: {Err}", string.Join(' ', args), stderr);
                return false;
            }
            return true;
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Failed to run icacls");
            return false;
        }
    }

    // ── Validation (public for unit coverage) ──────

    /// <summary>A config key must be non-empty, ≤255 chars, and free of null bytes.</summary>
    public static bool IsValidKey(string key) =>
        key.Length is > 0 and <= 255 && !key.Contains('\0');

    /// <summary>A Windows username safe to interpolate into an <c>icacls</c> grant argument.</summary>
    public static bool IsSafeUsername(string name) =>
        name.Length is > 0 and <= 104
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-' or ' ');
}
