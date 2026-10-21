using System.Diagnostics;
using Microsoft.Win32;
using Wattkin.Features;
using Wattkin.Settings;

namespace Wattkin.Platform;

/// <summary>
/// Wattkin installs itself: one exe is the installer, the app and the update target. Installed means
/// %ProgramFiles%\Wattkin\Wattkin.exe (only administrators can change it, which matters because it
/// runs as administrator at logon), a Start-menu shortcut, the logon task, and an entry in
/// Settings › Apps whose uninstall runs "Wattkin.exe --uninstall".
/// </summary>
internal static class Installation
{
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Wattkin";
    private const string RepositoryUrl = "https://github.com/CoppingEthan/Wattkin";

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Wattkin");
    public static string Exe => Path.Combine(Folder, "Wattkin.exe");
    private static string Shortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "Wattkin.lnk");

    /// <summary>Per-user shortcut made by the old install.ps1.</summary>
    private static string LegacyShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Wattkin.lnk");

    public static bool IsInstalled => File.Exists(Exe);

    public static bool RunningInstalled => string.Equals(Environment.ProcessPath, Exe, StringComparison.OrdinalIgnoreCase);

    public static Version? InstalledVersion =>
        IsInstalled && Releases.TryParseVersion(FileVersionInfo.GetVersionInfo(Exe).ProductVersion, out var v) ? v : null;

    /// <summary>
    /// Elevated. Copies this exe into Program Files and registers it. <paramref name="startWithWindows"/>
    /// null leaves the logon task as it is (an update). Returns an error message or null.
    /// </summary>
    public static string? Install(bool? startWithWindows)
    {
        try
        {
            StopRunningCopy();
            Directory.CreateDirectory(Folder);
            var source = Environment.ProcessPath!;
            if (!string.Equals(source, Exe, StringComparison.OrdinalIgnoreCase))
            {
                var staged = Exe + ".new";
                File.Copy(source, staged, overwrite: true);
                if (Swap(staged) is { } swapError)
                {
                    return swapError;
                }
            }

            CreateShortcut();
            Register();
            if (startWithWindows == true && StartupTask.Enable(Exe) is { } taskError)
            {
                Log.Warn(taskError);
            }

            // How Windows reaches MSI's firmware interface without any MSI software (also checked at every start).
            if (global::Wattkin.Hardware.WmiSchema.EnsureRegistered() is { } schemaError)
            {
                Log.Warn(schemaError);
            }

            Log.Info($"Installed Wattkin {CurrentVersion.ToString(3)} to {Folder}.");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Install failed", ex);
            return $"Couldn't install: {ex.Message}";
        }
    }

    /// <summary>
    /// Elevated. Puts back what Wattkin changed (Windows power settings, a disabled NVIDIA GPU, the
    /// charge limit and the fans) and removes it, including its WMI classes.
    /// </summary>
    public static void Uninstall(bool removeData)
    {
        StopRunningCopy();
        Step("restore Windows settings", () =>
        {
            new SettingsStore().Update(WindowsPower.RestoreOriginals);
            global::Wattkin.Hardware.DiscreteGpu.Enable();
        });
        Step("laptop controller", ResetHardware);
        Step("WMI classes", global::Wattkin.Hardware.WmiSchema.Remove); // after the controller, which needs them
        Step("screen", () =>
        {
            // The running copy put the normal picture back as it exited; only the range setting is left.
            // The flag goes too, so a reinstall's uninstall doesn't remove a value someone else set since.
            var store = new SettingsStore();
            if (store.Current.ScreenGammaRangeSet)
            {
                GammaRamp.RemoveFullRange();
                store.Update(s => s.ScreenGammaRangeSet = false);
            }
        });
        Step("logon task", () => StartupTask.Disable());
        Step("shortcuts", () =>
        {
            File.Delete(Shortcut);
            File.Delete(LegacyShortcut);
        });
        Step("Apps entry", () => Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false));
        if (removeData)
        {
            Step("settings and logs", () =>
            {
                foreach (var dir in new[] { Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData }.Select(f => Path.Combine(Environment.GetFolderPath(f), "Wattkin")))
                {
                    if (Directory.Exists(dir))
                    {
                        Directory.Delete(dir, recursive: true);
                    }
                }
            });
        }

        Step("program files", () =>
        {
            if (RunningInstalled)
            {
                // This exe is the one being removed: delete the folder once it has exited, and the copy
                // that launched it (waiting for it, non-elevated) too. A fixed two-second wait left the
                // exe behind whenever the "Wattkin has been removed" message was read for longer.
                var waitFor = string.Join(",", new[] { Environment.ProcessId, ParentWattkin() ?? 0 }.Where(id => id > 0));
                var folder = Folder.Replace("'", "''", StringComparison.Ordinal);
                var script = $"Wait-Process -Id {waitFor} -Timeout 600 -ErrorAction SilentlyContinue; Start-Sleep -Milliseconds 500; Remove-Item -LiteralPath '{folder}' -Recurse -Force -ErrorAction SilentlyContinue";
                // By full path, from System32: elevated, a powershell.exe in the current folder mustn't be the one run
                // (and a current folder inside Program Files\Wattkin would keep it from being removed).
                var powershell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
                Process.Start(new ProcessStartInfo(powershell, $"-NoProfile -NonInteractive -WindowStyle Hidden -Command \"{script}\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WorkingDirectory = Environment.SystemDirectory,
                });
            }
            else if (Directory.Exists(Folder))
            {
                Directory.Delete(Folder, recursive: true);
            }
        });
    }

    /// <summary>
    /// Leaves the laptop as it came: charging to 100%, not running from battery on the charger, and the
    /// fans on Auto with the factory fan table back in the controller.
    /// </summary>
    private static void ResetHardware()
    {
        using var acpi = global::Wattkin.Hardware.MsiAcpi.TryOpen(out _);
        if (acpi is null)
        {
            return;
        }

        var ec = new global::Wattkin.Hardware.EmbeddedController(acpi);
        if (!ec.CanWrite)
        {
            return;
        }

        ec.SetSimulatedUnplug(false);
        ec.SetChargeLimit(100);
        ec.SetCoolerBoost(false);
        var settings = new SettingsStore(readOnly: true).Current;
        if (settings.FactoryFanTable is { } table && ec.HasFullFanTable)
        {
            ec.WriteFullFanTable(table, unrestricted: true); // what the laptop held before Wattkin, whatever it was
        }
        else if (settings.FactoryFanCurve is { } factory && ec.HasFanCurves)
        {
            ec.WriteFanTable(factory);
        }

        ec.SetFanMode(global::Wattkin.Hardware.FanMode.Auto);
    }

    /// <summary>
    /// Elevated, from the installed copy. Writes a verified update next to the exe and swaps it in;
    /// the caller then restarts. Returns an error message or null.
    /// </summary>
    public static string? StageUpdate(byte[] exe, Version version)
    {
        var staged = Exe + ".new";
        try
        {
            File.WriteAllBytes(staged, exe);
            if (!Releases.TryParseVersion(FileVersionInfo.GetVersionInfo(staged).ProductVersion, out var fileVersion) || fileVersion != Releases.Normalize(version))
            {
                File.Delete(staged);
                return "The downloaded file's version doesn't match the release, so it wasn't installed.";
            }

            return Swap(staged);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(staged);
            return $"Couldn't write the update: {ex.Message}";
        }
    }

    /// <summary>Puts the previous exe back after an update that couldn't start.</summary>
    public static void RollBack()
    {
        var old = Exe + ".old";
        if (File.Exists(old))
        {
            File.Move(old, Exe, overwrite: true);
        }
    }

    /// <summary>At startup of the installed copy: tidy up after an update and keep the Apps entry current.</summary>
    public static void AfterStart()
    {
        if (!RunningInstalled || !Elevation.IsElevated)
        {
            return;
        }

        TryDelete(Exe + ".old");
        TryDelete(Exe + ".new");
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(UninstallKey);
            if (key?.GetValue("DisplayVersion") as string != CurrentVersion.ToString(3))
            {
                Register();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Warn($"Couldn't update the Apps entry: {ex.Message}");
        }
    }

    public static Version CurrentVersion => Releases.Normalize(typeof(Installation).Assembly.GetName().Version ?? new Version(0, 0, 0));

    /// <summary>Running exes can be renamed but not overwritten: move the old one aside, then the new one in.</summary>
    private static string? Swap(string staged)
    {
        var old = Exe + ".old";
        TryDelete(old);
        var hadOld = File.Exists(Exe);
        if (hadOld)
        {
            File.Move(Exe, old);
        }

        try
        {
            File.Move(staged, Exe);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (hadOld)
            {
                File.Move(old, Exe, overwrite: true);
            }

            return $"Couldn't replace Wattkin.exe: {ex.Message}";
        }
    }

    /// <summary>Asks a running copy to exit cleanly (it releases the hardware first), then makes sure it has.</summary>
    public static void StopRunningCopy()
    {
        // Only the installed copy: the window that launched this installer is a "Wattkin" too, and so is
        // the non-elevated copy waiting for this elevated one (e.g. "--uninstall" from Settings › Apps),
        // which used to be killed after the 10-second wait.
        var self = Environment.ProcessId;
        var parent = ParentProcessId();
        Process[] Others() => [.. Process.GetProcessesByName("Wattkin").Where(p => p.Id != self && p.Id != parent && IsInstalledCopy(p))];
        if (Others().Length == 0)
        {
            return;
        }

        SingleInstance.RequestExit(App.InstanceName);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Others().Length > 0 && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(200);
        }

        foreach (var p in Others())
        {
            try
            {
                p.Kill();
                p.WaitForExit(3000);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
        }
    }

    /// <summary>The installed Wattkin that started this one and waits for it, or null (started by Windows' Settings, a script…).</summary>
    private static int? ParentWattkin()
    {
        try
        {
            return ParentProcessId() is { } id && IsInstalledCopy(Process.GetProcessById(id)) ? id : null;
        }
        catch (ArgumentException)
        {
            return null; // already gone
        }
    }

    /// <summary>The process that started this one (UAC elevation keeps the launcher as the parent), or null.</summary>
    private static int? ParentProcessId()
    {
        var info = default(Native.NtDll.ProcessBasicInformation);
        return Native.NtDll.NtQueryInformationProcess(Process.GetCurrentProcess().Handle, Native.NtDll.ProcessBasicInformationClass, ref info, System.Runtime.InteropServices.Marshal.SizeOf<Native.NtDll.ProcessBasicInformation>(), out _) == 0
            ? (int)info.InheritedFromUniqueProcessId
            : null;
    }

    private static bool IsInstalledCopy(Process process)
    {
        try
        {
            return string.Equals(process.MainModule?.FileName, Exe, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false; // exited, or not ours to inspect
        }
    }

    private static void CreateShortcut()
    {
        if (Type.GetTypeFromProgID("WScript.Shell") is not { } type)
        {
            return;
        }

        dynamic shell = Activator.CreateInstance(type)!;
        try
        {
            var link = shell.CreateShortcut(Shortcut);
            link.TargetPath = Exe;
            link.WorkingDirectory = Folder;
            link.Description = "Laptop power, battery and hardware control";
            link.Save();
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }

        TryDelete(LegacyShortcut);
    }

    private static void Register()
    {
        using var key = Registry.LocalMachine.CreateSubKey(UninstallKey);
        key.SetValue("DisplayName", "Wattkin");
        key.SetValue("DisplayVersion", CurrentVersion.ToString(3));
        key.SetValue("Publisher", "Ethan Copping");
        key.SetValue("DisplayIcon", Exe);
        key.SetValue("InstallLocation", Folder);
        key.SetValue("UninstallString", $"\"{Exe}\" --uninstall");
        key.SetValue("QuietUninstallString", $"\"{Exe}\" --uninstall --quiet");
        key.SetValue("URLInfoAbout", RepositoryUrl);
        key.SetValue("HelpLink", RepositoryUrl + "/issues");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("EstimatedSize", (int)Math.Max(1, new FileInfo(Exe).Length / 1024), RegistryValueKind.DWord);
    }

    private static void Step(string name, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Error($"Uninstall step '{name}' failed", ex);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
