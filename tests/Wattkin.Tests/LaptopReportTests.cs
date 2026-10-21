using Wattkin.Features;
using Wattkin.Hardware;
using Xunit;

namespace Wattkin.Tests;

public sealed class LaptopReportTests
{
    private static HardwareState Prestige13() => HardwareState.Initial("") with
    {
        Available = true,
        Problem = null,
        EcFirmware = "13Q2EMS1.110",
        WmiVersion = "2.8",
        CanWrite = true,
        EcChargeLimit = 85,
        Caps = new EcCapabilities(EcSupport.Community, "msi-ec CONF_G2_3", CanSimulateUnplug: true, CanSwitchGpuMode: false,
            HasCoolerBoost: true, HasKeyboardLight: true, HasKeyboardTimeout: true, KeyboardMaxLevel: 3, HasFanCurves: true),
    };

    [Fact]
    public void TheSummarySaysWhatWattkinCanControlInPlainWords()
    {
        var summary = LaptopReport.Summary(Prestige13(), gpuFan: false, backlight: true);

        Assert.Contains("- Support: msi-ec register map (msi-ec CONF_G2_3), passed Wattkin's safety check", summary);
        Assert.Contains("- Firmware: EC 13Q2EMS1.110, interface 2.8", summary);
        Assert.Contains("- Charge limit: yes (now 85%)", summary);
        Assert.Contains("- Fans: curves (one fan); Cooler Boost yes", summary);
        Assert.Contains("- Keyboard light: levels 0-3, auto-off", summary);
        Assert.Contains("- GPU switch: no", summary);
        Assert.Contains("- Screen brightness: backlight and extra dim", summary);
    }

    [Fact]
    public void ALaptopWattkinOnlyMonitorsSaysWhy()
    {
        var summary = LaptopReport.Summary(HardwareState.Initial("No register map for this firmware."), gpuFan: null, backlight: false);

        Assert.Contains("- Support: monitoring only: No register map for this firmware.", summary);
        Assert.Contains("- Controls: off (monitoring only)", summary);
        Assert.DoesNotContain("Charge limit", summary);
    }

    [Fact]
    public void GitHubsFormOpensFilledIn()
    {
        var url = LaptopReport.IssueUrl("MSI Prestige 13 AI Evo A1MG", "1.2.0", "Wattkin's own check", msiRunning: true);

        Assert.StartsWith("https://github.com/CoppingEthan/Wattkin/issues/new?template=laptop-report.yml&", url);
        Assert.Contains("&title=Laptop%20report%3A%20MSI%20Prestige%2013%20AI%20Evo%20A1MG", url);
        Assert.Contains("&model=MSI%20Prestige%2013%20AI%20Evo%20A1MG", url);
        Assert.Contains("&version=1.2.0", url);
        Assert.Contains("&what-happened=Wattkin%27s%20own%20check", url);
        Assert.Contains("&msi-center=Still%20installed", url); // a dropdown is filled in by its option's text
        Assert.DoesNotContain("msi-center", LaptopReport.IssueUrl("MSI X", "1.2.0", "s", msiRunning: false));
    }
}
