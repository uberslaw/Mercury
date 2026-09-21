namespace Mercury.Tests;

public class BandwidthUnitTests
{
    [Fact]
    public void TenMegabytesPerSecondIsEightyMegabits()
    {
        Assert.Equal("MB/s", BandwidthUnit.Label(false));
        Assert.Equal("Mbps", BandwidthUnit.Label(true));
        Assert.Equal(80, BandwidthUnit.ToDisplay(10, megabits: true));
        Assert.Equal(10, BandwidthUnit.FromDisplay(80, megabits: true));
        Assert.Equal("80", BandwidthUnit.FormatMegabytes(10, megabits: true));
        Assert.True(BandwidthUnit.TryParseMegabytes("80", megabits: true, out var mb));
        Assert.Equal(10, mb);
        Assert.Equal("80", BandwidthUnit.ConvertDisplayText("10", fromMegabits: false, toMegabits: true));
        Assert.Equal("10", BandwidthUnit.ConvertDisplayText("80", fromMegabits: true, toMegabits: false));
    }

    [Fact]
    public void ByteFormatterSpeedUsesMegabitsWhenAsked()
    {
        var tenMegabytesPerSecond = 10d * 1024 * 1024;
        Assert.Contains("Mbps", ByteFormatter.Speed(tenMegabytesPerSecond, megabits: true), StringComparison.Ordinal);
        Assert.Contains("80", ByteFormatter.Speed(tenMegabytesPerSecond, megabits: true), StringComparison.Ordinal);
        Assert.DoesNotContain("Mbps", ByteFormatter.Speed(tenMegabytesPerSecond), StringComparison.Ordinal);
    }
}
