using Wattkin.Hardware;
using Wattkin.Settings;
using Xunit;

namespace Wattkin.Tests;

public sealed class KeyboardLightTests
{
    // MSI Center's own packets to a Mystic Light keyboard (Alpha 15, 1462:1563), from the USB captures
    // attached to OpenRGB issue 2289: the report ID, then the 63 bytes of feature report 0x02.
    private const string SteadyRedFull = "02 00 01 00 0a 01 ff 00 00 ff 50 00 ff ff 00 00 ff 00 00 00 ff 96 00 ff ff 00 ff";
    private const string SteadyWhiteMin = "02 00 01 00 02 01 ff ff ff ff 50 00 ff ff 00 00 ff 00 00 00 ff 96 00 ff ff 00 ff";
    private const string OffAfterBlue = "02 00 00 00 0a 01 00 00 ff ff 50 00 ff ff 00 00 ff 00 00 00 ff 96 00 ff ff 00 ff";

    private static byte[] Captured(string hex)
    {
        var packet = new byte[MysticLight.ReportLength];
        Convert.FromHexString(hex.Replace(" ", "")).CopyTo(packet, 0);
        return packet;
    }

    [Fact]
    public void WattkinSendsMsiCentersPacketsByteForByte()
    {
        Assert.Equal(Captured(SteadyRedFull), MysticLight.Packet(3, new LightColor(0xFF, 0, 0)));
        Assert.Equal(Captured(SteadyWhiteMin), MysticLight.Packet(1, LightColor.White));
        Assert.Equal(Captured(OffAfterBlue), MysticLight.Packet(0, new LightColor(0, 0, 0xFF)));
    }

    [Fact]
    public void LowMidAndHighAreMsiCentersLowestMiddleAndFullBrightness()
    {
        Assert.Equal([2, 6, 10], new[] { 1, 2, 3 }.Select(level => MysticLight.Packet(level, LightColor.White)[4]));
        Assert.All(new[] { 1, 2, 3 }, level => Assert.Equal(1, MysticLight.Packet(level, LightColor.White)[2])); // steady
        Assert.Equal(MysticLight.Packet(3, LightColor.White), MysticLight.Packet(9, LightColor.White));            // out of range: clamped
        Assert.Equal(MysticLight.Packet(0, LightColor.White), MysticLight.Packet(-1, LightColor.White));
    }

    [Fact]
    public void ColoursAreReadAsHashRrggbbAndNothingElse()
    {
        Assert.Equal(new LightColor(0xFF, 0x50, 0x00), LightColor.Parse("#ff5000"));
        Assert.Equal(new LightColor(0x96, 0x00, 0xFF), LightColor.Parse(" 9600FF "));
        Assert.Equal("#9600FF", new LightColor(0x96, 0x00, 0xFF).Hex);
        Assert.Null(LightColor.Parse("#FFF"));
        Assert.Null(LightColor.Parse("#GG0000"));
        Assert.Null(LightColor.Parse("#-10000"));
        Assert.Null(LightColor.Parse(null));
    }

    [Fact]
    public void TheUsbControllerLightsTheKeysOnlyWhereTheControllerCantSayOtherwise()
    {
        static MysticLightInfo Light(string pid) => new($@"\\?\hid#vid_1462&{pid}&col02#7&abc#{{4d1e55b2}}", "MysticLight", 0x00FF, 64);

        Assert.True(Light("pid_1562").LightsTheKeys(ecHasKeyboardLight: true));   // the Stealth 15M: seen lighting the keys
        Assert.True(Light("pid_1563").LightsTheKeys(ecHasKeyboardLight: false));  // nothing else could
        Assert.False(Light("pid_1563").LightsTheKeys(ecHasKeyboardLight: true));  // maybe a light bar: the EC lights the keys
        Assert.False((Light("pid_1562") with { FeatureReportLength = 32 }).LightsTheKeys(false)); // unknown report layout
    }

    [Fact]
    public void ABrokenKeyboardColourInTheSettingsFileBecomesWhite()
    {
        var settings = new AppSettings { KeyboardColor = "red" };
        settings.Normalize();
        Assert.Equal("#FFFFFF", settings.KeyboardColor);

        settings.KeyboardColor = "#00ff7f";
        settings.Normalize();
        Assert.Equal("#00FF7F", settings.KeyboardColor);
    }
}
