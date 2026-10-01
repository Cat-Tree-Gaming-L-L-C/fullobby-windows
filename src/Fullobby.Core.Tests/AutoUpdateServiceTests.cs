using Fullobby.Core.Update;

namespace Fullobby.Core.Tests;

public class AutoUpdateServiceTests
{
    private const string Root = @"C:\Users\x\AppData\Local\Fullobby\updates";
    private const string InRoot = Root + @"\abc123\Fullobby-Setup-0.5.0.exe";

    // A signature that won't verify: every staged update reaching the signature check is discarded,
    // so these tests pin the checks in front of it and the discard itself.
    private static StagedUpdate Staged(string version = "0.5.0", string path = InRoot, int attempts = 0) =>
        new(version, new string('a', 64), "bm90LWEtc2lnbmF0dXJl", path, attempts);

    [Fact]
    public void Decide_NothingStaged_IsNone() =>
        Assert.Equal(StagedUpdateAction.None, AutoUpdateService.Decide(null, "0.4.4", Root, _ => true));

    [Theory]
    [InlineData("0.5.0")] // installed: the staged version is now the running one
    [InlineData("0.6.0")] // overtaken by a newer install
    public void Decide_NotNewerThanRunning_IsDiscarded(string running) =>
        Assert.Equal(StagedUpdateAction.Discard, AutoUpdateService.Decide(Staged(), running, Root, _ => true));

    [Fact]
    public void Decide_OutOfAttempts_IsDiscarded() =>
        Assert.Equal(StagedUpdateAction.Discard, AutoUpdateService.Decide(
            Staged(attempts: AutoUpdateService.MaxInstallAttempts), "0.4.4", Root, _ => true));

    [Theory]
    [InlineData(@"C:\Users\x\Downloads\evil.exe")]
    [InlineData(Root + @"\..\evil.exe")]
    [InlineData(Root + @"-other\evil.exe")]
    public void Decide_PathOutsideStagingFolder_IsDiscarded(string path) =>
        Assert.Equal(StagedUpdateAction.Discard, AutoUpdateService.Decide(
            Staged(path: path), "0.4.4", Root, _ => true));

    [Fact]
    public void Decide_FileGone_IsDiscarded() =>
        Assert.Equal(StagedUpdateAction.Discard, AutoUpdateService.Decide(Staged(), "0.4.4", Root, _ => false));

    [Fact]
    public void Decide_BadSignature_IsDiscarded() =>
        Assert.Equal(StagedUpdateAction.Discard, AutoUpdateService.Decide(Staged(), "0.4.4", Root, _ => true));

    [Fact]
    public void ShouldOffer_NewVersion() =>
        Assert.True(AutoUpdateService.ShouldOffer("0.5.0", null, null, null));

    [Theory]
    [InlineData("0.5.0", null, null)] // already staged
    [InlineData(null, "0.5.0", null)] // declined
    [InlineData(null, null, "0.5.0")] // already asked this run
    public void ShouldOffer_NotAgain(string? staged, string? declined, string? offered) =>
        Assert.False(AutoUpdateService.ShouldOffer("0.5.0", staged, declined, offered));

    [Fact]
    public void ShouldOffer_NewerThanDeclined() =>
        Assert.True(AutoUpdateService.ShouldOffer("0.5.1", null, "0.5.0", null));
}
