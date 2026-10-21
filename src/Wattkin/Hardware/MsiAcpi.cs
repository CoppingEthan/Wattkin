using System.Management;

namespace Wattkin.Hardware;

/// <summary>
/// Transport for MSI's BIOS-provided ACPI-WMI interface (GUID ABBC0F6E-8EA1-11D1-00A0-C90629100000),
/// the same one MSI Center uses, served by Windows' built-in ACPI-WMI driver. Wattkin reaches it
/// through its own WMI classes (<see cref="WmiSchema"/>), so no MSI software is needed.
///
/// Wire format (verified against MSI's MSIWMIACPI2.dll and its call log on this laptop):
///   input  = 32-byte package { Bytes[32] }, [0] = subfeature or EC address, [1..] = data
///   output = 32-byte package { Bytes[32] }, [0] = status (1 = success), [1..] = data
/// Get_WMI / Get_EC take no input. The firmware handler isn't re-entrant, so every call is serialized:
/// across all instances in this process, and across processes (e.g. a --diagnose run) by a named mutex.
/// Needs administrator rights.
/// </summary>
public sealed class MsiAcpi : IDisposable
{
    private const int PackageSize = 32;
    private static readonly object ProcessGate = new();
    private static readonly TimeSpan ReconnectBackoff = TimeSpan.FromSeconds(5);

    private readonly Mutex _machineGate = new(false, @"Local\Wattkin.EcCall");
    private ManagementScope _scope;
    private ManagementClass _packageClass;
    private ManagementObject _instance;
    private bool _broken;
    private bool _disposed;
    private DateTime _nextReconnectUtc = DateTime.MinValue;
    private int _failuresLogged;

    private MsiAcpi(ManagementScope scope, ManagementObject instance, ManagementClass packageClass)
    {
        _scope = scope;
        _instance = instance;
        _packageClass = packageClass;
    }

    public int WmiMajor { get; private set; }
    public int WmiMinor { get; private set; }

    /// <summary>EC firmware string, e.g. "1594EMS1.1131007202413:20:06" (version + build date/time).</summary>
    public string EcFirmware { get; private set; } = string.Empty;

    /// <summary>EC firmware version only, e.g. "1594EMS1.113".</summary>
    public string EcVersion => EcFirmware.Length >= 12 ? EcFirmware[..12] : EcFirmware;

    /// <summary>
    /// Set when another Wattkin owns the hardware (screenshots, measurements): every Set_* call is
    /// refused here, whatever the caller, so this process can only read.
    /// </summary>
    public bool ReadOnly { get; private init; }

    /// <param name="readOnly">A copy that doesn't own the hardware; it doesn't register anything either (the owner has).</param>
    public static MsiAcpi? TryOpen(out string? error, bool readOnly = false)
    {
        if (!readOnly && WmiSchema.EnsureRegistered() is { } schemaError)
        {
            error = $"Couldn't set up access to the firmware interface ({schemaError}).";
            return null;
        }

        try
        {
            if (Bind() is not var (scope, instance, packageClass))
            {
                error = "This laptop's firmware doesn't offer MSI's interface, so hardware controls aren't available. Monitoring still works.";
                return null;
            }

            var acpi = new MsiAcpi(scope, instance, packageClass) { ReadOnly = readOnly };
            var wmi = acpi.Call("Get_WMI", null);
            if (wmi is not { Length: PackageSize } || wmi[0] != 1)
            {
                error = "The firmware interface didn't answer.";
                acpi.Dispose();
                return null;
            }

            acpi.WmiMajor = wmi[1];
            acpi.WmiMinor = wmi[2];
            var ec = acpi.Call("Get_EC", null);
            if (ec is { Length: PackageSize } && ec[0] == 1)
            {
                acpi.EcFirmware = System.Text.Encoding.ASCII.GetString(ec, 2, 28).TrimEnd('\0', ' ');
            }

            error = null;
            Log.Info($"Firmware interface opened ({instance["InstanceName"]}): WMI {acpi.WmiMajor}.{acpi.WmiMinor}, EC '{acpi.EcFirmware}'");
            return acpi;
        }
        catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.AccessDenied)
        {
            error = "Administrator rights are needed to talk to the embedded controller.";
        }
        catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.InvalidClass)
        {
            error = "Wattkin's access to the firmware interface isn't set up yet. Restart Wattkin as administrator.";
        }
        catch (ManagementException ex)
        {
            error = $"The firmware interface didn't answer ({ex.ErrorCode}).";
        }
        catch (UnauthorizedAccessException)
        {
            error = "Administrator rights are needed to talk to the embedded controller.";
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            error = $"WMI error 0x{ex.HResult:X8}.";
        }

        return null;
    }

    /// <summary>
    /// A fresh connection: scope, instance and the package class all belong to one WMI session. The
    /// firmware's instance is looked up ("ACPI\PNP0C14\0_0" on the Prestige 16 Studio, but it needn't
    /// be that elsewhere). Null when the firmware has no active instance of the interface.
    /// </summary>
    private static (ManagementScope, ManagementObject, ManagementClass)? Bind()
    {
        var scope = new ManagementScope(@"root\WMI");
        scope.Connect();
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery($"SELECT * FROM {WmiSchema.AcpiClass}"));
        ManagementObject? instance = null;
        foreach (ManagementObject candidate in searcher.Get())
        {
            if (instance is null && candidate["Active"] is true)
            {
                instance = candidate;
            }
            else
            {
                candidate.Dispose();
            }
        }

        return instance is null ? null : (scope, instance, new ManagementClass(scope, new ManagementPath(WmiSchema.PackageClass), null));
    }

    /// <summary>Calls a Get_* method with one selector byte. Returns the 31 data bytes, or null on failure.</summary>
    public byte[]? Get(string method, byte selector)
    {
        var input = new byte[PackageSize];
        input[0] = selector;
        var output = Call(method, input);
        return output is { Length: PackageSize } && output[0] == 1 ? output[1..] : null;
    }

    /// <summary>Calls a Set_* method. Returns true when the firmware reports success.</summary>
    public bool Set(string method, byte selector, ReadOnlySpan<byte> data)
    {
        if (data.Length > PackageSize - 1)
        {
            throw new ArgumentOutOfRangeException(nameof(data));
        }

        if (ReadOnly)
        {
            Log.Warn($"Refused {method}(0x{selector:X2}): this copy of Wattkin is read-only.");
            return false;
        }

        var input = new byte[PackageSize];
        input[0] = selector;
        data.CopyTo(input.AsSpan(1));
        var output = Call(method, input);
        return output is { Length: PackageSize } && output[0] == 1;
    }

    public byte? ReadEc(byte address) => Get("Get_Data", address)?[0];

    /// <param name="log">False for a repeat of the value already there (a heartbeat): only failures are logged then.</param>
    public bool WriteEc(byte address, byte value, bool log = true)
    {
        var ok = Set("Set_Data", address, [value]);
        if ((ok && log) || (!ok && _failuresLogged < 20))
        {
            Log.Info($"EC write 0x{address:X2} = 0x{value:X2} -> {(ok ? "ok" : "FAILED")}");
        }

        return ok;
    }

    /// <summary>
    /// Read-modify-write of selected bits; other bits in the register are preserved. With
    /// <paramref name="force"/> the byte is written even when unchanged (a heartbeat the EC may expect).
    /// </summary>
    public bool WriteEcBits(byte address, byte mask, byte value, bool force = false)
    {
        lock (ProcessGate)
        {
            var current = ReadEc(address);
            if (current is null)
            {
                return false;
            }

            var next = (byte)((current.Value & ~mask) | (value & mask));
            return (next == current.Value && !force) || WriteEc(address, next, log: next != current.Value);
        }
    }

    /// <summary>
    /// One serialized firmware call. Any failure returns null. After a COM-level failure (e.g. the WMI
    /// service restarted) the whole binding is rebuilt, with backoff, before the next call.
    /// </summary>
    private byte[]? Call(string method, byte[]? input)
    {
        lock (ProcessGate)
        {
            if (_disposed)
            {
                return null;
            }

            var owned = false;
            try
            {
                try
                {
                    owned = _machineGate.WaitOne(TimeSpan.FromSeconds(2));
                }
                catch (AbandonedMutexException)
                {
                    owned = true;
                }

                // The firmware's handler isn't re-entrant: without the machine-wide lock (another process
                // stuck in a call), this call fails rather than run alongside it.
                if (!owned)
                {
                    if (_failuresLogged++ < 20)
                    {
                        Log.Warn($"Firmware call {method} skipped: another process held the firmware interface for over 2 s.");
                    }

                    return null;
                }

                if (_broken && !Reconnect())
                {
                    return null;
                }

                if (input is null)
                {
                    using var plain = _instance.InvokeMethod(method, null, null);
                    return Extract(plain);
                }

                using var parameters = _instance.GetMethodParameters(method);
                using var package = _packageClass.CreateInstance();
                package["Bytes"] = input;
                parameters["Data"] = package;
                using var result = _instance.InvokeMethod(method, parameters, null);
                return Extract(result);
            }
            catch (ManagementException ex)
            {
                LogFailure($"Firmware call {method}({(input is null ? "" : $"0x{input[0]:X2}")}) failed", ex);
                return null;
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or InvalidOperationException)
            {
                LogFailure($"Firmware call {method} failed; will reconnect", ex);
                _broken = true;
                return null;
            }
            finally
            {
                if (owned)
                {
                    _machineGate.ReleaseMutex();
                }
            }
        }
    }

    private bool Reconnect()
    {
        if (DateTime.UtcNow < _nextReconnectUtc)
        {
            return false;
        }

        _nextReconnectUtc = DateTime.UtcNow + ReconnectBackoff;
        try
        {
            // A WMI service restart can drop the classes along with the connection: register them again.
            if (!ReadOnly)
            {
                WmiSchema.EnsureRegistered();
            }

            if (Bind() is not var (scope, instance, packageClass))
            {
                return false;
            }

            _instance.Dispose();
            _packageClass.Dispose();
            (_scope, _instance, _packageClass) = (scope, instance, packageClass);
            _broken = false;
            _failuresLogged = 0;
            Log.Info("Firmware interface reconnected.");
            return true;
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            LogFailure("Firmware interface reconnect failed", ex);
            return false;
        }
    }

    /// <summary>Failures are logged, but not endlessly, so the log keeps its history of real writes.</summary>
    private void LogFailure(string message, Exception ex)
    {
        if (_failuresLogged++ < 20)
        {
            Log.Error(message, ex);
        }
    }

    private static byte[]? Extract(ManagementBaseObject? result)
    {
        if (result?["Data"] is not ManagementBaseObject data)
        {
            return null;
        }

        using (data)
        {
            return data["Bytes"] as byte[];
        }
    }

    public void Dispose()
    {
        lock (ProcessGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _instance.Dispose();
            _packageClass.Dispose();
            _machineGate.Dispose();
        }
    }
}
