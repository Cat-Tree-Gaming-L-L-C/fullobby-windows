using System.Text.RegularExpressions;
using ChllSeeder.Core.Scheduling;

namespace ChllSeeder.Core.Tests;

public class AutoSeedTimeTests
{
    [Theory]
    [InlineData("12:00", 12, 0)]
    [InlineData("06:00", 6, 0)]
    [InlineData("9:05", 9, 5)]
    [InlineData("23:59", 23, 59)]
    [InlineData("00:00", 0, 0)]
    [InlineData(" 14:30 ", 14, 30)] // trimmed
    public void TryParseUtc_Valid(string input, int h, int m)
    {
        Assert.True(AutoSeedTime.TryParseUtc(input, out var hours, out var minutes));
        Assert.Equal(h, hours);
        Assert.Equal(m, minutes);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("25:00")]
    [InlineData("12:60")]
    [InlineData("9:5")]
    [InlineData("not-a-time")]
    [InlineData("12")]
    public void TryParseUtc_Invalid(string? input)
    {
        Assert.False(AutoSeedTime.TryParseUtc(input, out _, out _));
    }

    [Theory]
    [InlineData("14:30", "14:30:00")]
    [InlineData("0:00", "00:00:00")]
    [InlineData("23:59:59", "23:59:59")]
    [InlineData("9:05", "09:05:00")]
    public void NormalizeHms_PadsAndKeepsSeconds(string input, string expected)
    {
        Assert.Equal(expected, AutoSeedTime.NormalizeHms(input));
    }

    [Theory]
    [InlineData("25:00")]
    [InlineData("12:60")]
    [InlineData("123:45:67")]
    [InlineData("")]
    public void NormalizeHms_Invalid_Throws(string input)
    {
        Assert.Throws<FormatException>(() => AutoSeedTime.NormalizeHms(input));
    }

    [Fact]
    public void NormalizeHms_NullByte_Throws()
    {
        Assert.Throws<FormatException>(() => AutoSeedTime.NormalizeHms("14:30\0"));
    }

    [Fact]
    public void TryParseStoredUtc_AcceptsHmAndHms()
    {
        Assert.True(AutoSeedTime.TryParseStoredUtc("06:00", out var t1));
        Assert.Equal(new TimeOnly(6, 0), t1);

        Assert.True(AutoSeedTime.TryParseStoredUtc("06:00:30", out var t2));
        Assert.Equal(new TimeOnly(6, 0), t2);

        Assert.False(AutoSeedTime.TryParseStoredUtc("nope", out _));
    }

    [Fact]
    public void UtcToLocalHms_ReturnsHhMmSs()
    {
        var s = AutoSeedTime.UtcToLocalHms(12, 0);
        Assert.Matches(new Regex(@"^\d{2}:\d{2}:00$"), s);
    }
}
