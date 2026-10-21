using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Wattkin.Native;

namespace Wattkin.Platform;

public static class Elevation
{
    public static bool IsElevated { get; } = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    /// <summary>Starts an elevated copy (UAC prompt). Returns false if the user declined.</summary>
    public static bool RestartElevated(string arguments = "--elevated")
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, arguments) { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// True when this exe lives under Program Files, where only administrators can change it. An
    /// elevated logon task must never point at a file the user (or malware running as the user) can replace.
    /// </summary>
    /// <summary>Runs this exe elevated (UAC) with <paramref name="arguments"/> and waits. Null if the user declined.</summary>
    public static int? RunElevatedAndWait(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, arguments) { UseShellExecute = true, Verb = "runas" });
            process?.WaitForExit();
            return process?.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    public static bool IsInstalledSecurely => IsUnderProgramFiles(Environment.ProcessPath);

    public static bool IsUnderProgramFiles(string? path)
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        return path is not null && Path.GetFullPath(path).StartsWith(programFiles + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Starts Wattkin at logon with highest privileges, so there is no UAC prompt every boot. Registered
/// through the Task Scheduler COM API (no temporary XML file), and only for the Program Files copy.
/// </summary>
public static class StartupTask
{
    private const string TaskName = "Wattkin";
    private const int TASK_CREATE_OR_UPDATE = 6;
    private const int TASK_LOGON_INTERACTIVE_TOKEN = 3;

    public static bool IsEnabled() => Folder() is { } folder && TryGetTask(folder) is not null;

    /// <summary>Needs elevation. <paramref name="exe"/> defaults to this exe. Returns an error message or null.</summary>
    public static string? Enable(string? exe = null)
    {
        exe ??= Environment.ProcessPath!;
        if (!Elevation.IsUnderProgramFiles(exe))
        {
            return "Start with Windows only works for the installed copy (in Program Files). Install Wattkin first, then turn this on there.";
        }

        var user = WindowsIdentity.GetCurrent().Name;
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Starts Wattkin (laptop power and battery control) at logon.</Description></RegistrationInfo>
              <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{System.Security.SecurityElement.Escape(user)}</UserId><Delay>PT5S</Delay></LogonTrigger></Triggers>
              <Principals><Principal id="Author"><UserId>{System.Security.SecurityElement.Escape(user)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>5</Priority>
              </Settings>
              <Actions Context="Author"><Exec><Command>{System.Security.SecurityElement.Escape(exe)}</Command><Arguments>--startup</Arguments></Exec></Actions>
            </Task>
            """;
        try
        {
            if (Folder() is not { } folder)
            {
                return "Task Scheduler isn't available.";
            }

            folder.RegisterTask(TaskName, xml, TASK_CREATE_OR_UPDATE, null, null, TASK_LOGON_INTERACTIVE_TOKEN, null);
            return null;
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return $"Couldn't create the logon task: {ex.Message}";
        }
    }

    /// <summary>Starts Wattkin through its logon task (as administrator, no UAC prompt). False if there's no task.</summary>
    public static bool TryRun()
    {
        try
        {
            if (Folder() is { } folder && TryGetTask(folder) is { } task)
            {
                task.Run(null);
                return true;
            }
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
        }

        return false;
    }

    public static string? Disable()
    {
        try
        {
            if (Folder() is { } folder && TryGetTask(folder) is not null)
            {
                folder.DeleteTask(TaskName, 0);
            }

            return null;
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return $"Couldn't remove the logon task: {ex.Message}";
        }
    }

    private static dynamic? Folder()
    {
        try
        {
            if (Type.GetTypeFromProgID("Schedule.Service") is not { } type)
            {
                return null;
            }

            dynamic service = Activator.CreateInstance(type)!;
            service.Connect();
            return service.GetFolder(@"\");
        }
        catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return null;
        }
    }

    private static dynamic? TryGetTask(dynamic folder)
    {
        try
        {
            return folder.GetTask(TaskName);
        }
        catch (Exception ex) when (ex is COMException or FileNotFoundException or DirectoryNotFoundException)
        {
            return null; // 'not found' surfaces as FileNotFoundException (HRESULT 0x80070002)
        }
    }
}

/// <summary>Keeps the PC awake (handle-based, independent of which thread asks).</summary>
public sealed class StayAwake : IDisposable
{
    private const int PowerRequestDisplayRequired = 0;
    private const int PowerRequestSystemRequired = 1;

    private readonly IntPtr _request;
    private IntPtr _reason;
    private bool _display;
    private bool _system;

    [StructLayout(LayoutKind.Sequential)]
    private struct ReasonContext
    {
        public uint Version;
        public uint Flags;
        public IntPtr SimpleReasonString;
        public IntPtr Pad1;
        public IntPtr Pad2;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr PowerCreateRequest(ref ReasonContext context);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PowerSetRequest(IntPtr request, int type);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PowerClearRequest(IntPtr request, int type);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    public StayAwake(string reason)
    {
        _reason = Marshal.StringToHGlobalUni(reason);
        var context = new ReasonContext { Version = 0, Flags = 1, SimpleReasonString = _reason };
        _request = PowerCreateRequest(ref context);
    }

    public void Set(bool system, bool display)
    {
        if (_request == IntPtr.Zero || _request == new IntPtr(-1))
        {
            return;
        }

        Toggle(ref _system, system, PowerRequestSystemRequired);
        Toggle(ref _display, display, PowerRequestDisplayRequired);
    }

    private void Toggle(ref bool current, bool wanted, int type)
    {
        if (current == wanted)
        {
            return;
        }

        current = wanted ? PowerSetRequest(_request, type) : !PowerClearRequest(_request, type);
    }

    public void Dispose()
    {
        Set(false, false);
        if (_request != IntPtr.Zero && _request != new IntPtr(-1))
        {
            CloseHandle(_request);
        }

        if (_reason != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_reason);
            _reason = IntPtr.Zero;
        }
    }
}

/// <summary>
/// Refresh-rate switching for the laptop's own panel (found via QueryDisplayConfig as the internal
/// output), never whichever external monitor happens to be primary.
/// </summary>
public static class DisplayModes
{
    private const uint QDC_ONLY_ACTIVE_PATHS = 2;
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
    private const uint OUTPUT_INTERNAL = 0x80000000;
    private const uint OUTPUT_LVDS = 6;
    private const uint OUTPUT_DISPLAYPORT_EMBEDDED = 11;
    private const uint OUTPUT_UDI_EMBEDDED = 13;

    // LUIDs are two DWORDs (4-byte aligned): Pack = 4 keeps the native 72-byte layout.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PathInfo
    {
        public long SourceAdapterId;
        public uint SourceId;
        public uint SourceModeInfoIdx;
        public uint SourceStatusFlags;
        public long TargetAdapterId;
        public uint TargetId;
        public uint TargetModeInfoIdx;
        public uint OutputTechnology;
        public uint Rotation;
        public uint Scaling;
        public uint RefreshNumerator;
        public uint RefreshDenominator;
        public uint ScanLineOrdering;
        public int TargetAvailable;
        public uint TargetStatusFlags;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct ModeInfo
    {
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4, CharSet = CharSet.Unicode)]
    private struct SourceDeviceName
    {
        public uint Type;
        public uint Size;
        public long AdapterId;
        public uint Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string ViewGdiDeviceName;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] PathInfo[] paths, ref uint modeCount, [Out] ModeInfo[] modes, IntPtr topology);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref SourceDeviceName request);

    /// <summary>
    /// GDI name (e.g. \\.\DISPLAY1) of the built-in panel, or null if it's off / not found. When no
    /// output says it's built in but the only screen on is one whose kind Windows can't name ("other"),
    /// that's taken as the laptop's own: a user's Prestige 13 AI Evo A1MG showed no refresh rates at all
    /// in 1.1.1 (diagnostics report, 2026-10-16; the report now lists the outputs). Never a screen on a
    /// known external connector, so an external monitor with the lid closed keeps its own refresh rate.
    /// </summary>
    public static string? InternalDisplay()
    {
        var paths = ActivePaths();
        foreach (var path in paths)
        {
            if (IsInternal(path.OutputTechnology) && GdiName(path) is { } name)
            {
                return name;
            }
        }

        return paths.Count == 1 && paths[0].OutputTechnology == OUTPUT_OTHER ? GdiName(paths[0]) : null;
    }

    private const uint OUTPUT_OTHER = 0xFFFFFFFF;

    /// <summary>For the diagnostics report: each active output's technology code and GDI name.</summary>
    public static string DescribeOutputs()
    {
        var paths = ActivePaths();
        return paths.Count == 0
            ? "none reported"
            : string.Join(", ", paths.Select(p => $"0x{p.OutputTechnology:X}{(IsInternal(p.OutputTechnology) ? " (built in)" : "")} {GdiName(p) ?? "?"}"));
    }

    private static bool IsInternal(uint technology) => technology is OUTPUT_INTERNAL or OUTPUT_LVDS or OUTPUT_DISPLAYPORT_EMBEDDED or OUTPUT_UDI_EMBEDDED;

    private static List<PathInfo> ActivePaths()
    {
        try
        {
            // The configuration can change between the two calls (a monitor plugged in): try again then.
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out var pathCount, out var modeCount) != 0)
                {
                    return [];
                }

                var paths = new PathInfo[pathCount];
                var modes = new ModeInfo[modeCount];
                var status = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
                if (status == 0)
                {
                    return [.. paths.AsSpan(0, (int)pathCount)];
                }

                if (status != ErrorInsufficientBuffer)
                {
                    return [];
                }
            }
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or MarshalDirectiveException)
        {
        }

        return [];
    }

    private const int ErrorInsufficientBuffer = 122;

    private static string? GdiName(PathInfo path)
    {
        var request = new SourceDeviceName
        {
            Type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
            Size = (uint)Marshal.SizeOf<SourceDeviceName>(),
            AdapterId = path.SourceAdapterId,
            Id = path.SourceId,
            ViewGdiDeviceName = string.Empty,
        };
        return DisplayConfigGetDeviceInfo(ref request) == 0 && !string.IsNullOrEmpty(request.ViewGdiDeviceName) ? request.ViewGdiDeviceName : null;
    }

    public static int? CurrentRefreshRate()
    {
        var device = InternalDisplay();
        if (device is null)
        {
            return null;
        }

        var mode = NewDevMode();
        return User32.EnumDisplaySettings(device, User32.ENUM_CURRENT_SETTINGS, ref mode) ? mode.dmDisplayFrequency : null;
    }

    /// <summary>Refresh rates the built-in panel offers at its current resolution.</summary>
    public static IReadOnlyList<int> SupportedRefreshRates()
    {
        var device = InternalDisplay();
        var current = NewDevMode();
        if (device is null || !User32.EnumDisplaySettings(device, User32.ENUM_CURRENT_SETTINGS, ref current))
        {
            return [];
        }

        var rates = new SortedSet<int>();
        var mode = NewDevMode();
        for (var i = 0; User32.EnumDisplaySettings(device, i, ref mode); i++)
        {
            if (mode.dmPelsWidth == current.dmPelsWidth && mode.dmPelsHeight == current.dmPelsHeight && mode.dmBitsPerPel == current.dmBitsPerPel && mode.dmDisplayFrequency > 1)
            {
                rates.Add(mode.dmDisplayFrequency);
            }
        }

        return [.. rates];
    }

    public static bool SetRefreshRate(int hz)
    {
        var device = InternalDisplay();
        if (device is null || !SupportedRefreshRates().Contains(hz))
        {
            return false;
        }

        var mode = NewDevMode();
        if (!User32.EnumDisplaySettings(device, User32.ENUM_CURRENT_SETTINGS, ref mode) || mode.dmDisplayFrequency == hz)
        {
            return mode.dmDisplayFrequency == hz;
        }

        mode.dmDisplayFrequency = hz;
        mode.dmFields = User32.DM_DISPLAYFREQUENCY;
        return User32.ChangeDisplaySettingsEx(device, ref mode, IntPtr.Zero, User32.CDS_UPDATEREGISTRY, IntPtr.Zero) == User32.DISP_CHANGE_SUCCESSFUL;
    }

    private static User32.DEVMODE NewDevMode() => new()
    {
        dmDeviceName = string.Empty,
        dmFormName = string.Empty,
        dmSize = (short)Marshal.SizeOf<User32.DEVMODE>(),
    };
}

/// <summary>Process-wide hardening for an app that runs elevated in a user's session.</summary>
internal static class ProcessHardening
{
    private const int ProcessRedirectionTrustPolicy = 16;

    [StructLayout(LayoutKind.Sequential)]
    private struct RedirectionTrustPolicy
    {
        public uint Flags; // bit0 EnforceRedirectionTrust, bit1 AuditRedirectionTrust
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessMitigationPolicy(int policy, ref RedirectionTrustPolicy buffer, int length);

    /// <summary>
    /// Don't follow junctions/symlinks planted by non-admin users when this elevated process writes
    /// files in the user's profile (settings, logs). Windows 11 21H2+; ignored where unsupported.
    /// </summary>
    public static void Apply()
    {
        if (!Elevation.IsElevated)
        {
            return;
        }

        var policy = new RedirectionTrustPolicy { Flags = 1 };
        if (!SetProcessMitigationPolicy(ProcessRedirectionTrustPolicy, ref policy, Marshal.SizeOf<RedirectionTrustPolicy>()))
        {
            Log.Warn($"Redirection trust policy not applied (error {Marshal.GetLastWin32Error()}).");
        }
    }
}
