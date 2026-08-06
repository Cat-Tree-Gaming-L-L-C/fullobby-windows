using System.Runtime.InteropServices;

namespace Fullobby.Core.Native;

/// <summary>
/// Non-PII OS facts for session analytics:
/// <see cref="OsVersion"/> is the <c>major.minor.build</c> string (e.g. "10.0.22631"),
/// <see cref="OsArch"/> the arch name (e.g. "x86_64", "aarch64", "x86", "arm").
/// </summary>
public static class OsInfo
{
    /// <summary>Windows version as <c>major.minor.build</c> (e.g. "10.0.22631"), or "unknown".</summary>
    public static string OsVersion
    {
        get
        {
            try
            {
                var v = Environment.OSVersion.Version;
                return $"{v.Major}.{v.Minor}.{v.Build}";
            }
            catch
            {
                return "unknown";
            }
        }
    }

    /// <summary>CPU architecture name ("x86_64", "aarch64", "x86", "arm").</summary>
    public static string OsArch => RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => "x86_64",
        Architecture.Arm64 => "aarch64",
        Architecture.X86 => "x86",
        Architecture.Arm => "arm",
        var other => other.ToString().ToLowerInvariant(),
    };
}
