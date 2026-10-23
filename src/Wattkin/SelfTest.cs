using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Xml;
using Wattkin.Features;
using Wattkin.Hardware;
using Wattkin.Monitoring;
using Wattkin.Platform;
using Wattkin.Settings;
using Wattkin.UI;

namespace Wattkin;

/// <summary>
/// Hardware self-test (Wattkin.exe --selftest read|write|power [file], or tools\selftest.ps1). Contributed
/// with the Stealth 15M A11UEK (EC 1563EMS1.115) support; "read" and "power" run on any MSI laptop
/// Wattkin controls, "write" needs one whose whole fan table it writes.
///
/// "read" never writes to the controller: firmware interface, register map and sanity check, sensors,
/// and the fan table (where the map names its registers, read two ways: registers, and MSI's own
/// Get_Fan / Get_Temperature calls, which must agree). Like any first start of Wattkin it registers
/// Wattkin's description of the firmware interface in WMI. A raw diagnostics report is saved next to it.
///
/// "write" changes one setting at a time, checks the controller took it (and, for the fans, that it
/// acts on it, hysteresis included), and puts it back. Every register it can touch is saved first and
/// written back at the end, whatever happened. It refuses to run next to Wattkin, YAMDCC, MSI Center or
/// ThrottleStop: two programs writing the controller or the power limits would spoil the results.
///
/// "power" measures what each of MSI's performance modes lets the processor draw: under full load, the
/// peak (about PL2) and the level it settles at (about PL1), from Windows' RAPL energy counter, as
/// reading the limits themselves would need a kernel driver. The power plan's turbo and maximum
/// processor state are lifted for the measurement and put back.
///
/// Exit code: 0 everything passed, 1 something failed, 2 it couldn't run.
/// </summary>
public static class SelfTest
{
    public const string ExpectedFirmware = "1563EMS1.115";

    private const byte CpuFanTarget = 0x71;
    private const byte AbortTempC = 90;
    private const int PowerAbortTempC = 98; // under full load the CPU gets hot by design; it throttles itself at 100°C

    // Settling at 90°C or more means the cooling, not the power limit, set the level: on the Stealth 15M
    // Balanced, Sport and Extreme all settled at 31–33 W with the CPU at 90–94°C (2026-10-21).
    private const int ThermalLimitC = 90;
    private static readonly string[] OtherFanTools = ["yamdccsvc"];

    // It changes the processor's power limits directly, which would spoil the power measurements.
    private static readonly string[] PowerLimitTools = ["ThrottleStop"];

    public static int Run(string? stage, string? path)
    {
        var name = stage?.ToLowerInvariant() switch
        {
            "write" => "write",
            "power" => "power",
            _ => "read",
        };
        path = string.IsNullOrEmpty(path)
            ? Path.Combine(DataFolders.Local, $"selftest-{name}.txt")
            : path;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        var report = new Report();
        int code;
        try
        {
            code = name switch
            {
                "write" => RunWrite(report),
                "power" => RunPower(report),
                _ => RunRead(report),
            };
        }
        catch (Exception ex)
        {
            report.Fail("Self-test", $"stopped by an unexpected error: {ex}");
            code = 1;
        }

        report.Line("");
        report.Line($"Result: {report.Summary}");
        File.WriteAllText(path, report.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Log.Info($"Self-test ({name}) finished: {report.Summary}. Report: {path}");

        if (name == "read" && code != 2)
        {
            // The raw picture as well: every register, read only.
            Diagnostics.Run(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, Path.GetFileNameWithoutExtension(path) + "-diagnostics.txt"));
        }

        return code;
    }

    // ---- read ---------------------------------------------------------------------------------------

    private static int RunRead(Report r)
    {
        if (!Preconditions(r, "read"))
        {
            return 2;
        }

        using var acpi = Open(r);
        if (acpi is null)
        {
            return 2;
        }

        var ec = new EmbeddedController(acpi);
        Capabilities(r, acpi, ec);

        r.Section("Registers (read only)");
        foreach (var (address, meaning) in new (byte, string)[]
        {
            (0xD7, "battery limit"), (0xD2, "performance mode"), (0xD4, "fan mode"), (0x98, "Cooler Boost (bit 7)"),
            (0xD3, "keyboard backlight (not wired to the keys when a Mystic Light controller lights them)"), (0x2C, "keyboard auto-off (bit 3), mic-mute light (bit 1)"), (0x2D, "mute light (bit 1)"),
            (0x2E, "webcam (bit 1)"), (0x2F, "capabilities"), (0xBF, "run from battery (bit 0)"), (0xD9, "host flags, Fn lock (bit 1)"),
            (0x68, "CPU temperature"), (0x71, "CPU fan target"), (0x80, "GPU temperature"), (0x89, "GPU fan target"),
        })
        {
            r.Info($"0x{address:X2}", $"{Hex(ec.ReadRegister(address))}  {meaning}");
        }

        r.Section("Decoded");
        r.Check(ec.GetChargeLimit() is not null, "Battery limit", $"{ec.GetChargeLimit()}%");
        r.Check(ec.GetShiftMode() is not null, "Performance mode", $"{ec.GetShiftMode()}");
        r.Check(ec.GetFanMode() is not null, "Fan mode", $"{ec.GetFanMode()}");
        r.Check(ec.GetCoolerBoost() is not null, "Cooler Boost", $"{ec.GetCoolerBoost()}");
        if (ec.HasKeyboardLight)
        {
            r.Check(ec.GetKeyboardLevel() is not null, "Keyboard backlight", $"level {ec.GetKeyboardLevel()}");
        }
        else
        {
            r.Info("Keyboard backlight", "no register for it in this map");
        }

        if (ec.HasKeyboardTimeout)
        {
            r.Check(ec.GetKeyboardAutoOff() is not null, "Keyboard auto-off", $"{ec.GetKeyboardAutoOff()}");
        }
        else
        {
            r.Info("Keyboard auto-off", "no register for it in this map");
        }
        r.Info("Webcam", ec.WebcamSupported ? $"{ec.GetWebcam()}" : "switch not offered by the firmware");
        r.Info("Fn lock", $"{ec.GetFnLock()}");

        r.Section("Sensors (5 s)");
        var samples = new List<EcSensors>();
        for (var i = 0; i < 5; i++)
        {
            if (ec.ReadSensors() is { } s)
            {
                samples.Add(s);
            }

            Thread.Sleep(1000);
        }

        r.Info("Readings", string.Join(" · ", samples.Select(s => $"CPU {s.CpuTempC}°C GPU {s.GpuTempC}°C fans {s.Fan1Rpm}/{s.Fan2Rpm} rpm")));
        r.Check(samples.Count > 0 && samples.All(s => s.CpuTempC is >= 20 and <= 100), "CPU temperature plausible");
        r.Check(samples.Count > 0 && samples.All(s => s.Fan1Rpm is not null && s.Fan2Rpm is not null), "Fan speeds reported");
        var register = ec.ReadRegister(0x68);
        var call = samples.LastOrDefault()?.CpuTempC;
        r.Check(register is { } reg && call is { } c && Math.Abs(reg - c) <= 3, "CPU temperature: MSI's call and register 0x68 agree", $"{call}°C / {register}°C");

        r.Section("Fan table");
        if (!ec.HasFullFanTable)
        {
            // Speeds only, through MSI's calls (most laptops): what they report.
            if (ec.ReadFanCurves() is { } curves)
            {
                r.Info("CPU fan", curves.Cpu.Describe());
                r.Info("GPU fan", curves.HasGpuFan ? curves.Gpu.Describe() : "none (this laptop has one fan)");
                r.Check(curves.Sane, "The table looks safe to use", curves.Sane ? "" : "Wattkin won't use the fan curve mode with it");
            }
            else
            {
                r.Info("Fan table", "MSI's calls didn't return one");
            }

            ReadKeyboardLight(r);
            return r.Failed > 0 ? 1 : 0;
        }

        if (ec.ReadFullFanTable() is not { } table)
        {
            r.Fail("Whole fan table", "couldn't be read from the registers");
            return r.Failed > 0 ? 1 : 0;
        }

        r.Info("CPU fan", table.Cpu.Describe());
        r.Info("GPU fan", table.Gpu.Describe());
        r.Check(table.Problem(unrestricted: true) is null, "Table is well formed", table.Problem(unrestricted: true) ?? "");
        if (table.Problem(unrestricted: false) is { } unsafeTable)
        {
            r.Warn("Safety rules", $"the table in the controller now wouldn't pass them: {unsafeTable}");
        }

        CompareWithMsiCalls(r, acpi, table, "now");

        if (YamdccTable(out var names) is { } yamdcc)
        {
            r.Info("YAMDCC", yamdcc.SameAs(table)
                ? $"the controller holds the table YAMDCC writes ({names}): Wattkin will keep it as \"Laptop default\"."
                : $"the controller's table differs from YAMDCC's ({names}): it looks like the firmware's own.");
        }

        ReadKeyboardLight(r);
        return r.Failed > 0 ? 1 : 0;
    }

    /// <summary>Read only: the USB controller that lights the keys, and whether it declares the reports Wattkin sends.</summary>
    private static void ReadKeyboardLight(Report r)
    {
        r.Section("Keyboard light (USB, read only)");
        var info = MysticLight.Find();
        if (info is null)
        {
            r.Info("Mystic Light controller", "none: the keys (if lit) are the controller's, through its keyboard register");
            return;
        }

        r.Info("Mystic Light controller", $"{info.Product} · {info.Path}");
        r.Check(info.Usable, "Declares the reports Wattkin sends", $"usage page 0x{info.UsagePage:X4}, feature report {info.FeatureReportLength} bytes (expected 0x{MysticLight.UsagePage:X4}, {MysticLight.ReportLength})");
    }

    /// <summary>The table read from the registers must be what MSI's own calls report (see <see cref="FanCurve.Parse"/>).</summary>
    private static void CompareWithMsiCalls(Report r, MsiAcpi acpi, FanTable table, string when)
    {
        foreach (var (name, selector, steps) in new[] { ("CPU", (byte)1, table.Cpu), ("GPU", (byte)2, table.Gpu) })
        {
            var viaCalls = FanCurve.Parse(acpi.Get("Get_Temperature", selector), acpi.Get("Get_Fan", selector));
            r.Check(viaCalls is not null && viaCalls.Speeds.SequenceEqual(steps.Speeds) && viaCalls.Temps.SequenceEqual(steps.Up),
                $"{name} fan: registers and Get_Fan/Get_Temperature({selector}) agree ({when})",
                viaCalls is null ? "the calls failed" : $"calls: {string.Join("/", viaCalls.Speeds)} from {string.Join("/", viaCalls.Temps)}°C");
        }
    }

    // ---- write --------------------------------------------------------------------------------------

    private static int RunWrite(Report r)
    {
        if (!Preconditions(r, "write"))
        {
            return 2;
        }

        using var acpi = Open(r);
        if (acpi is null)
        {
            return 2;
        }

        var ec = new EmbeddedController(acpi);
        Capabilities(r, acpi, ec);
        if (!ec.CanWrite || !ec.HasFullFanTable || ec.FanTableMap is not { } map)
        {
            r.Fail("Write tests", "need a laptop whose controller Wattkin writes whole fan tables (the Stealth 15M A11UEK, EC 1563EMS1.115). Nothing was changed.");
            return 2;
        }

        // Everything any test can touch, saved before the first write.
        byte[] watched = [0xBF, 0x98, .. map.Cpu.Addresses, .. map.Gpu.Addresses, 0xD4, 0xD2, 0xD7, 0xD3, 0x2C, 0x2D, 0x2E];
        var saved = new Dictionary<byte, byte>();
        foreach (var address in watched)
        {
            if (acpi.ReadEc(address) is not { } value)
            {
                r.Fail("Saving the current settings", $"register 0x{address:X2} couldn't be read. Nothing was changed.");
                return 2;
            }

            saved[address] = value;
        }

        r.Info("Saved", string.Join(" ", saved.Select(kv => $"{kv.Key:X2}={kv.Value:X2}")));
        try
        {
            Step(r, ec, "Battery limit", () => TestChargeLimit(r, ec));
            Put(acpi, saved, 0xD7);
            Step(r, ec, "Performance modes", () => TestShiftModes(r, ec));
            Put(acpi, saved, 0xD2);
            Step(r, ec, "Fan modes", () => TestFanModes(r, ec));
            Put(acpi, saved, 0xD4);
            Step(r, ec, "Keyboard backlight", () => TestKeyboard(r, ec));
            Put(acpi, saved, 0xD3, 0x2C);
            Step(r, ec, "Mute lights", () => TestLeds(r, ec));
            Put(acpi, saved, 0x2C, 0x2D);
            Step(r, ec, "Webcam", () => TestWebcam(r, ec));
            Put(acpi, saved, 0x2E);
            Step(r, ec, "Cooler Boost", () => TestCoolerBoost(r, ec));
            Put(acpi, saved, 0x98);
            Step(r, ec, "Run from battery while plugged in", () => TestSimulatedUnplug(r, ec));
            Put(acpi, saved, 0xBF);
            Step(r, ec, "Fan table", () => TestFanTable(r, acpi, ec));
            Step(r, ec, "The controller follows the table", () => TestFollowsTable(r, ec));
        }
        finally
        {
            Restore(r, acpi, saved);
        }

        return r.Failed > 0 ? 1 : 0;
    }

    private static void TestChargeLimit(Report r, EmbeddedController ec)
    {
        var before = ec.GetChargeLimit();
        var target = before == 80 ? 75 : 80;
        var ok = ec.SetChargeLimit(target) && ec.GetChargeLimitRaw() == EmbeddedController.EncodeChargeLimit(target);
        r.Check(ok, "Battery limit written and read back", $"{before}% → {target}% (read {ec.GetChargeLimit()}%)");
    }

    private static void TestShiftModes(Report r, EmbeddedController ec)
    {
        foreach (var mode in ec.HasSportMode ? new[] { ShiftMode.Eco, ShiftMode.Balanced, ShiftMode.Sport, ShiftMode.Turbo } : [ShiftMode.Eco, ShiftMode.Balanced, ShiftMode.Turbo])
        {
            var ok = ec.SetShiftMode(mode);
            Thread.Sleep(500);
            r.Check(ok && ec.GetShiftMode() == mode, $"Performance mode {mode}", $"read {ec.GetShiftMode()} ({Hex(ec.ReadRegister(0xD2))})");
        }
    }

    private static void TestFanModes(Report r, EmbeddedController ec)
    {
        foreach (var mode in new[] { FanMode.Silent, FanMode.Auto })
        {
            var ok = ec.SetFanMode(mode);
            Thread.Sleep(500);
            r.Check(ok && ec.GetFanMode() == mode, $"Fan mode {mode}", $"read {ec.GetFanMode()} ({Hex(ec.ReadRegister(0xD4))})");
        }
    }

    private static void TestKeyboard(Report r, EmbeddedController ec)
    {
        if (MysticLight.Find()?.LightsTheKeys(ec.HasKeyboardLight) == true) // the app's own rule
        {
            TestUsbKeyboardLight(r); // register 0xD3 isn't wired to these keys: Wattkin doesn't write it here
        }
        else if (!ec.HasKeyboardLight)
        {
            r.Skip("Keyboard backlight levels", "this map has no keyboard light register");
        }
        else
        {
            var levels = new List<string>();
            var ok = true;
            for (var level = 0; level <= ec.KeyboardMaxLevel; level++)
            {
                ok &= ec.SetKeyboardLevel(level);
                Thread.Sleep(700);
                var read = ec.GetKeyboardLevel();
                ok &= read == level;
                levels.Add($"{level}→{read}");
            }

            r.Check(ok, "Keyboard backlight levels (the keyboard went dark, then up to full)", string.Join(" ", levels));
        }

        if (!ec.HasKeyboardTimeout)
        {
            r.Skip("Keyboard auto-off switched", "this map has no auto-off register (where the keys are lit over USB, auto-off is Wattkin's own)");
            return;
        }

        var autoOff = ec.GetKeyboardAutoOff();
        var toggled = autoOff is { } a && ec.SetKeyboardAutoOff(!a) && ec.GetKeyboardAutoOff() == !a;
        r.Check(toggled, "Keyboard auto-off switched", $"{autoOff} → {ec.GetKeyboardAutoOff()} ({Hex(ec.ReadRegister(0x2C))})");
    }

    /// <summary>
    /// The Mystic Light controller takes each packet (it can't be read back, so the eye is the check),
    /// then goes back to Wattkin's own setting, or to full white when Wattkin hasn't set one yet.
    /// </summary>
    private static void TestUsbKeyboardLight(Report r)
    {
        using var light = MysticLight.Open(out var error);
        if (light is null)
        {
            r.Fail("Keyboard light opened for writing", error ?? "the controller went away");
            return;
        }

        var white = LightColor.White;
        var steps = new List<string>();
        var ok = true;
        foreach (var (name, level, color) in new (string, int, LightColor)[]
        {
            ("off", 0, white), ("low", 1, white), ("mid", 2, white), ("high", 3, white),
            ("red", 3, new(0xFF, 0, 0)), ("green", 3, new(0, 0xFF, 0)), ("blue", 3, new(0, 0, 0xFF)),
        })
        {
            var sent = light.Set(level, color);
            ok &= sent;
            steps.Add(sent ? name : $"{name} refused");
            Thread.Sleep(1000);
        }

        r.Check(ok, "Keyboard light: off, white low, mid and high, then red, green, blue (watch the keys)", string.Join(" · ", steps));

        var settings = new SettingsStore(readOnly: true).Current;
        var (backLevel, backColor) = settings.KeyboardLevel is { } saved ? (saved, LightColor.Parse(settings.KeyboardColor) ?? white) : (MysticLight.MaxLevel, white);
        r.Check(light.Set(backLevel, backColor), "Keyboard light back",
            settings.KeyboardLevel is null ? "the controller can't say what it showed before: left on, full white" : $"Wattkin's setting: level {backLevel}, {backColor.Hex}");
    }

    private static void TestLeds(Report r, EmbeddedController ec)
    {
        foreach (var (name, address, set) in new (string, byte, Func<bool, bool>)[] { ("Mute light", 0x2D, ec.SetMuteLed), ("Mic-mute light", 0x2C, ec.SetMicMuteLed) })
        {
            var on = set(true) && ec.ReadRegister(address) is { } lit && (lit & 0x02) != 0;
            Thread.Sleep(700);
            var off = set(false) && ec.ReadRegister(address) is { } dark && (dark & 0x02) == 0;
            r.Check(on && off, $"{name} on, then off (watch the key's light)", $"0x{address:X2} = {Hex(ec.ReadRegister(address))}");
        }
    }

    private static void TestWebcam(Report r, EmbeddedController ec)
    {
        if (!ec.WebcamSupported || ec.GetWebcam() is not { } before)
        {
            r.Skip("Webcam switch", "not offered by the firmware");
            return;
        }

        var switched = ec.SetWebcam(!before) && ec.GetWebcam() == !before;
        Thread.Sleep(1500);
        var back = ec.SetWebcam(before) && ec.GetWebcam() == before;
        r.Check(switched && back, "Webcam switched and back", $"{before} → {!before} → {ec.GetWebcam()}");
    }

    private static void TestCoolerBoost(Report r, EmbeddedController ec)
    {
        var before = AverageRpm(ec, 3);
        var on = ec.SetCoolerBoost(true) && ec.GetCoolerBoost() == true;
        var peak = before;
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(25) && peak < Math.Max(before + 1500, 4500))
        {
            Thread.Sleep(1000);
            peak = Math.Max(peak, AverageRpm(ec, 1));
        }

        var off = ec.SetCoolerBoost(false) && ec.GetCoolerBoost() == false;
        r.Check(on && off, "Cooler Boost on, then off");
        r.Check(peak >= Math.Max(before + 1500, 4500), "Cooler Boost speeds the fans up (you heard them)", $"{before} → {peak} rpm (average of both fans)");
    }

    private static void TestSimulatedUnplug(Report r, EmbeddedController ec)
    {
        var power = System.Windows.Forms.SystemInformation.PowerStatus;
        if (power.PowerLineStatus != System.Windows.Forms.PowerLineStatus.Online)
        {
            r.Skip("Run from battery while plugged in", "the charger isn't connected");
            return;
        }

        if (power.BatteryLifePercent < 0.3f)
        {
            r.Skip("Run from battery while plugged in", "the battery is under 30%");
            return;
        }

        var set = ec.SetSimulatedUnplug(true);
        var onBattery = WaitFor(() => System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Offline, TimeSpan.FromSeconds(20));
        var cleared = ec.SetSimulatedUnplug(false) && ec.GetSimulatedUnplug() == false;
        var backOnAc = WaitFor(() => System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online, TimeSpan.FromSeconds(20));
        r.Check(set && onBattery, "Windows saw the laptop run from the battery with the charger connected");
        r.Check(cleared && backOnAc, "Back on the charger");
    }

    /// <summary>A table unlike any real one, written, read back from the registers and through MSI's calls.</summary>
    private static void TestFanTable(Report r, MsiAcpi acpi, EmbeddedController ec)
    {
        var test = new FanTable
        {
            Cpu = new FanSteps { Speeds = [0, 31, 46, 61, 76, 91, 111], Up = [51, 59, 67, 75, 83, 89], Down = [47, 54, 61, 68, 77, 85] },
            Gpu = new FanSteps { Speeds = [0, 36, 51, 66, 81, 96, 116], Up = [53, 61, 69, 77, 85, 90], Down = [49, 55, 62, 70, 79, 86] },
        };
        var written = ec.WriteFullFanTable(test);
        var back = ec.ReadFullFanTable();
        r.Check(written && back is not null && back.SameAs(test), "Whole table written and read back (drop-back offsets included)",
            back is null ? "couldn't read it back" : $"CPU {back.Cpu.Describe()}");
        CompareWithMsiCalls(r, acpi, test, "after writing");

        var unsafeTable = test.Clone();
        unsafeTable.Cpu.Speeds[^1] = 40;
        r.Check(!ec.WriteFullFanTable(unsafeTable) && ec.ReadFullFanTable()?.SameAs(test) == true, "A table that breaks the safety rules is refused, and nothing is written");
    }

    /// <summary>
    /// In curve mode the controller's CPU fan target (0x71) must follow the table: the base speed while
    /// the temperature is under the first step, a change of that speed, and the hysteresis: a step whose
    /// start moves above the temperature holds until the temperature falls under its drop-back point.
    /// The tables are built around today's CPU temperature; keep the laptop idle while this runs.
    /// </summary>
    private static void TestFollowsTable(Report r, EmbeddedController ec)
    {
        ec.SetCoolerBoost(false);
        var samples = Enumerable.Range(0, 3).Select(_ => { Thread.Sleep(300); return ec.ReadSensors()?.CpuTempC; }).Where(t => t is not null).Select(t => t!.Value).ToList();
        if (samples.Count == 0 || samples.Average() is < 31 or > 80)
        {
            r.Skip("Fan behaviour tests", $"need a CPU temperature between 31 and 80°C (now {(samples.Count == 0 ? "unknown" : $"{samples.Average():0}°C")})");
            return;
        }

        var t = (int)Math.Round(samples.Average());
        r.Info("Reference temperature", $"{t}°C (keep the laptop idle)");

        FanTable Table(int[] cpuUp, int[] cpuDown, int[] cpuSpeeds) => new()
        {
            Cpu = new FanSteps { Speeds = cpuSpeeds, Up = cpuUp, Down = cpuDown },
            Gpu = new FanSteps { Speeds = [40, 45, 50, 55, 60, 65, 80], Up = [t + 12, t + 14, t + 16, t + 18, t + 20, t + 22], Down = [t + 9, t + 11, t + 13, t + 15, t + 17, t + 19] },
        };

        int[] far = [t + 12, t + 14, t + 16, t + 18, t + 20, t + 22];
        int[] farDown = [.. far.Select(u => u - 3)];

        // Under the first step: the base speed.
        var ok = ec.SetFanMode(FanMode.Custom, unrestricted: true, table: Table(far, farDown, [45, 50, 55, 60, 65, 70, 80]));
        Expect(r, ec, ok, "Under the first step the CPU fan runs at the base speed", 45, TimeSpan.FromSeconds(25), t, below: far[0]);

        ok = ec.SetFanMode(FanMode.Custom, unrestricted: true, table: Table(far, farDown, [30, 50, 55, 60, 65, 70, 80]));
        Expect(r, ec, ok, "A new base speed is followed", 30, TimeSpan.FromSeconds(25), t, below: far[0]);

        // Hysteresis: in step 1, then step 1 starts above the temperature but drops back far below it.
        int[] up = [t - 8, .. far[1..]];
        int[] down = [t - 11, .. farDown[1..]];
        int[] speeds = [30, 60, 65, 70, 75, 80, 85];
        ok = ec.SetFanMode(FanMode.Custom, unrestricted: true, table: Table(up, down, speeds));
        Expect(r, ec, ok, "Over the first step the CPU fan steps up", 60, TimeSpan.FromSeconds(25), t, above: up[0]);

        // Changed while the controller keeps following the table: it must keep its current step.
        up[0] = t + 6;
        down[0] = t - 10;
        ok = ec.WriteFullFanTable(Table(up, down, speeds), unrestricted: true, inCurveMode: true);
        Hold(r, ec, ok, "Hysteresis: the step holds while the temperature stays above its drop-back point", 60, TimeSpan.FromSeconds(15), t, above: down[0], below: up[0]);

        down[0] = t + 3;
        ok = ec.WriteFullFanTable(Table(up, down, speeds), unrestricted: true, inCurveMode: true);
        Expect(r, ec, ok, "Under the drop-back point the CPU fan steps down", 30, TimeSpan.FromSeconds(25), t, below: down[0]);
    }

    /// <summary>Waits for the CPU fan target to reach <paramref name="expected"/>; a temperature outside the band makes the result inconclusive.</summary>
    private static void Expect(Report r, EmbeddedController ec, bool written, string name, int expected, TimeSpan timeout, int reference, int? above = null, int? below = null)
    {
        if (!written)
        {
            r.Fail(name, "the table wasn't accepted");
            return;
        }

        var (targets, temps) = Watch(ec, timeout, stopWhen: v => v is { } x && Math.Abs(x - expected) <= 1);
        var reached = targets.LastOrDefault() is { } last && Math.Abs(last - expected) <= 1;
        Judge(r, name, reached, expected, targets, temps, reference, above, below);
    }

    /// <summary>The CPU fan target must stay at <paramref name="expected"/> for the whole time.</summary>
    private static void Hold(Report r, EmbeddedController ec, bool written, string name, int expected, TimeSpan duration, int reference, int? above = null, int? below = null)
    {
        if (!written)
        {
            r.Fail(name, "the table wasn't accepted");
            return;
        }

        var (targets, temps) = Watch(ec, duration, stopWhen: _ => false);
        var held = targets.Count > 0 && targets.All(v => v is { } x && Math.Abs(x - expected) <= 1);
        Judge(r, name, held, expected, targets, temps, reference, above, below);
    }

    private static void Judge(Report r, string name, bool ok, int expected, List<int?> targets, List<int> temps, int reference, int? above, int? below)
    {
        var detail = $"expected {expected}%, target {string.Join(" ", targets.Select(v => v?.ToString(CultureInfo.InvariantCulture) ?? "?"))} · CPU {(temps.Count == 0 ? "?" : $"{temps.Min()}–{temps.Max()}")}°C (reference {reference}°C)";
        var inBand = temps.Count > 0 && (above is not { } a || temps.Min() >= a) && (below is not { } b || temps.Max() < b);
        if (ok)
        {
            r.Pass(name, detail);
        }
        else if (!inBand)
        {
            r.Warn(name, $"inconclusive: the temperature left the band the test needs. {detail}");
        }
        else
        {
            r.Fail(name, detail);
        }
    }

    private static (List<int?> Targets, List<int> Temps) Watch(EmbeddedController ec, TimeSpan duration, Func<int?, bool> stopWhen)
    {
        var targets = new List<int?>();
        var temps = new List<int>();
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < duration)
        {
            Thread.Sleep(1000);
            var temp = ec.ReadSensors()?.CpuTempC;
            if (temp is >= AbortTempC)
            {
                throw new InvalidOperationException($"CPU at {temp}°C: stopping the tests and putting everything back.");
            }

            if (temp is { } tc)
            {
                temps.Add(tc);
            }

            var target = ec.ReadRegister(CpuFanTarget) is { } v ? (int?)v : null;
            targets.Add(target);
            if (stopWhen(target))
            {
                break;
            }
        }

        return (targets, temps);
    }

    // ---- power limits -------------------------------------------------------------------------------

    private static int RunPower(Report r)
    {
        if (!Preconditions(r, "power"))
        {
            return 2;
        }

        if (System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus != System.Windows.Forms.PowerLineStatus.Online)
        {
            r.Fail("Charger connected", "the limits are measured on the charger; plug it in");
            return 2;
        }

        using var acpi = Open(r);
        if (acpi is null)
        {
            return 2;
        }

        var ec = new EmbeddedController(acpi);
        using var query = new PdhQuery();
        var energy = query.Add(@"\Energy Meter(*)\Power");
        if (!r.Check(ec.CanWrite, "Register map passed its sanity check") || !r.Check(energy is not null, "Processor energy counter (RAPL)") || WindowsPower.ReadProcessorPlan() is not { } plan)
        {
            r.Fail("Power limits", "can't measure here. Nothing was changed.");
            return 2;
        }

        var saved = new Dictionary<byte, byte>();
        foreach (var address in new byte[] { 0x98, 0xD4, 0xD2 })
        {
            if (acpi.ReadEc(address) is not { } value)
            {
                r.Fail("Saving the current settings", $"register 0x{address:X2} couldn't be read. Nothing was changed.");
                return 2;
            }

            saved[address] = value;
        }

        r.Info("Power plan", $"turbo {(plan.BoostAc == 0 ? "off" : "on")}, maximum state {plan.MaxAc}%, maximum frequency {(plan.Freq is { Ac: var mhz } ? mhz == 0 ? "no cap" : $"{mhz} MHz" : "not in the plan")}, EPP {(plan.Epp is { Ac: var epp } ? epp.ToString(CultureInfo.InvariantCulture) : "not in the plan")} on the charger: set to on, 100%, no cap and EPP 0 while measuring, then put back");
        r.Info("Windows power mode", $"{WindowsPower.GetMode(onAc: true)} on the charger (left as it is)");
        r.Info("Fans", "Auto while measuring (the firmware's own behaviour for each mode)");

        var results = new List<string>();
        try
        {
            WindowsPower.WriteProcessorPlan(WindowsPower.Unlimited(plan));
            ec.SetCoolerBoost(false);
            ec.SetFanMode(FanMode.Auto);
            List<ShiftMode> modes = [ShiftMode.Eco, ShiftMode.Balanced, .. ec.HasSportMode ? [ShiftMode.Sport] : Array.Empty<ShiftMode>(), ShiftMode.Turbo];
            foreach (var mode in modes)
            {
                r.Section($"Power limits: {Fmt.Shift(mode)}");
                if (!ec.SetShiftMode(mode) || ec.GetShiftMode() != mode)
                {
                    r.Fail($"{Fmt.Shift(mode)} mode", $"the controller didn't take it ({Hex(ec.ReadRegister(0xD2))})");
                    continue;
                }

                if (MeasureUnderLoad(ec, query, energy!) is not { } m)
                {
                    r.Fail($"{Fmt.Shift(mode)} mode", "too few readings from the energy counter");
                    continue;
                }

                r.Pass($"{Fmt.Shift(mode)} (0x{ec.ReadRegister(0xD2):X2})", m.Describe());
                if (m.HottestLate >= ThermalLimitC)
                {
                    r.Warn($"{Fmt.Shift(mode)}: limited by temperature", $"the CPU was at {m.HottestLate}°C while settling, so the cooling set this level: the mode's PL1 may be higher");
                }

                results.Add($"{Fmt.Shift(mode),-9} PL2 ≈ {m.Peak,3:0} W   PL1 ≈ {m.Sustained,3:0} W   boost ≈ {m.BoostSeconds,2} s");
            }
        }
        finally
        {
            r.Section("Putting everything back");
            WindowsPower.WriteProcessorPlan(plan);
            PutBack(acpi, saved);
            var back = WindowsPower.ReadProcessorPlan();
            var wrong = saved.Where(kv => acpi.ReadEc(kv.Key) != kv.Value).Select(kv => $"0x{kv.Key:X2}").ToList();
            r.Check(wrong.Count == 0 && back == plan, "Performance mode, fans and power plan back as they were", string.Join(" ", wrong) + (back == plan ? "" : " power plan differs"));
        }

        if (results.Count > 0)
        {
            r.Section("Summary (on the charger)");
            foreach (var line in results)
            {
                r.Line(line);
            }
        }

        return r.Failed > 0 ? 1 : 0;
    }

    /// <summary>One run under full load: the peak (about PL2), the level it settles at (about PL1), how long the boost lasted.</summary>
    private sealed record LoadResult(double Peak, double Sustained, int BoostSeconds, int? MaxTempC, int HottestLate, double IdleBefore)
    {
        public string Describe() =>
            $"peak {Peak:0} W (about PL2) · settles at {Sustained:0} W (about PL1) · boost about {BoostSeconds} s · CPU up to {(MaxTempC is { } t ? t.ToString(CultureInfo.InvariantCulture) : "?")}°C · {IdleBefore:0.0} W idle before";
    }

    /// <summary>A cool-down, then 80 s of full load; null when the energy counter gave too few readings.</summary>
    private static LoadResult? MeasureUnderLoad(EmbeddedController ec, PdhQuery query, PdhCounter energy)
    {
        var idle = CoolDown(ec, query, energy);
        var samples = Load(ec, query, energy, TimeSpan.FromSeconds(80));
        if (samples.Count < 20)
        {
            return null;
        }

        var watts = samples.Select(s => s.Watts).ToList();
        var peak = watts.Max();
        var sustained = watts.TakeLast(15).Average();
        var boost = watts.Skip(watts.IndexOf(peak)).TakeWhile(w => w > (peak + sustained) / 2).Count();
        var temps = samples.Where(s => s.TempC is not null).Select(s => s.TempC!.Value).ToList();
        var hottestLate = samples.TakeLast(15).Where(s => s.TempC is not null).Select(s => s.TempC!.Value).DefaultIfEmpty(0).Max();
        return new LoadResult(peak, sustained, boost, temps.Count == 0 ? null : temps.Max(), hottestLate, idle);
    }

    /// <summary>Waits (up to a minute) for the processor to calm down between modes. Returns the package watts at the end.</summary>
    private static double CoolDown(EmbeddedController ec, PdhQuery query, PdhCounter energy)
    {
        var clock = Stopwatch.StartNew();
        query.Collect();
        double watts = 0;
        while (clock.Elapsed < TimeSpan.FromSeconds(60))
        {
            Thread.Sleep(1000);
            query.Collect();
            watts = PackageWatts(energy) ?? 0;
            if (clock.Elapsed > TimeSpan.FromSeconds(10) && watts < 12 && ec.ReadSensors()?.CpuTempC is < 65)
            {
                break;
            }
        }

        return watts;
    }

    /// <summary>Every logical processor busy for <paramref name="duration"/>; package watts and CPU temperature each second.</summary>
    private static List<(double Watts, int? TempC)> Load(EmbeddedController ec, PdhQuery query, PdhCounter energy, TimeSpan duration)
    {
        var samples = new List<(double, int?)>();
        using var stop = new CancellationTokenSource();
        var workers = Enumerable.Range(0, Environment.ProcessorCount).Select(_ => new Thread(() => Spin(stop.Token)) { IsBackground = true, Priority = ThreadPriority.BelowNormal }).ToList();
        workers.ForEach(t => t.Start());
        try
        {
            query.Collect();
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < duration)
            {
                Thread.Sleep(1000);
                query.Collect();
                var temp = ec.ReadSensors()?.CpuTempC;
                if (temp is >= PowerAbortTempC)
                {
                    throw new InvalidOperationException($"CPU at {temp}°C: stopping the load and putting everything back.");
                }

                if (PackageWatts(energy) is { } watts)
                {
                    samples.Add((watts, temp));
                }
            }
        }
        finally
        {
            stop.Cancel();
            workers.ForEach(t => t.Join());
        }

        return samples;
    }

    private static void Spin(CancellationToken token)
    {
        var x = 1.0;
        while (!token.IsCancellationRequested)
        {
            for (var i = 0; i < 200_000; i++)
            {
                x = Math.Sqrt(x + i) * 1.000001;
            }
        }

        GC.KeepAlive(x);
    }

    private static double? PackageWatts(PdhCounter energy) =>
        energy.Read().FirstOrDefault(kv => kv.Key.EndsWith("_pkg", StringComparison.OrdinalIgnoreCase)) is { Key: not null } kv ? kv.Value / 1000.0 : null;

    // ---- shared -------------------------------------------------------------------------------------

    private static bool Preconditions(Report r, string stage)
    {
        r.Line($"Wattkin self-test ({stage}) · {DateTime.Now:yyyy-MM-dd HH:mm:ss} · version {typeof(SelfTest).Assembly.GetName().Version?.ToString(3)}");
        var info = StaticSystemInfo.Read(new CpuTopology());
        r.Line($"Model: {info.Model} · BIOS {info.BiosVersion}");

        r.Section("Before starting");
        var ok = r.Check(Elevation.IsElevated, "Administrator rights", Elevation.IsElevated ? "" : "run tools\\selftest.ps1, which asks for them");
        var others = Process.GetProcessesByName("Wattkin").Where(p => p.Id != Environment.ProcessId).ToList();
        ok &= r.Check(others.Count == 0, "No other Wattkin running", others.Count == 0 ? "" : "close it first (Wattkin.exe --exit, or its tray menu)");
        var tools = OtherFanTools.Concat(HardwareService.MsiProcesses).Concat(PowerLimitTools).Where(n => Process.GetProcessesByName(n).Length > 0).ToList();
        ok &= r.Check(tools.Count == 0, "YAMDCC, MSI Center and ThrottleStop stopped", tools.Count == 0 ? "" : $"still running: {string.Join(", ", tools)}. Stop the YAMDCC service (Stop-Service yamdccsvc) and close ThrottleStop first.");
        return ok;
    }

    private static MsiAcpi? Open(Report r)
    {
        r.Section("Firmware interface");
        var acpi = MsiAcpi.TryOpen(out var error); // registers Wattkin's WMI description if it isn't yet
        if (acpi is null)
        {
            r.Fail("Firmware interface", error ?? "not available");
            return null;
        }

        r.Pass("Firmware interface opened", $"WMI {acpi.WmiMajor}.{acpi.WmiMinor} · EC '{acpi.EcFirmware}'");
        r.Check(acpi.WmiMajor == 2, "Firmware interface version 2 (the one Wattkin controls)");
        if (string.Equals(acpi.EcVersion, ExpectedFirmware, StringComparison.OrdinalIgnoreCase))
        {
            r.Pass("EC firmware", $"{acpi.EcVersion}, the Stealth 15M A11UEK's");
        }
        else
        {
            r.Info("EC firmware", $"{acpi.EcVersion} (the write tests were written for the Stealth 15M A11UEK's {ExpectedFirmware})");
        }

        return acpi;
    }

    private static void Capabilities(Report r, MsiAcpi acpi, EmbeddedController ec)
    {
        r.Check(ec.CanWrite, "Register map passed its sanity check", $"{ec.Support} · {ec.MapSource ?? "no map"}{(ec.Reason is { } why ? $" · {why}" : "")}");
        r.Check(ec.HasFanCurves, "Fan curve mode available");
        r.Info("Whole fan tables", ec.HasFullFanTable ? "yes, temperatures included" : "no: speeds only, through MSI's calls (the write tests need whole tables)");
        r.Info("Sport mode", ec.HasSportMode ? "offered" : "not offered on this laptop");
        r.Info("Also", $"Cooler Boost {ec.HasCoolerBoost} · keyboard light {ec.HasKeyboardLight} · keyboard auto-off {ec.HasKeyboardTimeout} · webcam switch {ec.WebcamSupported}");
    }

    /// <summary>One test; an error in it is reported and the next one still runs. Stops everything if the CPU is too hot.</summary>
    private static void Step(Report r, EmbeddedController ec, string name, Action test)
    {
        r.Section(name);
        if (ec.ReadSensors()?.CpuTempC is >= AbortTempC)
        {
            throw new InvalidOperationException($"CPU at {AbortTempC}°C or more: stopping the tests and putting everything back.");
        }

        try
        {
            test();
        }
        catch (Exception ex) when (ex is System.Management.ManagementException or System.Runtime.InteropServices.COMException or IOException)
        {
            r.Fail(name, $"error: {ex.Message}");
        }
    }

    /// <summary>Writes back the saved value of each register that no longer holds it.</summary>
    private static void Put(MsiAcpi acpi, Dictionary<byte, byte> saved, params byte[] addresses)
    {
        foreach (var address in addresses)
        {
            if (acpi.ReadEc(address) != saved[address])
            {
                acpi.WriteEc(address, saved[address]);
            }
        }
    }

    /// <summary>Everything back as it was (the order of <c>saved</c>: run-from-battery first, the table before the fan mode), then checked.</summary>
    private static void Restore(Report r, MsiAcpi acpi, Dictionary<byte, byte> saved)
    {
        r.Section("Putting everything back");
        PutBack(acpi, saved);
        var wrong = saved.Where(kv => acpi.ReadEc(kv.Key) != kv.Value).Select(kv => $"0x{kv.Key:X2} is {Hex(acpi.ReadEc(kv.Key))}, was 0x{kv.Value:X2}").ToList();
        r.Check(wrong.Count == 0, "Every register holds its value from before the tests", string.Join(" · ", wrong));
    }

    /// <summary>
    /// Writes back every saved register that doesn't hold its value, in order, for up to 15 seconds: after
    /// a WMI failure the firmware interface refuses calls for 5 s while it reconnects, and quick retries
    /// would all land in that gap and leave the laptop in a test state.
    /// </summary>
    internal static void PutBack(MsiAcpi acpi, IReadOnlyDictionary<byte, byte> saved)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (true)
        {
            var wrong = saved.Where(kv => acpi.ReadEc(kv.Key) != kv.Value).ToList();
            if (wrong.Count == 0 || DateTime.UtcNow > deadline)
            {
                return;
            }

            foreach (var (address, value) in wrong)
            {
                acpi.WriteEc(address, value);
            }

            if (saved.Any(kv => acpi.ReadEc(kv.Key) != kv.Value))
            {
                Thread.Sleep(1000);
            }
        }
    }

    private static int AverageRpm(EmbeddedController ec, int seconds)
    {
        var readings = new List<int>();
        for (var i = 0; i < seconds; i++)
        {
            if (ec.ReadSensors() is { } s)
            {
                readings.Add((FanTest.Plausible(s.Fan1Rpm) + FanTest.Plausible(s.Fan2Rpm)) / 2);
            }

            Thread.Sleep(1000);
        }

        return readings.Count == 0 ? 0 : (int)readings.Average();
    }

    private static bool WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(500);
        }

        return condition();
    }

    /// <summary>The table YAMDCC writes at every start (its selected curve per fan), or null when there's no readable config.</summary>
    internal static FanTable? YamdccTable(out string? names, string? file = null)
    {
        names = null;
        file ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Sparronator9999", "YAMDCC", "CurrentConfig.xml");
        try
        {
            if (!File.Exists(file))
            {
                return null;
            }

            var doc = new XmlDocument { XmlResolver = null };
            using (var reader = XmlReader.Create(file, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
            {
                doc.Load(reader);
            }

            var fans = doc.SelectNodes("/YAMDCC_Config/FanConfs/FanConf");
            if (fans is not { Count: 2 } || Steps(fans[0]!, out var cpuName) is not { } cpu || Steps(fans[1]!, out var gpuName) is not { } gpu)
            {
                return null;
            }

            names = $"CPU \"{cpuName}\", GPU \"{gpuName}\"";
            return new FanTable { Cpu = cpu, Gpu = gpu };
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException or FormatException or OverflowException or System.Xml.XPath.XPathException)
        {
            return null;
        }

        static FanSteps? Steps(XmlNode fan, out string? name)
        {
            name = null;
            var selected = int.Parse(fan.SelectSingleNode("CurveSel")?.InnerText ?? "", CultureInfo.InvariantCulture);
            var curves = fan.SelectNodes("FanCurveConfs/FanCurveConf");
            if (curves is null || selected < 0 || selected >= curves.Count || curves[selected] is not { } curve)
            {
                return null;
            }

            name = curve.SelectSingleNode("Name")?.InnerText;
            var steps = curve.SelectNodes("TempThresholds/TempThreshold");
            if (steps is not { Count: FanSteps.Count })
            {
                return null;
            }

            int Value(int i, string field) => int.Parse(steps[i]!.SelectSingleNode(field)?.InnerText ?? "", CultureInfo.InvariantCulture);
            return new FanSteps
            {
                Speeds = [.. Enumerable.Range(0, FanSteps.Count).Select(i => Value(i, "FanSpeed"))],
                Up = [.. Enumerable.Range(1, FanSteps.Thresholds).Select(i => Value(i, "UpThreshold"))],
                Down = [.. Enumerable.Range(1, FanSteps.Thresholds).Select(i => Value(i, "DownThreshold"))],
            };
        }
    }

    private static string Hex(byte? value) => value is { } v ? $"0x{v:X2}" : "read failed";

    private sealed class Report
    {
        private readonly StringBuilder _text = new();

        public int Passed { get; private set; }
        public int Failed { get; private set; }
        public int Warnings { get; private set; }
        public int Skipped { get; private set; }

        public string Summary => $"{Passed} passed, {Failed} failed, {Warnings} warnings, {Skipped} skipped";

        public void Line(string text) => _text.AppendLine(text);

        public void Section(string title)
        {
            _text.AppendLine();
            _text.AppendLine($"== {title}");
        }

        public bool Check(bool ok, string name, string detail = "")
        {
            if (ok)
            {
                Pass(name, detail);
            }
            else
            {
                Fail(name, detail);
            }

            return ok;
        }

        public void Pass(string name, string detail = "")
        {
            Passed++;
            Add("PASS", name, detail);
        }

        public void Fail(string name, string detail = "")
        {
            Failed++;
            Add("FAIL", name, detail);
        }

        public void Warn(string name, string detail = "")
        {
            Warnings++;
            Add("WARN", name, detail);
        }

        public void Skip(string name, string detail = "")
        {
            Skipped++;
            Add("SKIP", name, detail);
        }

        public void Info(string name, string detail = "") => Add("INFO", name, detail);

        private void Add(string level, string name, string detail) =>
            _text.AppendLine(detail.Length == 0 ? $"{level}  {name}" : $"{level}  {name}: {detail}");

        public override string ToString() => _text.ToString();
    }
}
