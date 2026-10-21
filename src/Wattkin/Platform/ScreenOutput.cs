using System.Management;
using Microsoft.Win32;
using Wattkin.Native;

namespace Wattkin.Platform;

/// <summary>
/// The built-in panel's backlight: the brightness Windows' own slider and the Fn keys set, through
/// Windows' WMI monitor classes (root\WMI WmiMonitorBrightness / WmiMonitorBrightnessMethods). Only
/// panels whose driver supports it (laptop screens) have one; external monitors don't.
/// </summary>
internal static class Backlight
{
    /// <summary>The panel's brightness in percent, or null when there's no backlight Windows controls.</summary>
    public static int? Get()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness WHERE Active = TRUE");
            using var results = searcher.Get();
            foreach (ManagementObject item in results)
            {
                using (item)
                {
                    if (item["CurrentBrightness"] is byte value)
                    {
                        return value;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    /// <summary>Sets the panel's brightness (0–100%), as Windows' slider does. False when there's no backlight to set.</summary>
    public static bool Set(int percent)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods WHERE Active = TRUE");
            using var results = searcher.Get();
            foreach (ManagementObject item in results)
            {
                using (item)
                {
                    // Timeout (s) = 1, as Windows' own callers pass; Brightness = 0–100.
                    item.InvokeMethod("WmiSetBrightness", [(uint)1, (byte)Math.Clamp(percent, 0, 100)]);
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            Log.Warn($"Setting the screen brightness failed: {ex.Message}");
        }

        return false;
    }
}

/// <summary>
/// Darkens what a display shows through its gamma ramp (the colour lookup table GDI hands the graphics
/// driver), the way f.lux dims: below the backlight's lowest setting, and on external monitors too. It
/// saves no power (the backlight stays as it is) and HDR displays ignore or refuse it.
///
/// A ramp isn't always straight: a colour calibration (an ICC profile's curve, MSI True Color) lives in
/// it too. So the display's own ramp is read before the first dim, the dim scales that one, and
/// un-dimming puts it back, never a plain straight ramp in its place.
///
/// Windows refuses ramps far from normal unless HKLM\…\ICM\GdiICMGammaRange is 256 (f.lux sets it when
/// it installs); <see cref="AllowFullRange"/> lifts the limit from the next sign-in.
/// </summary>
internal static class GammaRamp
{
    private const string IcmKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ICM";
    private const string RangeValue = "GdiICMGammaRange";
    public const int Length = 3 * 256;

    /// <summary>Windows' own straight ramp, for a display whose ramp couldn't be read.</summary>
    public static ushort[] Linear() => Scale(null, 100);

    /// <summary>
    /// <paramref name="baseRamp"/> (red, green, blue: 3 × 256; null = straight) at <paramref name="percent"/>
    /// of its brightness.
    /// </summary>
    public static ushort[] Scale(ushort[]? baseRamp, int percent)
    {
        var factor = Math.Clamp(percent, 1, 100) / 100.0;
        var ramp = new ushort[Length];
        for (var i = 0; i < Length; i++)
        {
            var value = baseRamp is { Length: Length } ? baseRamp[i] : (i % 256) * 257;
            ramp[i] = (ushort)Math.Round(value * factor);
        }

        return ramp;
    }

    /// <summary>
    /// A ramp a display could really have had: three channels that never fall. Saved ramps come back
    /// from settings.json, which the user can edit, and must not end up on the screen otherwise.
    /// </summary>
    public static bool Plausible(ushort[]? ramp)
    {
        if (ramp is not { Length: Length })
        {
            return false;
        }

        for (var channel = 0; channel < 3; channel++)
        {
            for (var i = channel * 256 + 1; i < (channel + 1) * 256; i++)
            {
                if (ramp[i] < ramp[i - 1])
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>Within 1% everywhere: drivers round a ramp a little when they take it.</summary>
    public static bool Close(ushort[] a, ushort[] b) => a.Length == b.Length && !a.Zip(b).Any(v => Math.Abs(v.First - v.Second) > 655);

    /// <summary>The displays Windows draws on, by GDI name (\\.\DISPLAY1 …).</summary>
    public static IReadOnlyList<string> Displays() => [.. System.Windows.Forms.Screen.AllScreens.Select(s => s.DeviceName)];

    /// <summary>
    /// Which monitor a GDI name stands for now (its interface path, e.g. \\?\DISPLAY#BOE0A8F#…), or null
    /// when Windows can't say. A GDI name is reused: after undocking, \\.\DISPLAY2 can be another monitor.
    /// </summary>
    public static unsafe string? MonitorId(string display)
    {
        var info = new User32.DisplayDevice { Size = sizeof(User32.DisplayDevice) };
        if (!User32.EnumDisplayDevices(display, 0, ref info, User32.EDD_GET_DEVICE_INTERFACE_NAME))
        {
            return null;
        }

        var id = new string(info.DeviceId, 0, 128);
        var end = id.IndexOf('\0');
        id = end >= 0 ? id[..end] : id;
        return id.Length > 0 ? id : null;
    }

    /// <summary>A display's ramp now, or null when it can't be read.</summary>
    public static unsafe ushort[]? Read(string display)
    {
        var dc = Gdi32.CreateDC(display, null, null, IntPtr.Zero);
        if (dc == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var ramp = new ushort[Length];
            fixed (ushort* p = ramp)
            {
                return Gdi32.GetDeviceGammaRamp(dc, p) ? ramp : null;
            }
        }
        finally
        {
            Gdi32.DeleteDC(dc);
        }
    }

    /// <summary>Sets a display's ramp. False when Windows or the driver refused it (too far from normal, HDR).</summary>
    public static unsafe bool Write(string display, ushort[] ramp)
    {
        var dc = Gdi32.CreateDC(display, null, null, IntPtr.Zero);
        if (dc == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            fixed (ushort* p = ramp)
            {
                return Gdi32.SetDeviceGammaRamp(dc, p);
            }
        }
        finally
        {
            Gdi32.DeleteDC(dc);
        }
    }

    /// <summary>
    /// One display at <paramref name="percent"/> of <paramref name="baseRamp"/>; if refused, 5% brighter
    /// at a time up to normal. The level it shows, or null when it took nothing at all.
    /// </summary>
    public static int? Dim(string display, ushort[]? baseRamp, int percent)
    {
        for (var level = Math.Clamp(percent, 1, 100); ; level = Math.Min(100, level + 5))
        {
            if (Write(display, Scale(baseRamp, level)))
            {
                return level;
            }

            if (level >= 100)
            {
                return null;
            }
        }
    }

    /// <summary>Settings keep a ramp as base64 of its 1536 bytes.</summary>
    public static string Encode(ushort[] ramp)
    {
        var bytes = new byte[Length * 2];
        Buffer.BlockCopy(ramp, 0, bytes, 0, bytes.Length);
        return Convert.ToBase64String(bytes);
    }

    /// <summary>A saved ramp, or null when it's damaged or implausible.</summary>
    public static ushort[]? Decode(string? text)
    {
        try
        {
            var bytes = Convert.FromBase64String(text ?? "");
            if (bytes.Length != Length * 2)
            {
                return null;
            }

            var ramp = new ushort[Length];
            Buffer.BlockCopy(bytes, 0, ramp, 0, bytes.Length);
            return Plausible(ramp) ? ramp : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Windows still limits how dark a ramp may be.</summary>
    public static bool Limited()
    {
        using var key = Registry.LocalMachine.OpenSubKey(IcmKey);
        return key?.GetValue(RangeValue) is not 256;
    }

    /// <summary>Elevated: lets ramps go all the way down (from the next sign-in). True when Wattkin set it.</summary>
    public static bool AllowFullRange()
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(IcmKey, writable: true);
            key.SetValue(RangeValue, 256, RegistryValueKind.DWord);
            Log.Info("Screen extra dim: allowed Windows' full gamma range (GdiICMGammaRange = 256), from the next sign-in.");
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Log.Warn($"Couldn't allow the full gamma range: {ex.Message}");
            return false;
        }
    }

    /// <summary>Uninstall: takes back the full-range setting, only when Wattkin set it.</summary>
    public static void RemoveFullRange()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(IcmKey, writable: true);
            key?.DeleteValue(RangeValue, throwOnMissingValue: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Log.Warn($"Couldn't remove the gamma range setting: {ex.Message}");
        }
    }

}
