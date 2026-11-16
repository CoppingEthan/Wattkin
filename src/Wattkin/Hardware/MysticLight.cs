using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Wattkin.Native;

namespace Wattkin.Hardware;

/// <summary>A keyboard light colour; settings.json keeps it as "#RRGGBB".</summary>
public readonly record struct LightColor(byte R, byte G, byte B)
{
    public static readonly LightColor White = new(0xFF, 0xFF, 0xFF);

    public string Hex => $"#{R:X2}{G:X2}{B:X2}";

    /// <summary>"#RRGGBB" (the # optional), else null.</summary>
    public static LightColor? Parse(string? text)
    {
        var digits = text?.Trim().TrimStart('#');
        return digits is { Length: 6 } && uint.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var rgb)
            ? new LightColor((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)
            : null;
    }

    public override string ToString() => Hex;
}

/// <summary>What Windows reports about a Mystic Light controller, without opening it for writing.</summary>
public sealed record MysticLightInfo(string Path, string Product, ushort UsagePage, int FeatureReportLength)
{
    /// <summary>The 24-zone MS-1603 (Vector 16 HX AI): its own usage page and packets (<see cref="MysticLight.Packet1603"/>).</summary>
    public bool Is1603 => Path.Contains("vid_1462&pid_1603", StringComparison.OrdinalIgnoreCase);

    /// <summary>The report layout the protocol needs: 64-byte feature reports on MSI's usage page (0x00FF; 0xFF00 on the MS-1603).</summary>
    public bool Usable => FeatureReportLength == MysticLight.ReportLength && UsagePage == (Is1603 ? MysticLight.UsagePage1603 : MysticLight.UsagePage);

    /// <summary>
    /// This controller lights the keys, not the EC's register: the Stealth 15M A11UEK's 1462:1562 (seen to
    /// be so), or another Mystic Light controller where the EC's map has no keyboard register. Where it
    /// has one, a 1563/1564 controller could be lighting something else (a light bar), so the EC's
    /// keyboard path stays. The app and the self-test both decide by this.
    /// </summary>
    public bool LightsTheKeys(bool ecHasKeyboardLight) =>
        Usable && (Path.Contains("pid_1562", StringComparison.OrdinalIgnoreCase) || !ecHasKeyboardLight);
}

/// <summary>
/// The keyboard light on MSI laptops whose keys are lit by a "MysticLight" USB controller
/// instead of the embedded controller. On the Stealth 15M A11UEK it is 1462:1562 ("MysticLight
/// MS-1562", single-zone RGB): EC register 0xD3 takes and reads back the levels (self-test 1.2.0) but
/// nothing is wired to it, which is why neither Wattkin's EC path nor YAMDCC lit the keys, while
/// OpenRGB did (2026-10-21).
///
/// Protocol: one 64-byte HID feature report, ID 0x02: 0x00, mode (0 off, 1 steady; 2–6 are MSI's
/// effects), speed (0–2), brightness (0–10), number of colours, then seven RGB colours. Source: OpenRGB's
/// MSIMysticLight64Controller (detected for 1462:1562, 1563 and 1564), checked against MSI Center's own
/// USB captures on an Alpha 15 (1462:1563, OpenRGB issue 2289): a steady colour is sent as one colour
/// followed by MSI's default palette in the six other slots, brightness steps are 10, 8, 6, 4 and 2
/// (its minimum), and off is mode 0 with the rest unchanged. Wattkin sends those packets byte for byte.
/// The owner's controller declares the same reports (usage page 0x00FF, 64-byte feature report 0x02).
///
/// For the MS-1603, MSI Center uses a different pair of reports (zone mask, then colour),
/// verified on a 24-zone Vector 16 by MSI-KeyLight. Its levels here use RGB scaling, not a
/// verified hardware brightness opcode. Changing a level replaces multi-zone lighting with
/// one static colour. Do not assume this is safe to run concurrently with MSI Center/OpenRGB.
///
/// The older 156x controller doesn't report its state (a GET_FEATURE of report 0x02 returns zeros), so Wattkin
/// keeps the level and colour in settings.json and sends them again at startup and after sleep. No
/// administrator rights or driver needed: Windows' own HID class driver carries the report.
/// </summary>
internal sealed class MysticLight : IDisposable
{
    public const ushort UsagePage = 0x00FF;
    public const ushort UsagePage1603 = 0xFF00;
    public const int ReportLength = 64;
    public const int MaxLevel = 3;

    private const byte ReportId = 0x02;
    private const byte ModeOff = 0;
    private const byte ModeSteady = 1;

    /// <summary>The product IDs OpenRGB drives with this protocol (MSIMysticLightControllerDetect.cpp).</summary>
    private static readonly string[] HardwareIds = ["vid_1462&pid_1562", "vid_1462&pid_1563", "vid_1462&pid_1564", "vid_1462&pid_1603"];

    /// <summary>
    /// Off, Low, Mid, High: Low is MSI Center's lowest step, Mid its middle one, High full brightness.
    /// Off is mode 0 with the brightness left at full, as in MSI Center's capture.
    /// </summary>
    private static readonly byte[] LevelBrightness = [10, 2, 6, 10];

    /// <summary>What MSI Center sends in the six colour slots a steady colour doesn't use (its default palette).</summary>
    private static readonly byte[] MsiPalette = [0xFF, 0x50, 0x00, 0xFF, 0xFF, 0x00, 0x00, 0xFF, 0x00, 0x00, 0x00, 0xFF, 0x96, 0x00, 0xFF, 0xFF, 0x00, 0xFF];

    private readonly SafeFileHandle _handle;
    private readonly object _gate = new();

    private MysticLight(SafeFileHandle handle, MysticLightInfo info)
    {
        _handle = handle;
        Info = info;
    }

    public MysticLightInfo Info { get; }

    /// <summary>The 64 bytes for a level (0 off … <see cref="MaxLevel"/>) in one steady colour, as MSI Center sends them.</summary>
    public static byte[] Packet(int level, LightColor color)
    {
        level = Math.Clamp(level, 0, MaxLevel);
        var packet = new byte[ReportLength];
        packet[0] = ReportId;
        packet[2] = level == 0 ? ModeOff : ModeSteady;
        packet[4] = LevelBrightness[level];
        packet[5] = 1;
        packet[6] = color.R;
        packet[7] = color.G;
        packet[8] = color.B;
        MsiPalette.CopyTo(packet, 9);
        return packet;
    }

    /// <summary>
    /// MS-1603 (24-zone RGB): MSI Center selects zones with one feature report and applies a
    /// colour with a second. This is deliberately a separate packet format from MS-1562/3/4.
    /// Source: MSI-KeyLight's MSI Center USB HID captures for 1462:1603.
    ///
    /// The current UI controls one colour for the entire keyboard. Select all 24 zones with
    /// mask 0x00FFFFFF. For Off/Low/Mid/High, scale RGB rather than guessing which MS-1603
    /// field represents hardware brightness; this replaces any prior per-zone colour scheme.
    /// </summary>
    public static (byte[] Mask, byte[] Color) Packet1603(int level, LightColor color)
    {
        level = Math.Clamp(level, 0, MaxLevel);
        var mask = new byte[ReportLength];
        mask[0] = ReportId;
        mask[1] = 0x01;
        mask[2] = 0xFF;
        mask[3] = 0xFF;
        mask[4] = 0xFF;

        var data = new byte[ReportLength];
        byte[] prefix = [0x02, 0x02, 0x01, 0x58, 0x02, 0x00, 0x32, 0x08, 0x01, 0x01, 0x00];
        prefix.CopyTo(data, 0);
        // Field at offset 14 is 0x64 in MSI Center's captured steady-colour report.
        data[14] = 0x64;
        var scale = level switch { 0 => 0, 1 => 33, 2 => 67, _ => 100 };
        static byte Scaled(byte channel, int percent) => (byte)((channel * percent + 50) / 100);
        data[11] = data[15] = Scaled(color.R, scale);
        data[12] = data[16] = Scaled(color.G, scale);
        data[13] = data[17] = Scaled(color.B, scale);
        return (mask, data);
    }

    /// <summary>
    /// The Mystic Light controller Windows knows of, read without opening it for writing (a usable
    /// interface first: a device can have several); null when there's none.
    /// </summary>
    public static MysticLightInfo? Find()
    {
        var found = Candidates().Select(Describe).OfType<MysticLightInfo>().ToList();
        return found.FirstOrDefault(i => i.Usable) ?? found.FirstOrDefault();
    }

    /// <summary>Opens the controller for writing. Null when there's none (error null) or it can't be used (error says why).</summary>
    public static MysticLight? Open(out string? error)
    {
        error = null;
        if (Find() is not { } info)
        {
            return null;
        }

        if (!info.Usable)
        {
            error = $"{info.Product} doesn't declare the reports Wattkin knows (usage page 0x{info.UsagePage:X4}, feature report {info.FeatureReportLength} bytes).";
            return null;
        }

        var handle = Kernel32.CreateFile(info.Path, Kernel32.GENERIC_READ | Kernel32.GENERIC_WRITE, Kernel32.FILE_SHARE_READ | Kernel32.FILE_SHARE_WRITE, IntPtr.Zero, Kernel32.OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            error = $"{info.Product} couldn't be opened (error {Marshal.GetLastWin32Error()}).";
            handle.Dispose();
            return null;
        }

        return new MysticLight(handle, info);
    }

    /// <summary>Sends a level and colour. False when the controller refused it or went away (reopen it then).</summary>
    /// <param name="log">False for auto-off's frequent switching: then only a failure is logged.</param>
    public bool Set(int level, LightColor color, bool log = true)
    {
        var is1603 = Info.Is1603;
        var packet = is1603 ? null : Packet(level, color);
        bool ok;
        int error;
        lock (_gate)
        {
            if (_handle.IsClosed)
            {
                ok = false;
                error = 6; // ERROR_INVALID_HANDLE
            }
            else if (is1603)
            {
                var (mask, data) = Packet1603(level, color);
                // Keep the selection + write together: another thread must not interleave them.
                ok = Hid.HidD_SetFeature(_handle, mask, mask.Length);
                if (ok)
                {
                    ok = Hid.HidD_SetFeature(_handle, data, data.Length);
                }
                error = ok ? 0 : Marshal.GetLastWin32Error();
            }
            else
            {
                ok = Hid.HidD_SetFeature(_handle, packet!, packet!.Length);
                error = ok ? 0 : Marshal.GetLastWin32Error();
            }
        }

        if (log || !ok)
        {
            Log.Info($"Keyboard light ({Info.Product}): {(level <= 0 ? "off" : $"level {level}, {color.Hex}")} -> {(ok ? "ok" : $"failed (error {error})")}");
        }

        return ok;
    }

    /// <summary>HID interfaces whose path names one of <see cref="HardwareIds"/>.</summary>
    private static List<string> Candidates()
    {
        var paths = new List<string>();
        var set = SetupApi.SetupDiGetClassDevs(Hid.GUID_DEVINTERFACE_HID, null, IntPtr.Zero, SetupApi.DIGCF_PRESENT | SetupApi.DIGCF_DEVICEINTERFACE);
        if (set == new IntPtr(-1))
        {
            return paths;
        }

        try
        {
            var data = new SetupApi.SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SetupApi.SP_DEVICE_INTERFACE_DATA>() };
            for (var index = 0; SetupApi.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, Hid.GUID_DEVINTERFACE_HID, index, ref data); index++)
            {
                if (InterfacePath(set, ref data) is { } path && HardwareIds.Any(id => path.Contains(id, StringComparison.OrdinalIgnoreCase)))
                {
                    paths.Add(path);
                }
            }
        }
        finally
        {
            SetupApi.SetupDiDestroyDeviceInfoList(set);
        }

        return paths;
    }

    private static string? InterfacePath(IntPtr set, ref SetupApi.SP_DEVICE_INTERFACE_DATA data)
    {
        SetupApi.SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out var required, IntPtr.Zero);
        if (required <= 0)
        {
            return null;
        }

        var detail = Marshal.AllocHGlobal(required);
        try
        {
            // SP_DEVICE_INTERFACE_DETAIL_DATA_W.cbSize is 8 on x64 (DWORD + one WCHAR, padded).
            Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
            return SetupApi.SetupDiGetDeviceInterfaceDetail(set, ref data, detail, required, out _, IntPtr.Zero)
                ? Marshal.PtrToStringUni(detail + 4)
                : null;
        }
        finally
        {
            Marshal.FreeHGlobal(detail);
        }
    }

    /// <summary>Product name and report layout, through a handle opened with no access (queries only).</summary>
    private static unsafe MysticLightInfo? Describe(string path)
    {
        using var handle = Kernel32.CreateFile(path, 0, Kernel32.FILE_SHARE_READ | Kernel32.FILE_SHARE_WRITE, IntPtr.Zero, Kernel32.OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle.IsInvalid || !Hid.HidD_GetPreparsedData(handle, out var preparsed))
        {
            return null;
        }

        try
        {
            if (Hid.HidP_GetCaps(preparsed, out var caps) != Hid.HIDP_STATUS_SUCCESS)
            {
                return null;
            }

            const int Chars = 128;
            var buffer = stackalloc char[Chars];
            var text = Hid.HidD_GetProductString(handle, buffer, Chars * sizeof(char)) ? new ReadOnlySpan<char>(buffer, Chars) : [];
            var product = text.IndexOf('\0') is var end and >= 0 ? text[..end].ToString() : text.ToString();
            return new MysticLightInfo(path, product.Length > 0 ? product : "Mystic Light", caps.UsagePage, caps.FeatureReportByteLength);
        }
        finally
        {
            Hid.HidD_FreePreparsedData(preparsed);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _handle.Dispose();
        }
    }
}
