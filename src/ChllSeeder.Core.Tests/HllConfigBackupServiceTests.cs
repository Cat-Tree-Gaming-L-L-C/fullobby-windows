using ChllSeeder.Core.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChllSeeder.Core.Tests;

public class EfficiencyIniTests
{
    [Fact]
    public void Resolution()
    {
        var result = HllConfigBackupService.ApplyEfficiencyIniSettings("ResolutionSizeX=1920\nResolutionSizeY=1080\n");
        Assert.Contains("ResolutionSizeX=1024", result);
        Assert.Contains("ResolutionSizeY=768", result);
    }

    [Fact]
    public void FullscreenMode()
    {
        var result = HllConfigBackupService.ApplyEfficiencyIniSettings("FullscreenMode=0\nLastConfirmedFullscreenMode=0\n");
        Assert.Contains("FullscreenMode=2", result);
        Assert.Contains("LastConfirmedFullscreenMode=2", result);
    }

    [Fact]
    public void FrameRate()
    {
        var result = HllConfigBackupService.ApplyEfficiencyIniSettings("FrameRateLimit=144.000000\n");
        Assert.Contains("FrameRateLimit=30.000000", result);
    }

    [Fact]
    public void GraphicsQuality()
    {
        var result = HllConfigBackupService.ApplyEfficiencyIniSettings(
            "sg.ViewDistanceQuality=4\nsg.ShadowQuality=3\nsg.TextureQuality=4\n");
        Assert.Contains("sg.ViewDistanceQuality=0", result);
        Assert.Contains("sg.ShadowQuality=0", result);
        Assert.Contains("sg.TextureQuality=0", result);
    }

    [Fact]
    public void Audio()
    {
        var result = HllConfigBackupService.ApplyEfficiencyIniSettings("MasterVolume=1.000000\nAudioQualityLevel=2\n");
        Assert.Contains("MasterVolume=0.000000", result);
        Assert.Contains("AudioQualityLevel=0", result);
    }

    [Fact]
    public void PreservesOtherSettings()
    {
        var result = HllConfigBackupService.ApplyEfficiencyIniSettings("CustomSetting=MyValue\nResolutionSizeX=1920\n");
        Assert.Contains("CustomSetting=MyValue", result);
        Assert.Contains("ResolutionSizeX=1024", result);
    }

    [Fact]
    public void CrlfOutput()
    {
        var result = HllConfigBackupService.ApplyEfficiencyIniSettings("Setting1=A\nSetting2=B\n");
        Assert.Contains("\r\n", result);
        Assert.EndsWith("\r\n", result);
    }

    [Fact]
    public void NoTrailingNewline()
    {
        var result = HllConfigBackupService.ApplyEfficiencyIniSettings("Setting1=A\nSetting2=B");
        Assert.Contains("\r\n", result);
        Assert.EndsWith("Setting2=B", result);
    }
}

public class HllConfigBackupRoundTripTests : IDisposable
{
    private readonly string _root;
    private readonly string _configPath;
    private readonly string _backupBase;
    private readonly HllConfigBackupService _svc;

    public HllConfigBackupRoundTripTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "chll_test_backup_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _configPath = Path.Combine(_root, "config", "GameUserSettings.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
        _backupBase = Path.Combine(_root, "backup", "HLL");
        _svc = new HllConfigBackupService(NullLogger<HllConfigBackupService>.Instance, _configPath, _backupBase);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void BackupThenApplyThenRestore_RoundTrips()
    {
        const string original = "ResolutionSizeX=1920\r\nMasterVolume=1.000000\r\nCustomSetting=Keep\r\n";
        File.WriteAllText(_configPath, original);

        _svc.BackupConfig();
        Assert.False(_svc.IsEfficiencyModeApplied);

        _svc.ApplyEfficiencySettings();
        Assert.True(_svc.IsEfficiencyModeApplied);
        var degraded = File.ReadAllText(_configPath);
        Assert.Contains("ResolutionSizeX=1024", degraded);
        Assert.Contains("MasterVolume=0.000000", degraded);
        Assert.Contains("CustomSetting=Keep", degraded);

        _svc.RestoreAfterSeeding();
        Assert.False(_svc.IsEfficiencyModeApplied);
        Assert.Equal(original, File.ReadAllText(_configPath));
    }

    [Fact]
    public void Apply_IsIdempotentWithinSession()
    {
        File.WriteAllText(_configPath, "ResolutionSizeX=1920\r\n");
        _svc.ApplyEfficiencySettings();
        // A second apply is a no-op (flag already claimed) and must not throw.
        _svc.ApplyEfficiencySettings();
        Assert.True(_svc.IsEfficiencyModeApplied);
    }

    [Fact]
    public void Backup_SkippedWhileEfficiencyActive()
    {
        const string original = "ResolutionSizeX=1920\r\n";
        File.WriteAllText(_configPath, original);
        _svc.BackupConfig();
        _svc.ApplyEfficiencySettings();

        // Backing up the degraded config must NOT clobber the good backup.
        _svc.BackupConfig();
        _svc.RestoreAfterSeeding();
        Assert.Equal(original, File.ReadAllText(_configPath));
    }

    [Fact]
    public void StartupRecovery_RestoresWhenFlagAndBackupPresent()
    {
        const string original = "ResolutionSizeX=1920\r\n";
        File.WriteAllText(_configPath, original);
        _svc.BackupConfig();
        _svc.ApplyEfficiencySettings(); // sets the persistent flag and degrades the live config

        // Simulate a fresh process: a new service instance over the same paths,
        // with the flag file still on disk from the "crashed" session.
        var fresh = new HllConfigBackupService(NullLogger<HllConfigBackupService>.Instance, _configPath, _backupBase);
        fresh.CheckAndRestoreOnStartup();

        Assert.Equal(original, File.ReadAllText(_configPath));
        Assert.NotNull(fresh.TakeStartupRestoreNotice());
        Assert.Null(fresh.TakeStartupRestoreNotice()); // notice is one-shot
    }

    [Fact]
    public void IsConfigOverwritten_EulaSignals()
    {
        File.WriteAllText(_configPath, "Foo=1\r\nLastSeenEULAVersion=1\r\n");
        Assert.False(_svc.IsConfigOverwritten());

        File.WriteAllText(_configPath, "Foo=1\r\nLastSeenEULAVersion=0\r\n");
        _svc.InvalidateConfigCache();
        Assert.True(_svc.IsConfigOverwritten());
    }
}
