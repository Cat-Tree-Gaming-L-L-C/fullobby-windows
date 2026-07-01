using System.Reflection;

namespace ChllSeeding.Core.Update;

/// <summary>
/// Resolves the running app's version for the self-update comparison.
/// </summary>
public static class AppVersion
{
    /// <summary>
    /// The version string to compare against the update manifest. Prefers
    /// <see cref="AssemblyInformationalVersionAttribute"/>, which preserves a pre-release suffix
    /// (e.g. <c>1.2.4-beta.1</c>) — unlike <see cref="AssemblyName.Version"/>, which the build
    /// truncates to the numeric core (<c>1.2.4</c>). Keeping the suffix matters on the beta channel:
    /// the manifest advertises the next prerelease (<c>1.2.4-beta.2</c>), and by SemVer precedence a
    /// bare <c>1.2.4</c> outranks any <c>1.2.4-beta.*</c>, so comparing on the numeric core alone would
    /// make <see cref="UpdateValidation.IsUpdateAvailable"/> conclude the newer beta is a downgrade and
    /// never offer it. Falls back to the numeric assembly version when no informational version is set.
    /// </summary>
    public static string ForUpdateCheck(Assembly assembly)
    {
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            return StripBuildMetadata(informational);
        }
        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    /// <summary>Strip SemVer build metadata — everything from the first <c>+</c> — that the SDK appends
    /// to the informational version (the source revision), e.g.
    /// <c>1.2.4-beta.1+abc1234</c> → <c>1.2.4-beta.1</c>. Precedence ignores build metadata anyway;
    /// removing it here keeps logs and the comparison input clean.</summary>
    public static string StripBuildMetadata(string version)
    {
        var plus = version.IndexOf('+');
        return plus >= 0 ? version[..plus] : version;
    }
}
