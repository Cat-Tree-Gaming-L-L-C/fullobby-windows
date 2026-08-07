using Fullobby.Core.Native;

namespace Fullobby.Core.Tests;

public class SteamLauncherIpTests
{
    [Theory]
    [InlineData("192.168.1.1")]
    [InlineData("10.0.0.1:27015")]
    [InlineData("0.0.0.0")]
    [InlineData("255.255.255.255")]
    [InlineData("1.2.3.4:65535")]
    public void ValidIps(string ip) => Assert.Null(SteamLauncher.ValidateServerIp(ip));

    [Theory]
    [InlineData("256.1.1.1")]          // octet out of range
    [InlineData("1.2.3.4:0")]          // port zero
    [InlineData("1.2.3.4:65536")]      // port too high
    [InlineData("")]                   // empty
    [InlineData("abc.def.ghi.jkl")]    // letters
    [InlineData("111.111.111.111:65535X")] // too long / trailing junk
    [InlineData("1.2.3.4\0")]          // null byte
    [InlineData("1.2.3.4; rm -rf /")]  // command injection
    [InlineData("1.2.3")]              // too few octets
    [InlineData("1.2.3.4.")]           // trailing dot
    public void InvalidIps(string ip) => Assert.NotNull(SteamLauncher.ValidateServerIp(ip));
}
