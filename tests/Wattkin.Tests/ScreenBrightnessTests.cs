using Wattkin.Features;
using Wattkin.Platform;
using Xunit;

namespace Wattkin.Tests;

public sealed class ScreenBrightnessTests
{
    [Fact]
    public void OneRangeCoversTheBacklightAndTheExtraDimBelowIt()
    {
        Assert.Equal(40, ScreenBrightness.ToPosition(backlight: 40, extraDim: 100));
        Assert.Equal(-30, ScreenBrightness.ToPosition(backlight: 0, extraDim: 70));
        Assert.Equal(-30, ScreenBrightness.ToPosition(backlight: 55, extraDim: 70)); // extra dim on: that's where it is
        Assert.Equal(0, ScreenBrightness.ToPosition(backlight: null, extraDim: 100)); // no backlight Windows controls

        Assert.Equal(((int?)40, 100), ScreenBrightness.FromPosition(40, hasBacklight: true));
        Assert.Equal(((int?)0, 70), ScreenBrightness.FromPosition(-30, hasBacklight: true));       // the backlight at its lowest first
        Assert.Equal(((int?)null, 70), ScreenBrightness.FromPosition(-30, hasBacklight: false));   // only the picture to dim
        Assert.Equal(((int?)0, ScreenBrightness.MinExtraDim), ScreenBrightness.FromPosition(-500, hasBacklight: true)); // never black
    }

    [Fact]
    public void TheKeysStepTenAtATimeThroughBothRanges()
    {
        int Down(int p, bool backlight = true) => ScreenBrightness.StepFrom(p, down: true, backlight);
        int Up(int p, bool backlight = true) => ScreenBrightness.StepFrom(p, down: false, backlight);

        Assert.Equal(30, Down(37));   // to the next ten down
        Assert.Equal(20, Down(30));
        Assert.Equal(0, Down(5));
        Assert.Equal(-10, Down(0));   // past the backlight's lowest: extra dim
        Assert.Equal(-90, Down(-90)); // the darkest it goes (10% of the picture)
        Assert.Equal(-80, Up(-90));
        Assert.Equal(0, Up(-10));     // extra dim off, backlight at its lowest
        Assert.Equal(10, Up(0));
        Assert.Equal(40, Up(37));
        Assert.Equal(100, Up(100));

        // From a slider position between tens, below 0 too: the next ten, never past 0 to a dim backlight.
        Assert.Equal(-30, Down(-25));
        Assert.Equal(-20, Up(-25));
        Assert.Equal(0, Up(-5));
        Assert.Equal(-10, Down(-5));

        // No backlight (a desktop monitor): only the extra dim.
        Assert.Equal(0, Up(0, backlight: false));
        Assert.Equal(-10, Down(0, backlight: false));
    }

    [Fact]
    public void ItSaysWhereTheScreenIs()
    {
        Assert.Equal("Brightness 40%", ScreenBrightness.Describe(40));
        Assert.Equal("Extra dim 70%", ScreenBrightness.Describe(-30));
        Assert.Equal("Extra dim off", ScreenBrightness.Describe(0, hasBacklight: false));

        // The slider's label: a monitor with no backlight Windows controls is "Normal" at 0, not "0%".
        Assert.Equal("40%", ScreenBrightness.SliderText(40, hasBacklight: true));
        Assert.Equal("Extra dim 70%", ScreenBrightness.SliderText(-30, hasBacklight: true));
        Assert.Equal("Normal", ScreenBrightness.SliderText(0, hasBacklight: false));
    }

    [Fact]
    public void ADimIsKeptOnlyWhenTheOldCopyHandedItOverForAnUpdate()
    {
        var now = new DateTime(2026, 10, 21, 23, 0, 0, DateTimeKind.Utc);
        Assert.True(ScreenBrightness.KeepDimmedAtStart(60, now.AddSeconds(-20), now));   // restarting into an update: no flash
        Assert.False(ScreenBrightness.KeepDimmedAtStart(60, null, now));                 // exit, crash or power cut: back to normal
        Assert.False(ScreenBrightness.KeepDimmedAtStart(60, now.AddMinutes(-30), now));  // a handover long ago isn't this restart's
        Assert.False(ScreenBrightness.KeepDimmedAtStart(100, now.AddSeconds(-20), now)); // not dimmed
        Assert.False(ScreenBrightness.KeepDimmedAtStart(60, now.AddMinutes(1), now));    // a clock that went back
    }

    [Fact]
    public void TheDimScalesTheDisplaysOwnRamp()
    {
        var normal = GammaRamp.Linear();
        Assert.Equal(GammaRamp.Length, normal.Length);
        Assert.Equal(0, normal[0]);
        Assert.Equal(65535, normal[255]);       // red
        Assert.Equal(65535, normal[511]);       // green
        Assert.Equal(65535, normal[767]);       // blue
        Assert.Equal(128 * 257, normal[128]);
        Assert.True(GammaRamp.Plausible(normal));

        var half = GammaRamp.Scale(null, 50);
        Assert.Equal(32768, half[255]);
        Assert.True(GammaRamp.Plausible(half)); // still rising: no colour flips

        // A calibrated display (its own curve, a warmer blue end): the dim keeps its shape, so un-dimming
        // can put the calibration back instead of a plain straight ramp (found in review).
        var calibrated = GammaRamp.Linear();
        for (var i = 512; i < 768; i++)
        {
            calibrated[i] = (ushort)(calibrated[i] * 9 / 10);
        }

        var dimmed = GammaRamp.Scale(calibrated, 50);
        Assert.Equal(Math.Round(calibrated[767] * 0.5), dimmed[767]);
        Assert.Equal(Math.Round(calibrated[255] * 0.5), dimmed[255]);
        Assert.True(GammaRamp.Close(GammaRamp.Scale(calibrated, 100), calibrated));
    }

    [Fact]
    public void SavedRampsMustLookLikeARealDisplaysRamp()
    {
        var calibrated = GammaRamp.Scale(null, 95);
        Assert.Equal(calibrated, GammaRamp.Decode(GammaRamp.Encode(calibrated)));

        var falling = GammaRamp.Linear();
        falling[300] = 0; // green drops in the middle: not a ramp any display had
        Assert.False(GammaRamp.Plausible(falling));
        Assert.Null(GammaRamp.Decode(GammaRamp.Encode(falling)));
        Assert.Null(GammaRamp.Decode("not base64"));
        Assert.Null(GammaRamp.Decode(Convert.ToBase64String(new byte[10])));

        // settings.json drops a damaged one, keeps a good one.
        var settings = new Wattkin.Settings.AppSettings
        {
            ScreenBaseRamps = new() { [@"\\.\DISPLAY1"] = GammaRamp.Encode(calibrated), [@"\\.\DISPLAY2"] = "garbage" },
        };
        settings.Normalize();
        Assert.Equal([@"\\.\DISPLAY1"], settings.ScreenBaseRamps!.Keys);
    }
}
