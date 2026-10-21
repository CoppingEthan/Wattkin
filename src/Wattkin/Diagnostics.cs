using System.Diagnostics;
using System.Text;
using Wattkin.Hardware;
using Wattkin.Monitoring;
using Wattkin.Platform;

namespace Wattkin;

/// <summary>
/// Read-only hardware report (Wattkin.exe --diagnose [file], or Settings › Debug). Never writes to the
/// EC or firmware, and never asks the RTX anything (that would wake it).
/// </summary>
public static class Diagnostics
{
    private static readonly byte[] Registers = [0xD7, 0xD2, 0xD3, 0xD4, 0x98, 0x2C, 0x2D, 0x2E, 0x2F, 0xD9, 0xBF, 0xD1, 0xBE, 0xE8, 0xD0];

    public static void Run(string? path)
    {
        // No file given ("--diagnose" alone passes ""), or a bare file name: the default folder / the current one.
        path = string.IsNullOrWhiteSpace(path)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wattkin", "diagnostics.txt")
            : Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var sb = new StringBuilder();
        void Line(string text) => sb.AppendLine(text);
        void Try(string label, Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Line($"{label}: ERROR {ex.GetType().Name}: {ex.Message}");
            }
        }

        Line($"Wattkin diagnostics · {DateTime.Now:yyyy-MM-dd HH:mm:ss} · version {typeof(Diagnostics).Assembly.GetName().Version}");
        Line($"Elevated: {Elevation.IsElevated} · OS {Environment.OSVersion.Version}");

        Try("System", () =>
        {
            var topology = new CpuTopology();
            var info = StaticSystemInfo.Read(topology);
            Line($"Model: {info.Model} · BIOS {info.BiosVersion}");
            Line($"CPU: {info.CpuName} · P-threads {info.PerformanceThreads} · E-threads {info.EfficiencyThreads}");
            Line($"Memory: {info.InstalledMemoryBytes / 1024 / 1024} MB {info.MemoryType} · {info.MemoryConfiguredMTs}/{info.MemoryRatedMTs} MT/s · {info.MemoryPart}");
        });

        Try("Battery", () =>
        {
            using var battery = new BatteryDevice();
            var info = battery.ReadInfo();
            var status = battery.ReadStatus();
            Line($"Battery info: {info}");
            Line($"Battery status: {status}");
            Line($"Battery temperature: {battery.ReadTemperatureC()?.ToString("0.0") ?? "n/a"}");
        });

        Try("Counters", () =>
        {
            using var query = new PdhQuery();
            var energy = query.Add(@"\Energy Meter(*)\Power");
            var perf = query.Add(@"\Processor Information(_Total)\% Processor Performance");
            var freq = query.Add(@"\Processor Information(_Total)\Processor Frequency");
            var thermal = query.Add(@"\Thermal Zone Information(*)\High Precision Temperature");
            query.Collect();
            Thread.Sleep(1000);
            query.Collect();
            Line("Energy meters (mW): " + string.Join(", ", energy?.Read().Select(kv => $"{kv.Key}={kv.Value:0}") ?? []));
            Line($"CPU: performance {perf?.ReadInstance("_Total"):0.0}% of base {freq?.ReadInstance("_Total"):0} MHz");
            Line("Thermal zones (°C): " + string.Join(", ", thermal?.Read().Select(kv => $"{kv.Key}={(kv.Value / 10) - 273.15:0.0}") ?? []));
        });

        Try("dGPU", () => Line($"dGPU: {DiscreteGpu.Query()}"));
        Try("Keyboard light", () => Line(MysticLight.Find() is { } light
            ? $"Keyboard light (USB): {light.Product} · usage page 0x{light.UsagePage:X4} · feature report {light.FeatureReportLength} bytes · {(light.Usable ? "used by Wattkin" : "unknown layout, not used")}"
            : "Keyboard light (USB): no Mystic Light controller (the keys are lit by the EC, if at all)"));
        Try("Display", () => Line($"Display: current {DisplayModes.CurrentRefreshRate()} Hz, available {string.Join(", ", DisplayModes.SupportedRefreshRates())} · built-in panel {DisplayModes.InternalDisplay() ?? "not found"} · outputs {DisplayModes.DescribeOutputs()}"));
        Try("Screen", () => Line($"Screen: backlight {(Backlight.Get() is { } b ? $"{b}%" : "not controllable")} · Windows gamma range {(GammaRamp.Limited() ? "limited" : "full")} · f.lux {(System.Diagnostics.Process.GetProcessesByName("flux").Length > 0 ? "running" : "not running")}"));
        Try("Power", () => Line($"Windows power mode: AC {WindowsPower.GetMode(true)}, DC {WindowsPower.GetMode(false)} · CPU boost AC {WindowsPower.GetCpuBoost(true)}, DC {WindowsPower.GetCpuBoost(false)} · critical battery {WindowsPower.CriticalBatteryPercent()}%"));
        // msiexec is Windows Installer, not MSI's.
        Try("MSI processes", () => Line("MSI processes running: " + string.Join(", ", Process.GetProcesses().Where(p => (p.ProcessName.Contains("MSI", StringComparison.OrdinalIgnoreCase) && !p.ProcessName.Equals("msiexec", StringComparison.OrdinalIgnoreCase)) || p.ProcessName.StartsWith("OmAp", StringComparison.OrdinalIgnoreCase)).Select(p => p.ProcessName).Distinct())));

        if (Elevation.IsElevated)
        {
            Try("WMI", () =>
            {
                // How Windows knows the interface: Wattkin's own classes, and whether MSI's description file is around.
                using var wmiAcpi = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\WmiAcpi");
                Line($"Interface description: Wattkin's {(WmiSchema.IsRegistered() ? "registered" : "NOT registered")} · MSI's (WmiAcpi MofImagePath) {wmiAcpi?.GetValue("MofImagePath") ?? "none"}");
            });

            Try("EC", () =>
            {
                // Read-only: the report never registers anything either.
                using var acpi = MsiAcpi.TryOpen(out var error, readOnly: true);
                if (acpi is null)
                {
                    Line($"Firmware interface: {error}");
                    return;
                }

                Line($"Firmware interface: WMI {acpi.WmiMajor}.{acpi.WmiMinor} · EC firmware '{acpi.EcFirmware}'");
                foreach (var register in Registers)
                {
                    var value = acpi.ReadEc(register);
                    Line($"  EC 0x{register:X2} = {(value is { } v ? $"0x{v:X2} ({Convert.ToString(v, 2).PadLeft(8, '0')})" : "read failed")}");
                }

                // Get_Fan(1|2) / Get_Temperature(1|2): the fan curves (speeds and temperature steps).
                foreach (var (method, selector) in new[] { ("Get_Temperature", (byte)0), ("Get_Temperature", (byte)1), ("Get_Temperature", (byte)2), ("Get_Fan", (byte)0), ("Get_Fan", (byte)1), ("Get_Fan", (byte)2), ("Get_AP", (byte)0), ("Get_AP", (byte)1), ("Get_AP", (byte)2), ("Get_Device", (byte)1) })
                {
                    var data = acpi.Get(method, selector);
                    Line($"  {method}({selector}) = {(data is null ? "failed" : BitConverter.ToString(data, 0, 16))}");
                }

                var ec = new EmbeddedController(acpi);
                Line($"  Register map: {ec.Support} ({ec.MapSource ?? "none"}) · writes {(ec.CanWrite ? "enabled" : "disabled")}");
                if (ec.Reason is { } reason)
                {
                    Line($"  Why: {reason}");
                }

                Line($"  Decoded: limit {ec.GetChargeLimit()}% · shift {ec.GetShiftMode()} · fan {ec.GetFanMode()} · cooler boost {ec.GetCoolerBoost()} · keyboard {ec.GetKeyboardLevel()} auto-off {ec.GetKeyboardAutoOff()} · webcam {ec.GetWebcam()} · fn-lock {ec.GetFnLock()} · simulate-unplug {ec.GetSimulatedUnplug()}");
                var samples = new List<string>();
                for (var i = 0; i < 10; i++)
                {
                    samples.Add(ec.ReadSensors() is { } s ? $"{s.Fan1Rpm}/{s.Fan2Rpm} rpm {s.CpuTempC}°C" : "failed");
                    Thread.Sleep(1000);
                }

                Line($"  Sensors over 10 s (fans, CPU): {string.Join(" · ", samples)}");

                // All 256 registers, read only: the whole picture when something doesn't behave.
                Line("  EC dump:   " + string.Join(" ", Enumerable.Range(0, 16).Select(c => $"_{c:X}")));
                for (var row = 0; row < 16; row++)
                {
                    var cells = Enumerable.Range(0, 16).Select(c => acpi.ReadEc((byte)((row * 16) + c)) is { } v ? v.ToString("X2") : "??");
                    Line($"  EC {row:X}_:     {string.Join(" ", cells)}");
                }
            });

            Try("Firmware GPU", () =>
            {
                var gpu = FirmwareGpuSwitch.Read(out var error);
                Line($"Firmware GPU switch: {(gpu?.ToString() ?? error)}");
            });
        }
        else
        {
            Line("EC / firmware: skipped (needs administrator).");
        }

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }
}
