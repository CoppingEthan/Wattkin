using Wattkin.Hardware;
using Xunit;

namespace Wattkin.Tests;

public class FanCurveTests
{
    private static byte[] Bytes(string hex) => [.. hex.Split('-').Select(h => Convert.ToByte(h, 16)), .. new byte[23]];

    // Get_Temperature(n) / Get_Fan(n) as the Prestige 16 Studio (EC 1594EMS1.113) returned them.
    private static readonly byte[] CpuTemps = Bytes("00-5F-64-3C-46-50-55-5A");
    private static readonly byte[] CpuSpeeds = Bytes("32-00-32-37-46-50-82-82");
    private static readonly byte[] GpuTemps = Bytes("00-57-5F-2D-3C-4B-50-55");
    private static readonly byte[] GpuSpeeds = Bytes("00-00-3C-50-82-82-82-82");

    [Fact]
    public void ReadsTheTablesInTheEcsOrder()
    {
        var cpu = FanCurve.Parse(CpuTemps, CpuSpeeds)!;
        Assert.Equal([60, 70, 80, 85, 90, 95], cpu.Temps);
        Assert.Equal([0, 50, 55, 70, 80, 130, 130], cpu.Speeds);
        Assert.True(cpu.Sane);
        Assert.Equal("off below 60°C · 50% from 60°C · 55% from 70°C · 70% from 80°C · 80% from 85°C · 130% from 90°C", cpu.Describe());

        var gpu = FanCurve.Parse(GpuTemps, GpuSpeeds)!;
        Assert.Equal([45, 60, 75, 80, 85, 87], gpu.Temps);
        Assert.True(gpu.Sane);
        Assert.Equal("off below 45°C · 60% from 45°C · 80% from 60°C · 130% from 75°C", gpu.Describe());
    }

    // The same calls on a Katana GF66 12UG (EC 1583EMS1.111), from a tester's diagnostics report: a table
    // that tops out at 75% (CPU) and 60% (GPU).
    private static FanCurves Katana() => new(
        FanCurve.Parse(Bytes("00-57-64-37-3E-45-4C-52"), Bytes("1E-1E-1E-23-25-2D-3C-4B"))!,
        FanCurve.Parse(Bytes("00-52-63-37-3D-43-48-4D"), Bytes("00-00-00-14-19-21-32-3C"))!);

    private static FanCurves Prestige() => new(FanCurve.Parse(CpuTemps, CpuSpeeds)!, FanCurve.Parse(GpuTemps, GpuSpeeds)!);

    // A Prestige 13 AI Evo A1MG (EC 13Q2EMS1.110), from a tester's diagnostics report: one fan, so
    // Get_Temperature(2) and Get_Fan(2) answer with zeros.
    private static FanCurves Prestige13() => new(
        FanCurve.Parse(Bytes("00-69-73-32-41-50-5F-64"), Bytes("26-26-28-32-46-46-46-46"))!,
        FanCurve.Parse(Bytes("00-00-00-00-00-00-00-00"), Bytes("00-00-00-00-00-00-00-00"))!);

    [Fact]
    public void ALaptopWithOneFanGetsFanCurvesForThatFan()
    {
        var laptop = Prestige13();
        Assert.Equal([50, 65, 80, 95, 100, 105], laptop.Cpu.Temps);
        Assert.Equal([38, 40, 50, 70, 70, 70, 70], laptop.Cpu.Speeds);
        Assert.True(laptop.Gpu.Absent);
        Assert.False(laptop.HasGpuFan);
        Assert.True(laptop.Sane); // 1.1.1 refused it: the missing GPU fan's all-zero table "wasn't safe"

        // The GPU's speeds are never written there, so they don't count; the CPU fan keeps its rules.
        var quiet = new FanSpeeds { Cpu = [0, 0, 30, 45, 56, 60], Gpu = [9, 9, 9, 9, 9, 9] };
        Assert.Null(quiet.Problem(unrestricted: false, laptop));
        Assert.NotNull(new FanSpeeds { Cpu = [0, 0, 30, 45, 50, 50], Gpu = quiet.Gpu }.Problem(unrestricted: false, laptop)); // under 56% (4/5 of 70) at the hottest editable step
        Assert.True(FanSpeeds.From(laptop).Fits(laptop));

        // And a two-fan laptop's GPU fan still counts.
        Assert.True(Prestige().HasGpuFan);
    }

    [Fact]
    public void MsisOwnLayoutIsRecognisedWithoutAKeyboardRegister()
    {
        // Until 1.1.1 the layout check also wanted the keyboard at 0xD3, which left msi-ec's families without
        // one (Stealth, Raider, Vector, Titan: CONF_G2_10, G2_2, G2_5) without fan curves.
        Assert.True(EmbeddedController.UsesMsiLayout(EcModels.Find("1563EMS1.111")));  // CONF_G2_10, no keyboard register
        Assert.True(EmbeddedController.UsesMsiLayout(EcModels.Find("1543EMS1.108")));  // CONF_G2_2
        Assert.True(EmbeddedController.UsesMsiLayout(EcModels.Find("1594EMS1.113")));  // the Prestige 16 Studio
        Assert.True(EmbeddedController.UsesMsiLayout(EcModels.MsiStandardWmi2));
        Assert.False(EmbeddedController.UsesMsiLayout(EcModels.Find("16R1EMS1.105"))); // older WMI v1 layout (0xF2 / 0xF4)
    }

    [Fact]
    public void TablesThatCouldOverheatAreRejected()
    {
        // Top step too slow to be a real fan table's.
        Assert.False(new FanCurve([60, 70, 80, 85, 90, 95], [0, 10, 20, 25, 30, 35, 40]).Sane);

        // Speeds dropping as it gets hotter.
        Assert.False(new FanCurve([60, 70, 80, 85, 90, 95], [0, 50, 55, 30, 80, 130, 130]).Sane);

        // An empty table (all zero), as a reset controller might hold.
        Assert.False(FanCurve.Parse(new byte[31], new byte[31])!.Sane);

        Assert.Null(FanCurve.Parse(null, CpuSpeeds));
    }

    [Fact]
    public void TheSafetyRulesKeepTheFansAheadOfTheHeat()
    {
        // The laptop's own curve passes.
        var factory = FanSpeeds.From(new FanCurves(FanCurve.Parse(CpuTemps, CpuSpeeds)!, FanCurve.Parse(GpuTemps, GpuSpeeds)!));
        Assert.Equal([0, 50, 55, 70, 80, 130], factory.Cpu);
        Assert.Null(factory.Problem(unrestricted: false));

        // Slower as it gets hotter: refused.
        var falling = new FanSpeeds { Cpu = [0, 50, 40, 70, 80, 130], Gpu = factory.Gpu };
        Assert.NotNull(falling.Problem(unrestricted: false));

        // Not at full speed by the hottest editable step: refused, unless the user accepted the experimental mode.
        var lazy = new FanSpeeds { Cpu = [0, 0, 20, 30, 40, 60], Gpu = factory.Gpu };
        Assert.NotNull(lazy.Problem(unrestricted: false));
        Assert.Null(lazy.Problem(unrestricted: true));

        // Out of range or the wrong length: refused even then.
        Assert.NotNull(new FanSpeeds { Cpu = [0, 0, 20, 30, 40, 200], Gpu = factory.Gpu }.Problem(unrestricted: true));
        Assert.NotNull(new FanSpeeds { Cpu = [0, 50], Gpu = factory.Gpu }.Problem(unrestricted: true));
        Assert.NotNull(new FanSpeeds().Problem(unrestricted: true));
    }

    [Fact]
    public void ThePresetsPassTheSafetyRules()
    {
        Assert.Null(FanSpeeds.Quiet().Problem(unrestricted: false));
        Assert.Null(FanSpeeds.Cool().Problem(unrestricted: false));
        Assert.True(FanSpeeds.Quiet().Fits(Prestige()));
        Assert.True(FanSpeeds.Cool().Fits(Prestige()));
    }

    [Fact]
    public void ALaptopWithASlowerTableKeepsItsOwnCurveAndGetsFittedPresets()
    {
        var katana = Katana();
        Assert.Equal([55, 62, 69, 76, 82, 87], katana.Cpu.Temps);
        Assert.Equal([30, 30, 35, 37, 45, 60, 75], katana.Cpu.Speeds);
        Assert.Equal([0, 0, 20, 25, 33, 50, 60], katana.Gpu.Speeds);
        Assert.True(katana.Sane);

        // The floor at the hottest editable step follows the laptop's top speed: unchanged on the Prestige.
        Assert.Equal(100, FanSpeeds.Floor(130));
        Assert.Equal(60, FanSpeeds.Floor(75));
        Assert.Equal(48, FanSpeeds.Floor(60));

        // Its own curve passes against its own table, but not against the strict default.
        var own = FanSpeeds.From(katana);
        Assert.Null(own.Problem(unrestricted: false, katana));
        Assert.NotNull(own.Problem(unrestricted: false));
        Assert.NotNull(new FanSpeeds { Cpu = [0, 0, 20, 30, 40, 55], Gpu = own.Gpu }.Problem(unrestricted: false, katana));

        // The Prestige presets are faster than its fixed top step; the fitted ones aren't.
        Assert.False(FanSpeeds.Quiet().Fits(katana));
        Assert.False(FanSpeeds.Cool().Fits(katana));
        var quiet = FanSpeeds.Quiet(katana);
        var cool = FanSpeeds.Cool(katana);
        Assert.Equal([0, 0, 25, 25, 30, 60], quiet.Cpu);
        Assert.Equal([0, 0, 15, 15, 20, 50], quiet.Gpu);
        Assert.Equal([45, 45, 50, 55, 60, 75], cool.Cpu);
        Assert.Equal([15, 15, 35, 40, 50, 60], cool.Gpu);
        Assert.True(quiet.Fits(katana));
        Assert.True(cool.Fits(katana));
    }

    [Fact]
    public void OnlyUneditedShippedFanProfilesAreRefitted()
    {
        var prestige = new Wattkin.Settings.AppSettings();
        Assert.Empty(prestige.FitDefaultFanProfiles(Prestige()));
        Assert.Equal(FanSpeeds.Quiet().Cpu, prestige.FindFanProfile("Quiet")!.Speeds.Cpu);

        var katana = new Wattkin.Settings.AppSettings();
        katana.FanProfiles = [new() { Name = "Quiet", Speeds = new FanSpeeds { Cpu = [0, 0, 30, 30, 40, 60], Gpu = [0, 0, 20, 25, 33, 50] } }, .. katana.FanProfiles.Where(f => f.Name != "Quiet")];
        Assert.Equal(["Cool"], katana.FitDefaultFanProfiles(Katana()));
        Assert.Equal([0, 0, 30, 30, 40, 60], katana.FindFanProfile("Quiet")!.Speeds.Cpu); // edited: kept
        Assert.Equal(FanSpeeds.Cool(Katana()).Gpu, katana.FindFanProfile("Cool")!.Speeds.Gpu);
    }

    [Fact]
    public void TheControllerChecksAcceptTheStatesRealLaptopsReport()
    {
        // Battery limit: on, 10–100%; or 0x80 (on, nothing set), how the controller comes back after a battery swap.
        Assert.True(EmbeddedController.ChargeRegisterLooksRight(0x80));
        Assert.True(EmbeddedController.ChargeRegisterLooksRight(0xBC)); // 60%
        Assert.True(EmbeddedController.ChargeRegisterLooksRight(0xE4)); // 100%
        Assert.False(EmbeddedController.ChargeRegisterLooksRight(0x50)); // flag missing
        Assert.False(EmbeddedController.ChargeRegisterLooksRight(0x85)); // 5%
        Assert.False(EmbeddedController.ChargeRegisterLooksRight(0xFF));

        // Performance mode: MSI's Sport (0xC0), as the Katana GF66 12UG reports, on MSI's own layout only.
        var msiLayout = EcModels.Find("1583EMS1.111")!;
        Assert.Equal("msi-ec CONF_G2_1", msiLayout.Source);
        Assert.True(EmbeddedController.ShiftRegisterLooksRight(msiLayout, 0xC0));
        Assert.True(EmbeddedController.ShiftRegisterLooksRight(msiLayout, 0xC1));
        Assert.True(EmbeddedController.ShiftRegisterLooksRight(msiLayout, 0x80));
        Assert.False(EmbeddedController.ShiftRegisterLooksRight(msiLayout, 0x00));
        Assert.False(EmbeddedController.ShiftRegisterLooksRight(msiLayout, 0xC3));
        var olderLayout = EcModels.Find("158LEMS1.105")!; // CONF_G1_2: no sport mode
        Assert.False(EmbeddedController.ShiftRegisterLooksRight(olderLayout, 0xC0));

        // CONF_G2_10, where msi-ec says turbo is "sometimes 0xc0": 0xC0 isn't taken as Sport (read-only, as in 1.1.1).
        var g210 = EcModels.Find("1822EMS1.111")!;
        Assert.Equal("msi-ec CONF_G2_10", g210.Source);
        Assert.False(EmbeddedController.ShiftRegisterLooksRight(g210, 0xC0));
        Assert.True(EmbeddedController.ShiftRegisterLooksRight(g210, 0xC4));
        Assert.True(EmbeddedController.ShiftRegisterLooksRight(EcModels.Find("1563EMS1.115")!, 0xC0)); // the Stealth 15M's own map names it
    }

    [Fact]
    public void WattkinsInterfaceDescriptionUsesMsisFirmwareIds()
    {
        // The class names are Wattkin's own; the GUIDs are the firmware's, so the calls reach the same BIOS methods.
        Assert.Equal("Wattkin_MsiAcpi", WmiSchema.AcpiClass);
        Assert.Equal("Wattkin_MsiEvent", WmiSchema.EventClass);
        var mof = typeof(WmiSchema).GetField("Mof", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null) as string;
        Assert.Contains("{ABBC0F6E-8EA1-11d1-00A0-C90629100000}", mof);
        Assert.Contains("{5B3CC38A-40D9-7245-8AE6-1145B751BE3F}", mof);
        Assert.Contains("[WmiMethodId(18), Implemented, read, write] void Set_Fan", mof);
        Assert.DoesNotContain("Set_BIOS", mof); // only the calls Wattkin makes
    }
}
