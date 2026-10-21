using Wattkin.Monitoring;

namespace Wattkin.Hardware;

/// <summary>
/// EC performance mode (MSI "shift mode"): the firmware's CPU power limits. Mapped to each model's
/// register values; from the lowest limits to the highest: Eco, Balanced, Sport, Turbo.
/// </summary>
public enum ShiftMode
{
    /// <summary>MSI "Super Battery" (0xC2 on the standard layout).</summary>
    Eco,
    /// <summary>MSI "Balanced" / "Silent" (msi-ec "comfort"; 0xC1).</summary>
    Balanced,
    /// <summary>MSI's "Sport" (msi-ec "sport", YAMDCC "Balanced"; 0xC0), between Balanced and Turbo. Not every laptop has it.</summary>
    Sport,
    /// <summary>MSI "Extreme Performance" (0xC4).</summary>
    Turbo,
}

/// <summary>EC fan mode.</summary>
public enum FanMode
{
    Auto,
    Silent,
    /// <summary>
    /// The controller's own fan table (<see cref="FanCurve"/>), MSI Center's "advanced" mode. Wattkin
    /// switches to it only with a table that passes the safety rules: a fan profile's speeds at the
    /// firmware's temperature steps, or, where the map names the table's registers, a whole
    /// <see cref="FanTable"/> with its own temperatures.
    /// </summary>
    Custom,
}

public enum EcSupport
{
    /// <summary>Register map verified on Windows on this exact firmware family.</summary>
    Verified,
    /// <summary>Firmware covered by msi-ec's per-model map (tested on Linux); passed the sanity check here.</summary>
    Community,
    /// <summary>Not in any table, but uses MSI's standard WMI v2 layout and passed the sanity check here.</summary>
    Standard,
    /// <summary>Old firmware interface, or registers that don't look like any known layout: monitoring only.</summary>
    Unknown,
}

/// <summary>
/// Typed access to an MSI laptop's embedded controller.
///
/// The register map comes from, in order: the verified map for this firmware family, msi-ec's per-model
/// map (<see cref="EcModels"/>), or MSI's standard WMI v2 layout (the addresses MSI Center itself uses
/// on every WMI v2 notebook). Before any write is allowed, a sanity check reads the battery-limit and
/// performance-mode registers and requires values that only make sense for that layout; anything
/// else leaves the laptop on monitoring only. On MSI's standard layout, writes follow MSI's own
/// bit-level semantics; other msi-ec maps use msi-ec's values. Shared registers are read-modify-write.
///
/// The verified map (EC 1594EMS1.*, Prestige 16 Studio) was cross-checked three ways: MSI Center's call
/// log on the laptop, MSI's decompiled code, and msi-ec CONF_G2_1 (tested on 1594EMS1.113, PR #797).
/// The Stealth 15M A11UEK's (EC 1563EMS1.115) comes from YAMDCC's configuration and passed the hardware
/// self-test (<see cref="SelfTest"/>) on that laptop: every control, whole fan tables and their hysteresis.
/// </summary>
public sealed class EmbeddedController : IEcSensorSource
{
    public const int MinChargeLimit = 10;
    public const int MaxChargeLimit = 100;

    // MSI's generic WMI v2 registers (hard-coded in MSI Center for all such notebooks).
    private const byte RegAdapter = 0xBF;          // bit0 = simulate unplugged (battery discharges on AC)
    private const byte RegHostFlags = 0xD9;        // bit0 = vendor software present, bit1 = Fn lock
    private const byte RegCapabilities = 0x2F;     // bit1 = webcam switch supported, bit6 = GPU switch supported

    // The Prestige 16 Studio's family, and the Stealth 15M A11UEK's exact firmware (its self-test passed
    // every check, 2026-10-21; the rest of its msi-ec family keeps msi-ec's map).
    private static readonly string[] VerifiedFirmwarePrefixes = ["1594EMS1.", "1563EMS1.115"];

    private readonly MsiAcpi _acpi;
    private readonly EcModel? _model;
    private readonly bool _shiftOk;
    private readonly bool _fanOk;
    private readonly bool _sportSeen;

    // One fan-mode or table write at a time, from any thread: a whole table takes up to ~150 firmware
    // calls, and a second write starting part-way through would take the half-written table as the one
    // to roll back to.
    private readonly object _fanWrite = new();

    /// <param name="sportSeen">
    /// This laptop has been seen in MSI's Sport mode (0xC0) by itself, so its firmware has one: Wattkin
    /// then offers it on MSI's own layout, where the map doesn't name it.
    /// </param>
    public EmbeddedController(MsiAcpi acpi, bool sportSeen = false)
    {
        _acpi = acpi;
        _sportSeen = sportSeen;
        var listed = EcModels.Find(acpi.EcVersion);
        var verified = VerifiedFirmwarePrefixes.Any(p => acpi.EcVersion.StartsWith(p, StringComparison.OrdinalIgnoreCase));
        var candidate = listed ?? (acpi.WmiMajor == 2 ? EcModels.MsiStandardWmi2 : null);

        // Writes only on WMI v2, the version MSI's register access is known for. v3 (newer laptops) goes
        // through different calls in MSI's own library, so it stays read-only until someone reports one.
        var why = "";
        if (acpi.WmiMajor == 2 && candidate is not null && Sane(candidate, out _shiftOk, out _fanOk, out why))
        {
            _model = candidate;
            Support = verified ? EcSupport.Verified : listed is not null ? EcSupport.Community : EcSupport.Standard;
            CanWrite = true;
        }
        else
        {
            _model = candidate;
            Support = EcSupport.Unknown;
            CanWrite = false;
            Reason = acpi.WmiMajor switch
            {
                < 2 => $"This laptop uses MSI's older firmware interface (version {acpi.WmiMajor}.{acpi.WmiMinor}), which Wattkin can't control yet. Monitoring works.",
                > 2 => $"This laptop uses a newer version of MSI's firmware interface ({acpi.WmiMajor}.{acpi.WmiMinor}) that Wattkin hasn't been tested with, so it only monitors for now. A report from \"Help support this laptop\" would help add it.",
                _ => candidate is null ? "No register map for this firmware." : $"The registers didn't look like {candidate.Source} ({why}), so Wattkin stays read-only.",
            };
            Log.Warn($"EC {acpi.EcVersion}: controls disabled. {Reason}");
        }
    }

    /// <summary>
    /// The battery-limit register must look like one (<see cref="ChargeRegisterLooksRight"/>) and, if the
    /// map has one, the performance-mode register must hold a known value (<see cref="ShiftRegisterLooksRight"/>).
    /// Fan mode is checked separately so an odd fan value only disables fan control.
    /// </summary>
    private bool Sane(EcModel model, out bool shiftOk, out bool fanOk, out string why)
    {
        shiftOk = fanOk = false;
        why = "";
        var raw = model.ChargeAddress is { } charge ? _acpi.ReadEc(charge) : null;
        if (raw is not { } limit || !ChargeRegisterLooksRight(limit))
        {
            why = $"battery-limit register {Hex(raw)}";
            return false;
        }

        if (model.ShiftAddress is { } shift)
        {
            var value = _acpi.ReadEc(shift);
            ShiftAtStart = value;
            shiftOk = value is { } v && ShiftRegisterLooksRight(model, v);
            if (!shiftOk)
            {
                why = $"performance-mode register {Hex(value)}";
                return false;
            }
        }

        if (model.FanModeAddress is { } fan && _acpi.ReadEc(fan) is { } f)
        {
            fanOk = f == model.FanAuto || f == model.FanSilent || f == model.FanAdvanced || (UsesMsiLayout(model) && (f & 0x6F) == 0x0D);
        }

        return true;
    }

    /// <summary>
    /// The battery-limit register on WMI v2: bit 7 is the firmware's own flag (MSI Center keeps it as it
    /// finds it and never sets it), bits 0–6 the stop percentage, 10–100. After the controller loses all
    /// power (battery swapped or run flat) it comes back as 0x80, the flag with no limit set yet (seen on
    /// the Prestige 16 Studio after a battery swap); Wattkin then sets the user's limit.
    /// </summary>
    public static bool ChargeRegisterLooksRight(byte raw) =>
        (raw & 0x80) != 0 && (raw & 0x7F) is 0 or (>= MinChargeLimit and <= MaxChargeLimit);

    /// <summary>
    /// One of the map's performance modes, or 0x80 (none chosen yet). On MSI's own layout also 0xC0, MSI's
    /// "Sport" mode: in MSI Center's code it sits between Comfort (0xC1) and Turbo (0xC4), and the
    /// Katana GF66 12UG (EC 1583EMS1.111) reports it. Not on a family where 0xC0 may be turbo instead
    /// (<see cref="EcModel.MsiSportUnclear"/>): a laptop found there stays read-only, as before.
    /// </summary>
    public static bool ShiftRegisterLooksRight(EcModel model, byte value) =>
        value == model.ShiftEco || value == model.ShiftBalanced || value == model.ShiftSport || value == model.ShiftTurbo || value == 0x80
        || (UsesMsiLayout(model) && !model.MsiSportUnclear && value == MsiSport);

    public const byte MsiSport = 0xC0;

    /// <summary>The performance-mode register as the laptop held it when Wattkin started (0xC0: it uses MSI's Sport).</summary>
    public byte? ShiftAtStart { get; private set; }

    private static string Hex(byte? value) => value is { } v ? $"0x{v:X2}" : "unreadable";

    /// <summary>
    /// MSI's standard WMI v2 layout (performance mode 0xD2, fan mode 0xD4, the keyboard at 0xD3 where it
    /// has one): write with MSI Center's exact bit semantics, and use its fan-table calls. Until 1.1.1 this
    /// also required the keyboard register, which left msi-ec's families without one (CONF_G2_2, G2_5 and
    /// G2_10: Stealth, Raider, Vector, Titan) without fan curves, though their fan register is the same.
    /// </summary>
    internal static bool UsesMsiLayout(EcModel? model) => model is { ShiftAddress: 0xD2, FanModeAddress: 0xD4 };

    public string FirmwareVersion => _acpi.EcVersion;
    public string FirmwareFull => _acpi.EcFirmware;
    public string WmiVersion => $"{_acpi.WmiMajor}.{_acpi.WmiMinor}";
    public EcSupport Support { get; }
    public string? MapSource => _model?.Source;

    /// <summary>Why controls are off, when they are.</summary>
    public string? Reason { get; }

    public bool CanWrite { get; }

    /// <summary>
    /// Drain-to-limit and calibration use MSI's generic "simulate unplug" register (MSI's own calibration
    /// tool uses it on every WMI v2 notebook). The GPU switch also checks the firmware's own capability bits.
    /// </summary>
    public bool CanSimulateUnplug => CanWrite;
    public bool CanSwitchGpuMode => CanWrite;
    public bool HasCoolerBoost => CanWrite && _model?.CoolerBoostAddress is not null;
    public bool HasKeyboardLight => CanWrite && _model?.KeyboardStateAddress is not null;
    public bool HasKeyboardTimeout => CanWrite && _model is { KeyboardModeAddress: not null, KeyboardAlwaysOnValue: not null, KeyboardAutoOffValue: not null };
    public int KeyboardMaxLevel => _model?.KeyboardMaxLevel ?? 0;
    // ---- Battery -----------------------------------------------------------------------------

    /// <summary>Charge stop percentage (10..100), ignoring whether the limit is switched on.</summary>
    public int? GetChargeLimit() => GetChargeLimitRaw() is { } r ? r & 0x7F : null;

    /// <summary>Raw register: bit7 = limit enabled, bits0-6 = stop %. Compare this, not the masked value.</summary>
    public byte? GetChargeLimitRaw() => _model?.ChargeAddress is { } address ? _acpi.ReadEc(address) : null;

    public static byte EncodeChargeLimit(int percent) => (byte)(0x80 | Math.Clamp(percent, MinChargeLimit, MaxChargeLimit));

    public bool SetChargeLimit(int percent) =>
        _model?.ChargeAddress is { } address && Write(() => _acpi.WriteEc(address, EncodeChargeLimit(percent)));

    /// <summary>Battery discharges even though the charger is connected (verified firmware only).</summary>
    public bool? GetSimulatedUnplug() => CanSimulateUnplug ? Bit(RegAdapter, 0) : false;

    /// <param name="heartbeat">Write even if already set, as MSI's tool does every 30 s.</param>
    public bool SetSimulatedUnplug(bool enabled, bool heartbeat = false) =>
        CanSimulateUnplug && Write(() => _acpi.WriteEcBits(RegAdapter, 0x01, enabled ? (byte)0x01 : (byte)0x00, force: heartbeat && enabled));

    // ---- Performance ------------------------------------------------------------------------

    private byte? ShiftValue(ShiftMode mode) => mode switch
    {
        ShiftMode.Eco => _model?.ShiftEco,
        ShiftMode.Balanced => _model?.ShiftBalanced,
        ShiftMode.Sport => SportValue,
        ShiftMode.Turbo => _model?.ShiftTurbo ?? _model?.ShiftSport,
        _ => null,
    };

    /// <summary>
    /// Sport's register value: the map's, else MSI's 0xC0 on its own layout once this laptop has been seen
    /// using it (MSI Center and the firmware put the Katana GF66 12UG in it; the Prestige 16 Studio never
    /// was). Null where sport stands in for a missing turbo: that's <see cref="ShiftMode.Turbo"/> then.
    /// </summary>
    private byte? SportValue =>
        _model?.ShiftTurbo is null ? null
        : _model.ShiftSport ?? (UsesMsiLayout(_model) && !_model.MsiSportUnclear && (_sportSeen || ShiftAtStart == MsiSport) ? MsiSport : null);

    /// <summary>The firmware has a Sport mode of its own, between Balanced and Turbo.</summary>
    public bool HasSportMode => _shiftOk && SportValue is not null;

    public ShiftMode? GetShiftMode()
    {
        if (_model?.ShiftAddress is not { } address || _acpi.ReadEc(address) is not { } value)
        {
            return null;
        }

        return value == _model.ShiftEco ? ShiftMode.Eco
            : value == _model.ShiftBalanced ? ShiftMode.Balanced
            : value == _model.ShiftTurbo ? ShiftMode.Turbo
            : value == _model.ShiftSport && _model.ShiftTurbo is null ? ShiftMode.Turbo // a map whose only fast mode is sport
            : value == _model.ShiftSport ? ShiftMode.Sport
            : UsesMsiLayout(_model) && value == MsiSport ? (_model.MsiSportUnclear ? ShiftMode.Turbo : ShiftMode.Sport) // msi-ec: "turbo is sometimes 0xc0" there
            : null;
    }

    /// <summary>The performance-mode register holds 0x80: no mode chosen yet (MSI's software never set one). <see cref="GetShiftMode"/> reads null then too.</summary>
    public bool ShiftModeUnset => _model?.ShiftAddress is { } address && _acpi.ReadEc(address) == 0x80;

    /// <summary>Only values from the model's map ever reach the EC; nothing is written when it already holds the value.</summary>
    public bool SetShiftMode(ShiftMode mode) =>
        _shiftOk && _model?.ShiftAddress is { } address && ShiftValue(mode) is { } value && Write(() => _acpi.WriteEcBits(address, 0xFF, value));

    public FanMode? GetFanMode()
    {
        if (_model?.FanModeAddress is not { } address || _acpi.ReadEc(address) is not { } value)
        {
            return null;
        }

        if (UsesMsiLayout(_model))
        {
            // MSI's semantics: bit7 = custom curve, bit4 = silent.
            return (value & 0x80) != 0 ? FanMode.Custom : (value & 0x10) != 0 ? FanMode.Silent : FanMode.Auto;
        }

        return value == _model.FanSilent ? FanMode.Silent
            : value == _model.FanAdvanced ? FanMode.Custom
            : FanMode.Auto;
    }

    /// <param name="curve">For <see cref="FanMode.Custom"/>: the speeds to use; null hands the controller back the table it holds.</param>
    /// <param name="unrestricted">The user accepted the experimental mode: curves that let the laptop run hot are allowed.</param>
    /// <param name="table">For <see cref="FanMode.Custom"/> where <see cref="HasFullFanTable"/>: the whole table, temperatures included; it takes the place of <paramref name="curve"/>.</param>
    public bool SetFanMode(FanMode mode, FanSpeeds? curve = null, bool unrestricted = false, FanTable? table = null)
    {
        lock (_fanWrite)
        {
            return SetFanModeLocked(mode, curve, unrestricted, table);
        }
    }

    private bool SetFanModeLocked(FanMode mode, FanSpeeds? curve, bool unrestricted, FanTable? table)
    {
        if (!_fanOk || _model?.FanModeAddress is not { } address)
        {
            return false;
        }

        if (mode == FanMode.Custom)
        {
            var written = table is not null && HasFullFanTable ? WriteFullFanTable(table, unrestricted) : WriteFanTable(curve, unrestricted);
            return written && Write(() => _acpi.WriteEcBits(address, 0x90, 0x80, force: true));
        }

        if (UsesMsiLayout(_model))
        {
            // Like MSI Center: Silent sets bit4; both clear the custom-curve bit; the low nibble stays.
            return Write(() => _acpi.WriteEcBits(address, 0x90, mode == FanMode.Silent ? (byte)0x10 : (byte)0x00));
        }

        var value = mode == FanMode.Silent ? _model.FanSilent : _model.FanAuto;
        return value is { } v && Write(() => _acpi.WriteEc(address, v));
    }

    /// <summary>
    /// Hands both fans' tables to the controller with Set_Fan(n): 8 bytes laid out as Get_Fan(n) returns
    /// them, with the six editable speeds replaced by <paramref name="curve"/> (or unchanged when it's
    /// null). That's MSI Center's sequence for its custom curve; selecting it (0xD4 bit 7) is the caller's
    /// step. Setting the bit alone left the fans on the built-in curve (measured: 50% duty at 42°C under a
    /// table that says off below 60°C).
    ///
    /// Only on MSI's own layout, only firmware temperature steps that look right, and (unless
    /// <paramref name="unrestricted"/>) only a resulting table that can cool the laptop. The table is
    /// read back and must match; if it doesn't, the original is written back and the fans go to Auto.
    /// A laptop with one fan (<see cref="FanCurves.HasGpuFan"/> false) only gets the CPU fan's table.
    /// </summary>
    public bool WriteFanTable(FanSpeeds? curve, bool unrestricted = false)
    {
        lock (_fanWrite)
        {
            return WriteFanTableLocked(curve, unrestricted);
        }
    }

    private bool WriteFanTableLocked(FanSpeeds? curve, bool unrestricted)
    {
        var cpu = _acpi.Get("Get_Fan", 1);
        var gpu = _acpi.Get("Get_Fan", 2);
        var before = ReadFanCurves();
        if (!HasFanCurves || cpu is null || gpu is null || before is null || _model?.FanModeAddress is not { } address)
        {
            Log.Warn("Not using the fan curve: the controller's fan table couldn't be read.");
            return false;
        }

        if (curve?.Problem(unrestricted, before) is { } problem)
        {
            Log.Warn($"Not using this fan curve: {problem}");
            return false;
        }

        var gpuFan = before.HasGpuFan;
        var wantCpu = cpu[..8];
        var wantGpu = gpu[..8];
        if (curve is not null)
        {
            for (var i = 0; i < FanSpeeds.Steps; i++)
            {
                wantCpu[1 + i] = (byte)curve.Cpu[i];
                if (gpuFan)
                {
                    wantGpu[1 + i] = (byte)curve.Gpu[i];
                }
            }
        }

        var after = new FanCurves(before.Cpu with { Speeds = [.. wantCpu[1..8].Select(b => (int)b)] }, before.Gpu with { Speeds = [.. wantGpu[1..8].Select(b => (int)b)] });
        FanCurve[] fans = gpuFan ? [after.Cpu, after.Gpu] : [after.Cpu];
        var stepsOk = fans.All(c => c.Temps.All(t => t is >= 20 and <= 110) && c.Temps.Zip(c.Temps.Skip(1)).All(p => p.Second > p.First));
        if (!stepsOk || (!unrestricted && !after.Sane))
        {
            Log.Warn("Not using the fan curve: the controller's temperature steps or the resulting table don't look safe.");
            return false;
        }

        // A write that fails halfway (the CPU fan's taken, the GPU fan's not) is undone like one that
        // reads back wrong: in curve mode the controller would otherwise run half the new table.
        var written = Write(() => _acpi.Set("Set_Fan", 1, wantCpu) && (!gpuFan || _acpi.Set("Set_Fan", 2, wantGpu)));
        if (!written
            || _acpi.Get("Get_Fan", 1) is not { } cpuAfter || !cpuAfter.AsSpan(1, 7).SequenceEqual(wantCpu.AsSpan(1, 7))
            || (gpuFan && (_acpi.Get("Get_Fan", 2) is not { } gpuAfter || !gpuAfter.AsSpan(1, 7).SequenceEqual(wantGpu.AsSpan(1, 7)))))
        {
            Log.Error(written
                ? "The fan table read back differently; putting the previous one back and switching the fans to Auto."
                : "The controller didn't take the whole fan table; putting the previous one back and switching the fans to Auto.");
            Write(() => _acpi.Set("Set_Fan", 1, cpu.AsSpan(0, 8)) && (!gpuFan || _acpi.Set("Set_Fan", 2, gpu.AsSpan(0, 8))));
            Write(() => _acpi.WriteEcBits(address, 0x90, 0x00, force: true));
            return false;
        }

        Log.Info($"Fan table handed to the controller: CPU {BitConverter.ToString(wantCpu, 1, 7)}, GPU {(gpuFan ? BitConverter.ToString(wantGpu, 1, 7) : "none (one fan)")}{(unrestricted ? " (experimental: unrestricted)" : "")}.");
        return true;
    }

    /// <summary>The fan-curve mode, on MSI's standard layout (where the table's format is known).</summary>
    public bool HasFanCurves => CanWrite && _fanOk && UsesMsiLayout(_model);

    /// <summary>
    /// Whole fan tables, step temperatures and drop-back temperatures included, written register by
    /// register: only on a map that names the table's registers (<see cref="EcModel.FanTable"/>).
    /// </summary>
    public bool HasFullFanTable => HasFanCurves && _model?.FanTable is not null;

    /// <summary>Where the fan table lives, on a map that names it (the self-test saves and restores these registers).</summary>
    public FanTableMap? FanTableMap => _model?.FanTable;

    /// <summary>Both fans' whole tables from the controller's registers, or null. Reads only.</summary>
    public FanTable? ReadFullFanTable()
    {
        if (_model?.FanTable is not { } map)
        {
            return null;
        }

        var cpu = map.Cpu.Decode(_acpi.ReadEc);
        var gpu = map.Gpu.Decode(_acpi.ReadEc);
        return cpu is not null && gpu is not null ? new FanTable { Cpu = cpu, Gpu = gpu } : null;
    }

    /// <summary>
    /// Writes both fans' whole tables, register by register as YAMDCC does, only the bytes that differ;
    /// selecting the curve mode (0xD4 bit 7) is the caller's step. Refused when the table breaks the
    /// rules (<see cref="FanTable.Problem"/>, safety rules unless <paramref name="unrestricted"/>) or the
    /// current one can't be read. The table is read back and must match; if it doesn't, the previous one
    /// is written back and the fans go to Auto.
    /// </summary>
    /// <param name="inCurveMode">
    /// Write while the controller keeps following the table (the self-test's hysteresis checks need its
    /// current step kept). Otherwise the fans go to Auto for the write, and the caller selects the curve.
    /// </param>
    public bool WriteFullFanTable(FanTable table, bool unrestricted = false, bool inCurveMode = false)
    {
        lock (_fanWrite)
        {
            return WriteFullFanTableLocked(table, unrestricted, inCurveMode);
        }
    }

    private bool WriteFullFanTableLocked(FanTable table, bool unrestricted, bool inCurveMode)
    {
        if (!HasFullFanTable || _model?.FanTable is not { } map || _model.FanModeAddress is not { } address)
        {
            return false;
        }

        if (table.Problem(unrestricted) is { } problem)
        {
            Log.Warn($"Not using this fan table: {problem}");
            return false;
        }

        if (ReadFullFanTable() is not { } before)
        {
            Log.Warn("Not using the fan table: the controller's table couldn't be read.");
            return false;
        }

        if (before.SameAs(table))
        {
            return true;
        }

        // Written a register at a time (up to ~70 calls): not while the controller follows the table, or
        // for that long it would run a mix of the old and new ones, steps briefly out of order. Auto in
        // between; the caller selects the curve once the whole table is in. If the mode can't be read or
        // Auto can't be set, nothing is written.
        if (!inCurveMode && GetFanMode() is not (FanMode.Auto or FanMode.Silent) && !Write(() => _acpi.WriteEcBits(address, 0x90, 0x00, force: true)))
        {
            Log.Warn("Not writing the fan table: the fans couldn't be switched to Auto for it.");
            return false;
        }

        if (!Write(() => WriteChanged(Encode(map, table))) || ReadFullFanTable() is not { } after || !after.SameAs(table))
        {
            Log.Error("The fan table read back differently; putting the previous one back and switching the fans to Auto.");
            Write(() => WriteChanged(Encode(map, before)));
            Write(() => _acpi.WriteEcBits(address, 0x90, 0x00, force: true));
            return false;
        }

        Log.Info($"Fan table written to the controller{(unrestricted ? " (experimental: unrestricted)" : "")}: CPU {table.Cpu.Describe()}; GPU {table.Gpu.Describe()}.");
        return true;
    }

    private static IEnumerable<(byte Address, byte Value)> Encode(FanTableMap map, FanTable table) => map.Cpu.Encode(table.Cpu).Concat(map.Gpu.Encode(table.Gpu));

    /// <summary>Writes each byte the register doesn't already hold. False if any write failed.</summary>
    private bool WriteChanged(IEnumerable<(byte Address, byte Value)> writes)
    {
        var ok = true;
        foreach (var (address, value) in writes)
        {
            if (_acpi.ReadEc(address) != value)
            {
                ok &= _acpi.WriteEc(address, value);
            }
        }

        return ok;
    }

    /// <summary>Both fans' tables from the firmware, or null. Reads only.</summary>
    public FanCurves? ReadFanCurves()
    {
        var cpu = FanCurve.Parse(_acpi.Get("Get_Temperature", 1), _acpi.Get("Get_Fan", 1));
        var gpu = FanCurve.Parse(_acpi.Get("Get_Temperature", 2), _acpi.Get("Get_Fan", 2));
        return cpu is not null && gpu is not null ? new FanCurves(cpu, gpu) : null;
    }

    public bool? GetCoolerBoost() => _model?.CoolerBoostAddress is { } address ? Bit(address, _model.CoolerBoostBit) : null;

    public bool SetCoolerBoost(bool enabled) =>
        _model?.CoolerBoostAddress is { } address && WriteBit(address, _model.CoolerBoostBit, enabled);

    // ---- Keyboard / LEDs / webcam -------------------------------------------------------------

    /// <summary>Keyboard backlight level 0 (off) .. max.</summary>
    public int? GetKeyboardLevel() =>
        _model?.KeyboardStateAddress is { } address && _acpi.ReadEc(address) is { } v ? v & 0x03 : null;

    public bool SetKeyboardLevel(int level)
    {
        if (_model?.KeyboardStateAddress is not { } address)
        {
            return false;
        }

        level = Math.Clamp(level, 0, Math.Max(0, (int)_model.KeyboardMaxLevel));
        return UsesMsiLayout(_model)
            // Exactly MSI Center's write: D3 = (D3 & 0x80) + level.
            ? Write(() => _acpi.WriteEcBits(address, 0x7F, (byte)level))
            : Write(() => _acpi.WriteEc(address, (byte)(_model.KeyboardStateBase | level)));
    }

    /// <summary>True = backlight switches off after ~10 s without typing and back on with a key press.</summary>
    public bool? GetKeyboardAutoOff()
    {
        if (!HasKeyboardTimeout || _acpi.ReadEc(_model!.KeyboardModeAddress!.Value) is not { } value)
        {
            return null;
        }

        var mask = (byte)(_model.KeyboardAlwaysOnValue!.Value ^ _model.KeyboardAutoOffValue!.Value);
        return (value & mask) == (_model.KeyboardAutoOffValue.Value & mask);
    }

    /// <summary>Changes only the bits that differ between the two modes (the register also holds LED bits).</summary>
    public bool SetKeyboardAutoOff(bool enabled)
    {
        if (!HasKeyboardTimeout)
        {
            return false;
        }

        var mask = (byte)(_model!.KeyboardAlwaysOnValue!.Value ^ _model.KeyboardAutoOffValue!.Value);
        var value = enabled ? _model.KeyboardAutoOffValue.Value : _model.KeyboardAlwaysOnValue.Value;
        return mask != 0 && Write(() => _acpi.WriteEcBits(_model.KeyboardModeAddress!.Value, mask, value));
    }

    public bool SetMuteLed(bool on) => _model?.MuteLedAddress is { } address && WriteBit(address, _model.LedBit, on);

    public bool SetMicMuteLed(bool on) => _model?.MicMuteLedAddress is { } address && WriteBit(address, _model.LedBit, on);

    /// <summary>On the verified model MSI checks EC 0x2F bit1 before offering the webcam switch.</summary>
    public bool WebcamSupported =>
        _model?.WebcamAddress is not null && (!UsesMsiLayout(_model) || Bit(RegCapabilities, 1) == true);

    public bool? GetWebcam() => WebcamSupported ? Bit(_model!.WebcamAddress!.Value, _model.WebcamBit) : null;

    public bool SetWebcam(bool enabled) => WebcamSupported && WriteBit(_model!.WebcamAddress!.Value, _model.WebcamBit, enabled);

    public bool? GetFnLock() => CanWrite ? Bit(RegHostFlags, 1) : null;

    /// <summary>MSI's service sets D9 bit0 at every start and resume ("vendor software present"); so do we.</summary>
    public bool MarkVendorSoftwarePresent() => CanWrite && Write(() => _acpi.WriteEcBits(RegHostFlags, 0x01, 0x01));

    // ---- Sensors ------------------------------------------------------------------------------

    /// <summary>
    /// Get_Temperature(0): [0] CPU °C, [1] GPU °C (0 when the dGPU is off); falls back to the model's EC
    /// temperature registers. Get_Fan(0): big-endian 16-bit tach periods, RPM = 480000 / raw (0 = stopped).
    /// </summary>
    public EcSensors? ReadSensors()
    {
        var temps = _acpi.Get("Get_Temperature", 0);
        var fans = _acpi.Get("Get_Fan", 0);
        int? cpu = temps is { } t && t[0] is > 0 and < 120 ? t[0] : null;
        int? gpu = temps is { } g && g[1] is > 0 and < 120 ? g[1] : null;
        if (temps is null && _model is not null)
        {
            cpu = _model.CpuTempAddress is { } ca && _acpi.ReadEc(ca) is { } c and > 0 and < 120 ? c : null;
            gpu = _model.GpuTempAddress is { } ga && _acpi.ReadEc(ga) is { } gv and > 0 and < 120 ? gv : null;
        }

        if (cpu is null && gpu is null && fans is null)
        {
            return null;
        }

        return new EcSensors(cpu, gpu, fans is { } f1 ? Rpm(f1[0], f1[1]) : null, fans is { } f2 ? Rpm(f2[2], f2[3]) : null);
    }

    /// <summary>One register as it is, for the Debug page. Never writes.</summary>
    public byte? ReadRegister(byte address) => _acpi.ReadEc(address);

    private static int Rpm(byte high, byte low)
    {
        var raw = (high << 8) | low;
        return raw == 0 ? 0 : 480000 / raw;
    }

    private bool? Bit(byte address, int bit) => _acpi.ReadEc(address) is { } v ? (v & (1 << bit)) != 0 : null;

    private bool WriteBit(byte address, int bit, bool on)
    {
        var mask = (byte)(1 << bit);
        return Write(() => _acpi.WriteEcBits(address, mask, on ? mask : (byte)0));
    }

    private bool Write(Func<bool> write)
    {
        if (!CanWrite)
        {
            return false;
        }

        return write();
    }
}
