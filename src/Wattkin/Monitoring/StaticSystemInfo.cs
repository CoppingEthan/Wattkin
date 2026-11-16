using System.Management;

namespace Wattkin.Monitoring;

/// <summary>Facts that don't change while running. Read once, off the UI thread.</summary>
public sealed record StaticSystemInfo(
    string Model,
    string BiosVersion,
    string CpuName,
    int PerformanceThreads,
    int EfficiencyThreads,
    ulong InstalledMemoryBytes,
    string MemoryType,
    int? MemoryConfiguredMTs,
    int? MemoryRatedMTs,
    string? MemoryPart)
{
    public static StaticSystemInfo Read(CpuTopology topology)
    {
        string model = "Unknown laptop", bios = "?", memType = "RAM";
        ulong installed = 0;
        int? configured = null, rated = null;
        string? part = null;

        try
        {
            using (var cs = new ManagementObjectSearcher("SELECT Manufacturer, Model FROM Win32_ComputerSystem"))
            {
                foreach (var o in cs.Get())
                {
                    model = $"{o["Manufacturer"]} {o["Model"]}".Replace("Micro-Star International Co., Ltd.", "MSI").Trim();
                }
            }

            using (var bs = new ManagementObjectSearcher("SELECT SMBIOSBIOSVersion FROM Win32_BIOS"))
            {
                foreach (var o in bs.Get())
                {
                    bios = o["SMBIOSBIOSVersion"]?.ToString() ?? bios;
                }
            }

            using var ms = new ManagementObjectSearcher("SELECT Capacity, Speed, ConfiguredClockSpeed, SMBIOSMemoryType, PartNumber FROM Win32_PhysicalMemory");
            foreach (var o in ms.Get())
            {
                installed += Convert.ToUInt64(o["Capacity"] ?? 0UL);
                configured ??= ToInt(o["ConfiguredClockSpeed"]);
                rated ??= ToInt(o["Speed"]);
                part ??= o["PartNumber"]?.ToString()?.Trim();
                memType = Convert.ToInt32(o["SMBIOSMemoryType"] ?? 0) switch
                {
                    24 => "DDR3",
                    26 => "DDR4",
                    29 => "LPDDR3",
                    30 => "LPDDR4",
                    34 => "DDR5",
                    35 => "LPDDR5",
                    _ => memType,
                };
            }
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException or InvalidCastException or FormatException or OverflowException)
        {
            // Leave defaults; this is display-only information.
        }

        return new StaticSystemInfo(model, bios, topology.Name, topology.PerformanceThreads, topology.EfficiencyThreads, installed, memType, configured, rated, part);
    }

    private static int? ToInt(object? value) => value is null ? null : Convert.ToInt32(value) is var v && v > 0 ? v : null;
}
