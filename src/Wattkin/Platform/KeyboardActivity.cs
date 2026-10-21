using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using Wattkin.Native;

namespace Wattkin.Platform;

/// <summary>
/// Tells when a key is pressed on the laptop's own keyboard, whichever window has the focus: Windows'
/// Raw Input for keyboards (usage page 1, usage 6) with RIDEV_INPUTSINK, on a hidden window on a thread
/// of its own. Of each WM_INPUT it reads only the header (which keyboard), never the data (which key).
///
/// Only keyboards Windows counts as part of the computer count (DEVPKEY_Device_InLocalMachineContainer):
/// on the Stealth 15M the built-in PS/2 keyboard (ACPI\MSNB1001) and Intel's two virtual keyboards for
/// the special keys are, a Bluetooth Logitech keyboard isn't (2026-10-21). Typing on another keyboard
/// doesn't light the built-in one. Keys sent by software (no device) don't count either. When Windows
/// can't say where a keyboard is, it counts: a light that comes on is better than one that never does.
/// Keys typed on the secure desktop (lock screen, UAC prompt) don't reach it. Event-driven, so it costs
/// nothing while nobody types.
/// </summary>
internal sealed class KeyboardActivity : IDisposable
{
    /// <summary>
    /// Raw Input keeps one registration per process for each kind of device, so the newest listener owns
    /// it. A stopped listener's thread ends a little later: it only removes the registration if no newer
    /// listener has taken it over since (auto-off switched off and on quickly), or that one would go deaf.
    /// </summary>
    private static int s_generation;

    /// <summary>A registration and a stopped listener's check-then-remove don't interleave.</summary>
    private static readonly object s_registration = new();

    private readonly Dispatcher _dispatcher;

    private KeyboardActivity(Dispatcher dispatcher) => _dispatcher = dispatcher;

    /// <summary>Starts listening; <paramref name="pressed"/> runs on the listener's thread. Null with a reason when Windows refused.</summary>
    public static KeyboardActivity? Start(Action pressed, out string? error)
    {
        int generation;
        lock (s_registration)
        {
            generation = ++s_generation;
        }

        using var ready = new ManualResetEventSlim();
        Dispatcher? dispatcher = null;
        string? failure = null;
        var thread = new Thread(() =>
        {
            // Per keyboard (Raw Input's device handle): part of the computer or not. Forgotten when a keyboard comes or goes.
            var builtIn = new Dictionary<IntPtr, bool>();
            using var window = new HwndSource(new HwndSourceParameters("Wattkin key activity") { WindowStyle = 0, ExtendedWindowStyle = User32.WS_EX_TOOLWINDOW, Width = 0, Height = 0 });
            window.AddHook((IntPtr _, int message, IntPtr _, IntPtr input, ref bool _) =>
            {
                try
                {
                    if (message == User32.WM_INPUT_DEVICE_CHANGE)
                    {
                        builtIn.Clear();
                    }
                    else if (message == User32.WM_INPUT && Keyboard(input) switch { null => true, var device => device != IntPtr.Zero && IsBuiltIn(device.Value, builtIn) })
                    {
                        pressed();
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("Key activity handler failed", ex);
                }

                return IntPtr.Zero; // not handled: DefWindowProc does Raw Input's cleanup
            });

            var keyboards = new User32.RAWINPUTDEVICE { UsagePage = 0x01, Usage = 0x06, Flags = User32.RIDEV_INPUTSINK | User32.RIDEV_DEVNOTIFY, Target = window.Handle };
            lock (s_registration)
            {
                if (!Register(keyboards))
                {
                    failure = $"Windows refused to report key presses (error {Marshal.GetLastWin32Error()}).";
                }
            }

            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            if (failure is null)
            {
                Dispatcher.Run();
                lock (s_registration)
                {
                    if (Volatile.Read(ref s_generation) == generation)
                    {
                        Register(keyboards with { Flags = User32.RIDEV_REMOVE, Target = IntPtr.Zero });
                    }
                }
            }
        })
        {
            IsBackground = true,
            Name = "Wattkin key activity",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();

        error = failure;
        return failure is null ? new KeyboardActivity(dispatcher!) : null;
    }

    /// <summary>Which keyboard sent this input (zero: sent by software), or null when the header can't be read.</summary>
    private static IntPtr? Keyboard(IntPtr input)
    {
        var size = (uint)Marshal.SizeOf<User32.RAWINPUTHEADER>();
        return User32.GetRawInputData(input, User32.RID_HEADER, out var header, ref size, size) == uint.MaxValue ? null : header.Device;
    }

    private static bool IsBuiltIn(IntPtr device, Dictionary<IntPtr, bool> known)
    {
        if (!known.TryGetValue(device, out var builtIn))
        {
            builtIn = InThisComputer(device) ?? true;
            known[device] = builtIn;
        }

        return builtIn;
    }

    /// <summary>Whether Windows counts this Raw Input device as part of the computer; null when it can't say.</summary>
    internal static unsafe bool? InThisComputer(IntPtr device)
    {
        uint chars = 0;
        User32.GetRawInputDeviceInfo(device, User32.RIDI_DEVICENAME, null, ref chars);
        if (chars is 0 or > 1024)
        {
            return null;
        }

        var name = stackalloc char[(int)chars];
        if (User32.GetRawInputDeviceInfo(device, User32.RIDI_DEVICENAME, name, ref chars) == uint.MaxValue)
        {
            return null;
        }

        // An interface path ("\\?\ACPI#MSNB1001#...#{guid}"; older Windows wrote "\??\"): its device, then the property.
        var path = new string(name).TrimEnd('\0');
        path = path.StartsWith(@"\??\", StringComparison.Ordinal) ? @"\\?\" + path[4..] : path;
        var id = stackalloc char[512];
        var size = 512u * sizeof(char);
        if (SetupApi.CM_Get_Device_Interface_Property(path, SetupApi.DEVPKEY_Device_InstanceId, out _, id, ref size, 0) != SetupApi.CR_SUCCESS
            || SetupApi.CM_Locate_DevNode(out var node, new string(id).TrimEnd('\0'), 0) != SetupApi.CR_SUCCESS)
        {
            return null;
        }

        byte local = 0;
        var length = 1u;
        return SetupApi.CM_Get_DevNode_Property(node, SetupApi.DEVPKEY_Device_InLocalMachineContainer, out var type, &local, ref length, 0) == SetupApi.CR_SUCCESS && type == SetupApi.DEVPROP_TYPE_BOOLEAN
            ? local != 0
            : null;
    }

    private static bool Register(User32.RAWINPUTDEVICE device) =>
        User32.RegisterRawInputDevices([device], 1, (uint)Marshal.SizeOf<User32.RAWINPUTDEVICE>());

    /// <summary>Stops listening. Doesn't wait for the thread (it may be waiting on the caller's lock).</summary>
    public void Dispose() => _dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
}
