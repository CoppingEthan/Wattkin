using System.Globalization;

namespace Wattkin.Hardware;

/// <summary>
/// One fan's whole table, as the controller runs it in the custom curve mode: seven speeds, and for
/// each of the six steps above the first the temperature that moves the fan up to it (<see cref="Up"/>)
/// and the one under which it drops back (<see cref="Down"/>). The gap between the two is the
/// hysteresis that keeps a fan from hunting between two speeds around one temperature. YAMDCC calls
/// these up and down thresholds.
/// </summary>
public sealed class FanSteps
{
    public const int Count = 7;
    public const int Thresholds = Count - 1;

    /// <summary>Below Up[0], then from each Up[i]: MSI's percentages (above 100 is allowed; full speed is Cooler Boost).</summary>
    public int[] Speeds { get; set; } = [];

    /// <summary>°C at which the fan steps up to Speeds[i + 1].</summary>
    public int[] Up { get; set; } = [];

    /// <summary>°C under which the fan drops back from Speeds[i + 1] to Speeds[i].</summary>
    public int[] Down { get; set; } = [];

    public FanSteps Clone() => new() { Speeds = [.. Speeds], Up = [.. Up], Down = [.. Down] };

    public bool SameAs(FanSteps other) => Speeds.SequenceEqual(other.Speeds) && Up.SequenceEqual(other.Up) && Down.SequenceEqual(other.Down);

    /// <summary>"off below 50°C · 30% from 64°C (back under 61°C) · …" (repeated speeds folded into the first step).</summary>
    public string Describe()
    {
        if (Speeds.Length != Count || Up.Length != Thresholds || Down.Length != Thresholds)
        {
            return "incomplete table";
        }

        var parts = new List<string> { $"{Speed(Speeds[0])} below {Up[0]}°C" };
        for (var i = 0; i < Thresholds; i++)
        {
            if (Speeds[i + 1] != Speeds[i])
            {
                parts.Add($"{Speed(Speeds[i + 1])} from {Up[i]}°C (back under {Down[i]}°C)");
            }
        }

        return string.Join(" · ", parts);
    }

    /// <summary>What's wrong with this fan's table, or null (see <see cref="FanTable.Problem"/>).</summary>
    internal string? Problem(string name, bool unrestricted)
    {
        if (Speeds is not { Length: Count } || Up is not { Length: Thresholds } || Down is not { Length: Thresholds })
        {
            return $"The {name} fan's table is incomplete.";
        }

        if (Speeds.Any(s => s is < 0 or > FanSpeeds.Max))
        {
            return $"The {name} fan's speeds must be between 0 and {FanSpeeds.Max}%.";
        }

        if (Up.Any(t => t is < FanTable.MinTemp or > FanTable.MaxTemp))
        {
            return $"The {name} fan's step temperatures must be between {FanTable.MinTemp} and {FanTable.MaxTemp}°C.";
        }

        if (Up.Zip(Up.Skip(1)).Any(p => p.Second <= p.First))
        {
            return $"Each {name} fan step must start hotter than the one before.";
        }

        for (var i = 0; i < Thresholds; i++)
        {
            var gap = Up[i] - Down[i];
            if (gap is < 1 or > FanTable.MaxHysteresis || Down[i] < 0)
            {
                return $"At step {i + 1}, the {name} fan must drop back between 1 and {FanTable.MaxHysteresis}°C below the {Up[i]}°C that starts it.";
            }
        }

        if (unrestricted)
        {
            return null;
        }

        if (Speeds.Zip(Speeds.Skip(1)).Any(p => p.Second < p.First))
        {
            return $"The {name} fan's speed can't drop as the temperature rises.";
        }

        if (Speeds[^1] < FanTable.MinTopSpeed || Up[^1] > FanTable.MaxTopStepTemp)
        {
            return $"The {name} fan must run at {FanTable.MinTopSpeed}% or more from {FanTable.MaxTopStepTemp}°C at the latest (its hottest step), so the laptop can't overheat. To go past that, allow any curve (experimental).";
        }

        if (Speeds[^2] == 0)
        {
            return $"The {name} fan must be running by the step before its hottest ({Up[^2]}°C), so it isn't off until the last moment. To go past that, allow any curve (experimental).";
        }

        // The steps above can still sit just under the hottest (e.g. 1% from 88°C): the fan must be
        // properly running by a temperature the laptop reaches in normal hard use.
        if (Speeds[Up.Count(t => t <= FanTable.CheckTemp)] < FanTable.MinSpeedAtCheck)
        {
            return $"The {name} fan must run at {FanTable.MinSpeedAtCheck}% or more by {FanTable.CheckTemp}°C, so the laptop can't overheat. To go past that, allow any curve (experimental).";
        }

        return null;
    }

    private static string Speed(int percent) => percent == 0 ? "off" : percent.ToString(CultureInfo.InvariantCulture) + "%";
}

/// <summary>
/// Both fans' whole tables: what a fan profile holds on a laptop whose controller Wattkin writes
/// register by register (<see cref="FanTableMap"/>), temperatures included.
/// </summary>
public sealed class FanTable
{
    /// <summary>Always: step temperatures the controller can work with.</summary>
    public const int MinTemp = 20;
    public const int MaxTemp = 110;

    /// <summary>Always: the most a fan may wait below a step's start before it drops back (a larger gap is surely a typo).</summary>
    public const int MaxHysteresis = 30;

    /// <summary>Unless experimental: the hottest step runs the fan at least this fast…</summary>
    public const int MinTopSpeed = 80;

    /// <summary>…and starts at this temperature or below.</summary>
    public const int MaxTopStepTemp = 90;

    /// <summary>Unless experimental: by this temperature the fan runs at least <see cref="MinSpeedAtCheck"/>%.</summary>
    public const int CheckTemp = 80;
    public const int MinSpeedAtCheck = 50;

    public FanSteps Cpu { get; set; } = new();
    public FanSteps Gpu { get; set; } = new();

    public FanTable Clone() => new() { Cpu = Cpu.Clone(), Gpu = Gpu.Clone() };

    public bool SameAs(FanTable other) => Cpu.SameAs(other.Cpu) && Gpu.SameAs(other.Gpu);

    /// <summary>
    /// Could be the table the laptop came with: each fan's speed never falls as it gets hotter and its
    /// hottest step runs it fast (<see cref="FanCurve.MinTopSpeed"/>%), the bar the factory curve is held
    /// to. A quiet table other fan software left in the controller isn't kept as the laptop's own.
    /// </summary>
    public bool LooksLikeFactory() =>
        Problem(unrestricted: true) is null
        && new[] { Cpu, Gpu }.All(f => f.Speeds.Zip(f.Speeds.Skip(1)).All(p => p.Second >= p.First) && f.Speeds[^1] >= FanCurve.MinTopSpeed);

    /// <summary>
    /// What's wrong with this table, or null. Always: seven speeds of 0–150%, step temperatures of
    /// 20–110°C rising from step to step, and each step dropping back 1–30°C below where it starts.
    /// Unless <paramref name="unrestricted"/>: speeds never fall as it gets hotter, the fan is running
    /// by the step before the hottest and at <see cref="MinSpeedAtCheck"/>% or more by
    /// <see cref="CheckTemp"/>°C, and the hottest step runs it at <see cref="MinTopSpeed"/>% or more
    /// from <see cref="MaxTopStepTemp"/>°C at the latest.
    /// </summary>
    public string? Problem(bool unrestricted) =>
        Cpu is null || Gpu is null ? "The fan table is incomplete." // a hand-edited settings file can hold "Cpu": null
        : Cpu.Problem("CPU", unrestricted) ?? Gpu.Problem("GPU", unrestricted);

    /// <summary>The speeds-only view (the first six speeds of each fan), for <see cref="FanSpeeds"/> code paths.</summary>
    public FanSpeeds ToSpeeds() => new() { Cpu = [.. Cpu.Speeds.Take(FanSpeeds.Steps)], Gpu = [.. Gpu.Speeds.Take(FanSpeeds.Steps)] };

    /// <summary>This table with the first six speeds of each fan from <paramref name="speeds"/> (a speeds-only fan profile at this table's temperatures).</summary>
    public FanTable WithSpeeds(FanSpeeds speeds)
    {
        var table = Clone();
        for (var i = 0; i < FanSpeeds.Steps && i < table.Cpu.Speeds.Length && i < speeds.Cpu.Length; i++)
        {
            table.Cpu.Speeds[i] = speeds.Cpu[i];
        }

        for (var i = 0; i < FanSpeeds.Steps && i < table.Gpu.Speeds.Length && i < speeds.Gpu.Length; i++)
        {
            table.Gpu.Speeds[i] = speeds.Gpu[i];
        }

        return table;
    }

    /// <summary>Fans off until the hottest step, which keeps <paramref name="laptop"/>'s speed (experimental "Off").</summary>
    public static FanTable Off(FanTable laptop) => laptop.WithSpeeds(new FanSpeeds { Cpu = new int[FanSpeeds.Steps], Gpu = new int[FanSpeeds.Steps] });
}

/// <summary>
/// Where one fan's table lives in the embedded controller: seven speed registers, six "up" registers
/// (the step's start temperature) and six "down" registers. With <see cref="DownIsOffset"/> a down
/// register holds how far below its up temperature the fan drops back (YAMDCC's OffsetDT, true on
/// MSI laptops before 2025); otherwise it holds the temperature itself.
/// </summary>
public sealed record FanRegisters(byte[] Speeds, byte[] Up, byte[] Down, bool DownIsOffset = true)
{
    public IEnumerable<byte> Addresses => Speeds.Concat(Up).Concat(Down);

    /// <summary>The bytes to write, step by step as YAMDCC writes them: speed, then that step's up and down.</summary>
    public IEnumerable<(byte Address, byte Value)> Encode(FanSteps steps)
    {
        for (var i = 0; i < FanSteps.Count; i++)
        {
            yield return (Speeds[i], (byte)steps.Speeds[i]);
            if (i > 0)
            {
                yield return (Up[i - 1], (byte)steps.Up[i - 1]);
                yield return (Down[i - 1], (byte)(DownIsOffset ? steps.Up[i - 1] - steps.Down[i - 1] : steps.Down[i - 1]));
            }
        }
    }

    /// <summary>The table as the controller holds it, or null when a register couldn't be read.</summary>
    public FanSteps? Decode(Func<byte, byte?> read)
    {
        var speeds = Speeds.Select(read).ToArray();
        var up = Up.Select(read).ToArray();
        var down = Down.Select(read).ToArray();
        if (speeds.Any(b => b is null) || up.Any(b => b is null) || down.Any(b => b is null))
        {
            return null;
        }

        return new FanSteps
        {
            Speeds = [.. speeds.Select(b => (int)b!.Value)],
            Up = [.. up.Select(b => (int)b!.Value)],
            Down = [.. down.Select((b, i) => DownIsOffset ? up[i]!.Value - b!.Value : b!.Value)],
        };
    }
}

/// <summary>Both fans' registers.</summary>
public sealed record FanTableMap(FanRegisters Cpu, FanRegisters Gpu)
{
    /// <summary>
    /// MSI's classic layout: CPU fan speeds 0x72–0x78, step starts 0x6A–0x6F, drop-back offsets
    /// 0x7A–0x7F; the GPU fan's 0x18 higher (0x8A–0x90, 0x82–0x87, 0x92–0x97). The addresses of
    /// YAMDCC's configuration for the Stealth 15M A11UEK (EC 1563EMS1.115), which its service writes
    /// at every start, and the bytes MSI's Get_Fan / Get_Temperature calls return (see <see cref="FanCurve"/>).
    /// </summary>
    public static readonly FanTableMap MsiClassic = new(
        new FanRegisters([0x72, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78], [0x6A, 0x6B, 0x6C, 0x6D, 0x6E, 0x6F], [0x7A, 0x7B, 0x7C, 0x7D, 0x7E, 0x7F]),
        new FanRegisters([0x8A, 0x8B, 0x8C, 0x8D, 0x8E, 0x8F, 0x90], [0x82, 0x83, 0x84, 0x85, 0x86, 0x87], [0x92, 0x93, 0x94, 0x95, 0x96, 0x97]));
}
