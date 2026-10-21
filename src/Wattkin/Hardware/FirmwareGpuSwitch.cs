using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Wattkin.Hardware;

public enum FirmwareGpuMode
{
    Hybrid = 0,
    Discrete = 1,
    Integrated = 2,
}

public sealed record FirmwareGpuInfo(
    FirmwareGpuMode Current,
    FirmwareGpuMode Requested,
    bool SwitchSupported,
    bool IntegratedSupported,
    bool DiscreteSupported)
{
    public bool SwitchPending => Requested != Current;
}

/// <summary>
/// MSI's firmware GPU switch (what MSI Center's "GPU Switch" does), reproduced exactly from
/// OmApSvcBroker.exe and API_NB_Base Module.dll:
///  1. UEFI variable MsiDCVarData {DD96BAAF-145E-4F56-B1CF-193256298E99}, byte 5:
///     bits 0-1 = requested mode, bits 2-3 = current mode (0 Hybrid, 1 Discrete, 2 Integrated),
///     0x10 = switch supported, 0x20 = integrated-only supported, 0x40 = discrete NOT supported.
///  2. EC 0xD1: clear bits 0-1, set bit 0 (request); the EC acknowledges with bit 1.
///  3. EC 0xBE = 0x02, then a full shutdown; the firmware applies the mode on the next cold boot.
/// Only the two "requested mode" bits of the variable are changed; everything else is written back as read.
/// </summary>
public static class FirmwareGpuSwitch
{
    private const string VariableName = "MsiDCVarData";
    private const string VariableGuid = "{DD96BAAF-145E-4F56-B1CF-193256298E99}";
    private const int ModeByte = 5;
    private const byte RegRequest = 0xD1;
    private const byte RegBiosStatus = 0xBE;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFirmwareEnvironmentVariableExW(string name, string guid, byte[] buffer, uint size, out uint attributes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetFirmwareEnvironmentVariableExW(string name, string guid, byte[] value, uint size, uint attributes);

    public static FirmwareGpuInfo? Read(out string? error)
    {
        var data = ReadVariable(out _, out error);
        if (data is null)
        {
            return null;
        }

        var b = data[ModeByte];
        return new FirmwareGpuInfo(
            (FirmwareGpuMode)((b >> 2) & 0x03),
            (FirmwareGpuMode)(b & 0x03),
            (b & 0x10) != 0,
            (b & 0x20) != 0,
            (b & 0x40) == 0);
    }

    /// <summary>
    /// Arms the switch. The caller must then shut the PC down (see <see cref="ShutDownToApply"/>).
    /// Returns an error message, or null on success.
    /// </summary>
    public static string? RequestMode(FirmwareGpuMode mode, MsiAcpi acpi)
    {
        // One request at a time: two overlapping requests would verify and restore each other's writes.
        if (!RequestGate.Wait(0))
        {
            return "A GPU mode change is already in progress.";
        }

        try
        {
            return RequestModeLocked(mode, acpi);
        }
        finally
        {
            RequestGate.Release();
        }
    }

    private static readonly SemaphoreSlim RequestGate = new(1, 1);

    private static string? RequestModeLocked(FirmwareGpuMode mode, MsiAcpi acpi)
    {
        var data = ReadVariable(out var attributes, out var error);
        if (data is null)
        {
            return error;
        }

        var b = data[ModeByte];
        var current = (FirmwareGpuMode)((b >> 2) & 0x03);
        var requested = (FirmwareGpuMode)(b & 0x03);
        if (current == mode && requested == mode)
        {
            return null; // already in that mode, nothing pending: don't touch firmware storage
        }
        if ((b & 0x10) == 0)
        {
            return "This laptop's firmware does not offer the GPU switch.";
        }

        if (mode == FirmwareGpuMode.Integrated && (b & 0x20) == 0)
        {
            return "Integrated-only mode is not supported on this laptop.";
        }

        if (mode == FirmwareGpuMode.Discrete && (b & 0x40) != 0)
        {
            return "Discrete-only mode is not supported on this laptop.";
        }

        var updated = (byte[])data.Clone();
        updated[ModeByte] = (byte)((b & 0xFC) | (int)mode);
        if (!SetFirmwareEnvironmentVariableExW(VariableName, VariableGuid, updated, (uint)updated.Length, attributes))
        {
            return $"Writing the firmware setting failed (error {Marshal.GetLastWin32Error()}).";
        }

        var verify = ReadVariable(out _, out _);
        if (verify is null || !verify.AsSpan().SequenceEqual(updated))
        {
            var restored = Restore(data, attributes);
            return restored
                ? "The firmware setting didn't read back as written, so it was put back as it was. Nothing changed."
                : "The firmware setting didn't read back as written, and restoring it failed. Don't restart yet; save a diagnostics report.";
        }

        Log.Info($"MsiDCVarData[5]: 0x{b:X2} -> 0x{updated[ModeByte]:X2} (request {mode})");

        // EC handshake, as MSI Center does it: D1 = (D1 & ~3) | 1, atomically under the EC lock.
        if (!acpi.WriteEcBits(RegRequest, 0x03, 0x01, force: true))
        {
            var restored = Restore(data, attributes);
            return restored
                ? "The embedded controller didn't accept the switch request, so the firmware setting was put back. Nothing changed."
                : "The embedded controller didn't accept the switch request, and restoring the firmware setting failed. The switch may still happen at the next cold boot.";
        }

        Thread.Sleep(2000);
        var ack = acpi.ReadEc(RegRequest);
        if (ack is { } a && (a & 0x02) != 0)
        {
            acpi.WriteEc(RegBiosStatus, 0x02);
        }
        else
        {
            Log.Warn($"EC 0xD1 acknowledge bit not set (0x{ack ?? 0:X2}); the switch applies on next cold boot.");
        }

        return null;
    }

    private static bool Restore(byte[] original, uint attributes)
    {
        var ok = SetFirmwareEnvironmentVariableExW(VariableName, VariableGuid, original, (uint)original.Length, attributes)
            && ReadVariable(out _, out _) is { } check && check.AsSpan().SequenceEqual(original);
        Log.Warn($"MsiDCVarData restored to original: {ok}");
        return ok;
    }

    /// <summary>Full shutdown (not restart), like MSI Center. Apps get the normal chance to save.</summary>
    public static void ShutDownToApply() =>
        // By full path: elevated, a bare name also searches the current folder.
        Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"), "/s /t 0") { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Environment.SystemDirectory });

    private static byte[]? ReadVariable(out uint attributes, out string? error)
    {
        attributes = 0;
        if (!Privileges.Enable("SeSystemEnvironmentPrivilege"))
        {
            error = "Administrator rights are needed to read firmware settings.";
            return null;
        }

        const int ERROR_INSUFFICIENT_BUFFER = 122;
        for (var capacity = 64; capacity <= 4096; capacity *= 4)
        {
            var buffer = new byte[capacity];
            var size = GetFirmwareEnvironmentVariableExW(VariableName, VariableGuid, buffer, (uint)buffer.Length, out attributes);
            var lastError = Marshal.GetLastWin32Error();
            if (size == 0 && lastError == ERROR_INSUFFICIENT_BUFFER)
            {
                continue; // a BIOS update grew the variable
            }

            if (size <= ModeByte)
            {
                error = size == 0 ? $"MSI firmware setting not found (error {lastError})." : "MSI firmware setting is shorter than expected.";
                return null;
            }

            error = null;
            return buffer[..(int)size];
        }

        error = "MSI firmware setting is unexpectedly large.";
        return null;
    }
}

internal static class Privileges
{
    private const uint TOKEN_ADJUST_PRIVILEGES = 0x20;
    private const uint TOKEN_QUERY = 0x8;
    private const uint SE_PRIVILEGE_ENABLED = 0x2;

    // TOKEN_PRIVILEGES with one LUID_AND_ATTRIBUTES; LUID is two DWORDs, so 4-byte packing.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct TokenPrivileges
    {
        public uint Count;
        public long Luid;
        public uint Attributes;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out long luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivileges state, int length, IntPtr previous, IntPtr returnLength);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    private static readonly HashSet<string> Enabled = [];

    /// <summary>Enables a privilege in this process's token (once; it stays enabled).</summary>
    public static bool Enable(string privilege)
    {
        lock (Enabled)
        {
            if (Enabled.Contains(privilege))
            {
                return true;
            }

            if (EnableCore(privilege))
            {
                Enabled.Add(privilege);
                return true;
            }

            return false;
        }
    }

    private static bool EnableCore(string privilege)
    {
        // The pseudo-handle needs no closing and can't be finalized away mid-call.
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token))
        {
            return false;
        }

        try
        {
            if (!LookupPrivilegeValue(null, privilege, out var luid))
            {
                return false;
            }

            var state = new TokenPrivileges { Count = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
            return AdjustTokenPrivileges(token, false, ref state, 0, IntPtr.Zero, IntPtr.Zero) && Marshal.GetLastWin32Error() == 0;
        }
        finally
        {
            CloseHandle(token);
        }
    }
}
