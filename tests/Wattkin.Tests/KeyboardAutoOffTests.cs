using Wattkin.Features;
using Wattkin.Settings;
using Wattkin.UI;
using Xunit;

namespace Wattkin.Tests;

public sealed class KeyboardAutoOffTests
{
    private static KeyboardAutoOff Enabled()
    {
        var autoOff = new KeyboardAutoOff();
        Assert.Equal(LightStep.None, autoOff.SetEnabled(true, 0));
        return autoOff;
    }

    [Fact]
    public void TheLightGoesOffAfterTenSecondsWithoutAKeyAndComesBackAtTheNextKey()
    {
        var autoOff = Enabled();
        Assert.Equal(TimeSpan.FromSeconds(10), autoOff.NextCheck(0, lit: true));
        Assert.Equal(LightStep.None, autoOff.Check(9_999, lit: true));        // a timer that wakes a little early
        Assert.Equal(TimeSpan.FromMilliseconds(1), autoOff.NextCheck(9_999, lit: true));
        Assert.Equal(LightStep.Off, autoOff.Check(10_000, lit: true));
        Assert.True(autoOff.Dimmed);
        Assert.Null(autoOff.NextCheck(10_500, lit: true));                    // nothing to wait for until a key
        Assert.Equal(LightStep.On, autoOff.KeyPressed(15_000));
        Assert.Equal(LightStep.None, autoOff.KeyPressed(15_100));             // already back
        Assert.Equal(TimeSpan.FromSeconds(9.9), autoOff.NextCheck(15_200, lit: true)); // counted from the last key
    }

    [Fact]
    public void TypingPushesTheDeadlineBack()
    {
        var autoOff = Enabled();
        Assert.Equal(LightStep.None, autoOff.KeyPressed(8_000));
        Assert.Equal(LightStep.None, autoOff.Check(10_000, lit: true));
        Assert.Equal(TimeSpan.FromSeconds(8), autoOff.NextCheck(10_000, lit: true));
        Assert.Equal(LightStep.Off, autoOff.Check(18_000, lit: true));
    }

    [Fact]
    public void NothingIsSwitchedOffWhenTheLightIsAlreadyOffOrAutoOffIsOff()
    {
        var autoOff = Enabled();
        Assert.Equal(LightStep.None, autoOff.Check(60_000, lit: false));      // the user chose Off
        Assert.Null(autoOff.NextCheck(60_000, lit: false));

        var never = new KeyboardAutoOff();
        Assert.Equal(LightStep.None, never.Check(60_000, lit: true));
        Assert.Null(never.NextCheck(60_000, lit: true));
    }

    [Fact]
    public void TurningAutoOffOffPutsTheLightBack()
    {
        var autoOff = Enabled();
        Assert.Equal(LightStep.Off, autoOff.Check(10_000, lit: true));
        Assert.Equal(LightStep.On, autoOff.SetEnabled(false, 12_000));
        Assert.False(autoOff.Dimmed);
        Assert.Equal(LightStep.None, autoOff.Check(60_000, lit: true));
        Assert.Equal(LightStep.None, autoOff.SetEnabled(false, 61_000));      // no change, nothing to do
    }

    [Fact]
    public void WhileWindowsIsLockedTheLightStaysOnForThePassword()
    {
        var autoOff = Enabled();
        Assert.Equal(LightStep.Off, autoOff.Check(10_000, lit: true));
        Assert.Equal(LightStep.On, autoOff.SetLocked(true, 11_000));
        Assert.Equal(LightStep.None, autoOff.Check(600_000, lit: true));
        Assert.Null(autoOff.NextCheck(600_000, lit: true));
        Assert.Equal(LightStep.None, autoOff.SetLocked(false, 600_000));
        Assert.Equal(TimeSpan.FromSeconds(10), autoOff.NextCheck(600_000, lit: true));
        Assert.Equal(LightStep.Off, autoOff.Check(610_000, lit: true));
    }

    [Fact]
    public void TheDelayIsTheUsersAndAChangeCountsFromTheLastKey()
    {
        var autoOff = Enabled();
        autoOff.Delay = TimeSpan.FromSeconds(30);
        Assert.Equal(LightStep.None, autoOff.Check(10_000, lit: true));
        Assert.Equal(TimeSpan.FromSeconds(20), autoOff.NextCheck(10_000, lit: true));
        Assert.Equal(LightStep.Off, autoOff.Check(30_000, lit: true));

        Assert.Equal(LightStep.On, autoOff.KeyPressed(40_000));
        autoOff.Delay = TimeSpan.FromSeconds(5);                              // shortened after the last key
        Assert.Equal(TimeSpan.FromSeconds(2), autoOff.NextCheck(43_000, lit: true));
        Assert.Equal(LightStep.Off, autoOff.Check(45_000, lit: true));
    }

    [Fact]
    public void TheDelayStaysBetweenFiveSecondsAndFiveMinutes()
    {
        foreach (var (written, kept) in new[] { (0, 5), (-30, 5), (7, 7), (300, 300), (3600, 300) })
        {
            var settings = new AppSettings { KeyboardAutoOffSeconds = written };
            settings.Normalize();
            Assert.Equal(kept, settings.KeyboardAutoOffSeconds);
        }

        Assert.Equal(AppSettings.KeyboardAutoOffMinSeconds, AutoOffDelayStepper.Choices[0]);
        Assert.Equal(AppSettings.KeyboardAutoOffMaxSeconds, AutoOffDelayStepper.Choices[^1]);
        Assert.Equal(Array.IndexOf(AutoOffDelayStepper.Choices, 45), AutoOffDelayStepper.Nearest(50));  // a hand-edited value shows as the nearest
        Assert.Equal(["5 s", "45 s", "1 min", "1 min 30 s", "5 min"], new[] { 5, 45, 60, 90, 300 }.Select(Fmt.Delay));
    }

    [Fact]
    public void SettingTheLightShowsItAndRestartsTheWait()
    {
        var autoOff = Enabled();
        Assert.Equal(LightStep.Off, autoOff.Check(10_000, lit: true));
        autoOff.Shown(12_000);                                                // the user picked a colour
        Assert.False(autoOff.Dimmed);
        Assert.Equal(LightStep.None, autoOff.Check(20_000, lit: true));
        Assert.Equal(LightStep.Off, autoOff.Check(22_000, lit: true));
    }
}
