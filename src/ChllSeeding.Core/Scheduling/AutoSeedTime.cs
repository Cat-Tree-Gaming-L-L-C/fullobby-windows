using System.Globalization;
using System.Text.RegularExpressions;

namespace ChllSeeding.Core.Scheduling;

/// <summary>
/// Auto-seed time parsing + UTC↔local conversion. The user enters the daily seed time in
/// <b>UTC</b> (HH:MM); we store that UTC string and create the Windows scheduled task at the
/// equivalent <b>local</b> time (schtasks triggers run in local time). The missed-task monitor
/// compares the stored UTC time against the current UTC clock. Port of the time helpers in
/// <c>components/settings.rs</c> + <c>validate_start_time</c> in <c>task_scheduler.rs</c>.
/// </summary>
public static partial class AutoSeedTime
{
    // HH:MM or HH:MM:SS, single-digit hours allowed (mirrors task_scheduler.rs TIME_REGEX).
    [GeneratedRegex(@"^([01]?[0-9]|2[0-3]):([0-5][0-9])(?::([0-5][0-9]))?$")]
    private static partial Regex TimeRegex();

    /// <summary>Parse a user-entered UTC "HH:MM" (24-hour). Returns false on any malformed input.</summary>
    public static bool TryParseUtc(string? input, out int hours, out int minutes)
    {
        hours = 0;
        minutes = 0;
        if (string.IsNullOrEmpty(input))
        {
            return false;
        }

        var m = TimeRegex().Match(input.Trim());
        if (!m.Success)
        {
            return false;
        }

        hours = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        minutes = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        return hours is >= 0 and <= 23 && minutes is >= 0 and <= 59;
    }

    /// <summary>Parse a stored UTC time ("HH:MM" or "HH:MM:SS") into a <see cref="TimeOnly"/>.</summary>
    public static bool TryParseStoredUtc(string? stored, out TimeOnly time)
    {
        time = default;
        if (!TryParseUtc(stored, out var h, out var m))
        {
            return false;
        }
        time = new TimeOnly(h, m);
        return true;
    }

    /// <summary>
    /// Validate + normalize a "HH:MM"/"HH:MM:SS" time to zero-padded "HH:MM:SS". Rejects null bytes
    /// and over-length input. Port of <c>validate_start_time</c>. Throws <see cref="FormatException"/>.
    /// </summary>
    public static string NormalizeHms(string startTime)
    {
        if (startTime.Contains('\0') || startTime.Length > 8)
        {
            throw new FormatException("Invalid time");
        }
        var m = TimeRegex().Match(startTime);
        if (!m.Success)
        {
            throw new FormatException($"Invalid time: '{startTime}'");
        }
        var h = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var min = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        var sec = m.Groups[3].Success ? int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
        return $"{h:D2}:{min:D2}:{sec:D2}";
    }

    /// <summary>
    /// Convert a UTC time to the equivalent local time, formatted "HH:MM:SS" for the scheduled-task
    /// StartBoundary. Mirrors Rust's <c>utc_to_local</c>: today's local date is combined with the UTC
    /// time, interpreted as UTC, then converted to local.
    /// </summary>
    public static string UtcToLocalHms(int utcHours, int utcMinutes)
    {
        var local = UtcToLocal(utcHours, utcMinutes, DateTime.Now);
        return $"{local.Hour:D2}:{local.Minute:D2}:00";
    }

    /// <summary>Human-readable local time for the setup prompt, e.g. "7:00 AM".</summary>
    public static string UtcToLocalDisplay(int utcHours, int utcMinutes)
    {
        var local = UtcToLocal(utcHours, utcMinutes, DateTime.Now);
        return local.ToString("h:mm tt", CultureInfo.InvariantCulture);
    }

    private static DateTime UtcToLocal(int utcHours, int utcMinutes, DateTime localNow)
    {
        var date = localNow.Date;
        var utc = new DateTime(date.Year, date.Month, date.Day, utcHours, utcMinutes, 0, DateTimeKind.Utc);
        return utc.ToLocalTime();
    }
}
