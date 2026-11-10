using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Wattkin.Native;

namespace Wattkin.Platform;

/// <summary>
/// The built-in panel's brightness the way Windows' own slider sets it (Settings and Win+A): by asking
/// the Display Enhancement Service, which is what drives the backlight on Windows 11. The older
/// interfaces (<see cref="Backlight"/>'s WMI classes, the power plan's brightness, \\.\LCD) only leave
/// a request for that service, and it can stop listening: seen on a Prestige 16 Studio after switching
/// to Integrated-only graphics, where WMI took every value and the screen never changed, the Fn keys
/// did nothing, and only Windows' slider still worked.
///
/// Windows has no public API for this, so it's the class the Settings app uses,
/// Windows.Internal.Graphics.Display.DisplayEnhancementManagement, called by position in its interface
/// (read from Microsoft's public symbols for Windows 11 24H2). It's only used on the Windows builds
/// that was checked on, only when the object answers to that exact interface, and it's dropped for good
/// the moment an answer doesn't make sense; <see cref="Backlight"/> then uses WMI as before.
/// </summary>
internal static unsafe class SliderBrightness
{
    private const string ClassName = "Windows.Internal.Graphics.Display.DisplayEnhancementManagement.DisplayEnhancementManagement";
    private static readonly Guid StaticsId = new("22771028-1658-4e5e-ab77-303691a86fda");
    private static readonly Guid PanelId = new("cda29a3e-9e7e-4b86-8f5f-368aa0710008");
    private static readonly Guid ChangedHandlerId = new("24016517-d99b-5905-a803-c876616e727c");
    private static readonly Guid AsyncInfoId = new("00000036-0000-0000-c000-000000000046");
    private static readonly Guid UnknownId = new("00000000-0000-0000-c000-000000000046");
    private static readonly Guid AgileId = new("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90");

    // Positions in the vtables (IUnknown is 0–2, IInspectable 3–5).
    private const int Release = 2;
    private const int FromIdAsync = 6;
    private const int AsyncStatus = 7;
    private const int AsyncResults = 8;
    private const int IsBrightnessCapable = 34;
    private const int IsAutobrightnessOn = 36;
    private const int GetUserPercent = 46;
    private const int PutUserPercent = 47;
    private const int GetScreenPercent = 48;
    private const int AddUserPercentChanged = 70;
    private const int RemoveUserPercentChanged = 81;

    // Windows 11 24H2 and 25H2 (one code base). Older Windows and the preview builds after them may lay
    // the interface out differently.
    private const int FirstBuild = 26100;
    private const int LastBuild = 26299;
    private static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(30);

    private static readonly object Gate = new();
    private static IntPtr _panel;
    private static long _token;
    private static bool _watch;
    private static bool _off = Environment.OSVersion.Version.Build is < FirstBuild or > LastBuild;
    private static long _retryAtMs;
    private static IntPtr _handler;

    /// <summary>What Windows' slider is set to, and what the screen shows now (they differ while dimmed, e.g. by battery saver).</summary>
    public readonly record struct Reading(int User, int Screen, bool Automatic);

    /// <summary>Windows' slider moved (from any thread): Settings, Win+A, the Fn keys, or Wattkin itself.</summary>
    public static event Action? Changed;

    /// <summary>True while brightness goes through Windows' slider (it has answered); false means WMI.</summary>
    public static bool Active => _panel != IntPtr.Zero;

    /// <summary>Null when this route isn't available (no built-in panel, another Windows version): use WMI.</summary>
    public static Reading? Read()
    {
        lock (Gate)
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (!Open())
                {
                    return null;
                }

                uint user = 0, screen = 0;
                byte automatic = 0;
                var failed = Get(GetUserPercent, &user) < 0 || Get(GetScreenPercent, &screen) < 0
                    || ((delegate* unmanaged[Stdcall]<IntPtr, byte*, int>)Slot(_panel, IsAutobrightnessOn))(_panel, &automatic) < 0;
                if (failed)
                {
                    Close(); // the display went away, or the service restarted: once more with a new object
                    continue;
                }

                if (user > 100 || screen > 100 || automatic > 1)
                {
                    GiveUp($"it answered {user}% / {screen}% / {automatic}");
                    return null;
                }

                return new Reading((int)user, (int)screen, automatic == 1);
            }

            return null;
        }
    }

    /// <summary>The slider's value in percent, or null when this route isn't available.</summary>
    public static int? Get() => Read()?.User;

    /// <summary>Moves Windows' slider. False when this route isn't available or the value didn't take.</summary>
    public static bool Set(int percent)
    {
        var wanted = (uint)Math.Clamp(percent, 0, 100);
        lock (Gate)
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (!Open())
                {
                    return false;
                }

                if (((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)Slot(_panel, PutUserPercent))(_panel, wanted) < 0)
                {
                    Close();
                    continue;
                }

                // Not read back: the service takes the value a moment after the call returns (the old one
                // for some 10–30 ms), and waiting for it made a dragged slider trail behind the hand.
                return true;
            }

            return false;
        }
    }

    /// <summary>Raise <see cref="Changed"/> from now on (kept across a new object after a display change).</summary>
    public static void Watch()
    {
        lock (Gate)
        {
            _watch = true;
            if (_panel != IntPtr.Zero && _token == 0)
            {
                Subscribe();
            }
            else
            {
                Open();
            }
        }
    }

    /// <summary>A display came or went: look for the panel again at the next call instead of in half a minute.</summary>
    public static void DisplaysChanged()
    {
        lock (Gate)
        {
            _retryAtMs = 0;
        }
    }

    /// <summary>At exit: stop the events and let go of Windows' object.</summary>
    public static void Stop()
    {
        lock (Gate)
        {
            _watch = false;
            Close();
        }
    }

    // ---- under Gate ---------------------------------------------------------------------------------

    /// <summary>Finds the display whose brightness Windows controls (the built-in panel) and keeps its object.</summary>
    private static bool Open()
    {
        if (_panel != IntPtr.Zero)
        {
            return true;
        }

        if (_off || Environment.TickCount64 < _retryAtMs)
        {
            return false;
        }

        _retryAtMs = Environment.TickCount64 + (long)RetryEvery.TotalMilliseconds;
        try
        {
            Combase.RoInitialize(1); // multithreaded; already initialised (either way) is fine
            var statics = IntPtr.Zero;
            fixed (char* name = ClassName)
            {
                if (Combase.WindowsCreateString(name, ClassName.Length, out var className) < 0)
                {
                    return false;
                }

                var id = StaticsId;
                var hr = Combase.RoGetActivationFactory(className, &id, out statics);
                Combase.WindowsDeleteString(className);
                if (hr < 0 || statics == IntPtr.Zero)
                {
                    GiveUp($"Windows doesn't have it (0x{hr:X8})");
                    return false;
                }
            }

            try
            {
                foreach (var monitor in Monitors())
                {
                    var panel = FromId(statics, monitor);
                    if (panel == IntPtr.Zero)
                    {
                        continue;
                    }

                    byte capable = 0;
                    if (((delegate* unmanaged[Stdcall]<IntPtr, byte*, int>)Slot(panel, IsBrightnessCapable))(panel, &capable) >= 0 && capable == 1)
                    {
                        _panel = panel;
                        _retryAtMs = 0; // only a search that found nothing waits before the next
                        if (_watch)
                        {
                            Subscribe();
                        }

                        return true;
                    }

                    ReleaseObject(panel);
                }
            }
            finally
            {
                ReleaseObject(statics);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or COMException)
        {
            GiveUp(ex.Message);
        }

        return false;
    }

    /// <summary>Windows' object for one monitor, as the interface this class knows, or zero.</summary>
    private static IntPtr FromId(IntPtr statics, string monitor)
    {
        IntPtr operation;
        fixed (char* text = monitor)
        {
            if (Combase.WindowsCreateString(text, monitor.Length, out var id) < 0)
            {
                return IntPtr.Zero;
            }

            var hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Slot(statics, FromIdAsync))(statics, id, &operation);
            Combase.WindowsDeleteString(id);
            if (hr < 0 || operation == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }
        }

        try
        {
            // The answer comes from the service over RPC, in milliseconds; this is under the lock, so not long.
            var info = Query(operation, AsyncInfoId);
            if (info == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            var status = 0; // 0 started, 1 completed, 2 cancelled, 3 error
            for (var waited = 0; waited < 2000 && status == 0; waited += 10)
            {
                if (((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Slot(info, AsyncStatus))(info, &status) < 0)
                {
                    status = 3;
                }
                else if (status == 0)
                {
                    Thread.Sleep(10);
                }
            }

            ReleaseObject(info);
            if (status != 1)
            {
                return IntPtr.Zero;
            }

            IntPtr result;
            if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(operation, AsyncResults))(operation, &result) < 0 || result == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            var panel = Query(result, PanelId); // zero on a Windows that changed the interface: WMI then
            ReleaseObject(result);
            return panel;
        }
        finally
        {
            ReleaseObject(operation);
        }
    }

    /// <summary>Every monitor Windows draws on, by its interface path (\\?\DISPLAY#AUODB95#…).</summary>
    private static List<string> Monitors()
    {
        var found = new List<string>();
        var adapter = new User32.DisplayDevice { Size = sizeof(User32.DisplayDevice) };
        for (uint i = 0; User32.EnumDisplayDevices(null, i, ref adapter, 0); i++)
        {
            const uint AttachedToDesktop = 0x1;
            if ((adapter.StateFlags & AttachedToDesktop) == 0)
            {
                continue;
            }

            var name = Text(adapter.DeviceName, 32);
            if (GammaRamp.MonitorId(name) is { } id && !found.Contains(id, StringComparer.OrdinalIgnoreCase))
            {
                found.Add(id);
            }
        }

        return found;
    }

    private static string Text(char* text, int length)
    {
        var s = new string(text, 0, length);
        var end = s.IndexOf('\0');
        return end >= 0 ? s[..end] : s;
    }

    private static void Subscribe()
    {
        if (_handler == IntPtr.Zero)
        {
            _handler = ChangedHandler.Create();
        }

        long token;
        if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, long*, int>)Slot(_panel, AddUserPercentChanged))(_panel, _handler, &token) >= 0)
        {
            _token = token;
        }
    }

    private static void Close()
    {
        if (_panel == IntPtr.Zero)
        {
            return;
        }

        if (_token != 0)
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, long, int>)Slot(_panel, RemoveUserPercentChanged))(_panel, _token);
            _token = 0;
        }

        ReleaseObject(_panel);
        _panel = IntPtr.Zero;
    }

    /// <summary>Not the interface this class knows after all: WMI for the rest of this run.</summary>
    private static void GiveUp(string why)
    {
        Close();
        _off = true;
        Log.Warn($"Windows' brightness slider can't be used from here ({why}); using the older WMI brightness.");
    }

    private static int Get(int slot, uint* value) => ((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)Slot(_panel, slot))(_panel, value);

    private static void* Slot(IntPtr instance, int index) => (*(void***)instance)[index];

    private static IntPtr Query(IntPtr instance, Guid id)
    {
        IntPtr result;
        return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Slot(instance, 0))(instance, &id, &result) >= 0 ? result : IntPtr.Zero;
    }

    private static void ReleaseObject(IntPtr instance)
    {
        if (instance != IntPtr.Zero)
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(instance, Release))(instance);
        }
    }

    /// <summary>
    /// The TypedEventHandler Windows calls when the slider moves: a COM object made by hand (one for the
    /// process, never freed), since the interface has no managed definition. Windows calls it on one of
    /// its own threads, so the work is passed on.
    /// </summary>
    private static class ChangedHandler
    {
        public static IntPtr Create()
        {
            var vtable = (void**)NativeMemory.Alloc(4, (nuint)sizeof(void*));
            vtable[0] = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)&QueryInterface;
            vtable[1] = (delegate* unmanaged[Stdcall]<IntPtr, uint>)&AddRef;
            vtable[2] = (delegate* unmanaged[Stdcall]<IntPtr, uint>)&AddRef; // Release: it's never freed
            vtable[3] = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, int>)&Invoke;
            var instance = (void***)NativeMemory.Alloc(1, (nuint)sizeof(void*));
            *instance = vtable;
            return (IntPtr)instance;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int QueryInterface(IntPtr self, Guid* id, IntPtr* result)
        {
            if (*id == UnknownId || *id == ChangedHandlerId || *id == AgileId)
            {
                *result = self;
                return 0;
            }

            *result = IntPtr.Zero;
            return unchecked((int)0x80004002); // E_NOINTERFACE
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static uint AddRef(IntPtr self) => 1;

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int Invoke(IntPtr self, IntPtr sender, IntPtr args)
        {
            try
            {
                Task.Run(() => Changed?.Invoke());
            }
            catch (Exception)
            {
                // Nothing may escape into Windows' thread.
            }

            return 0;
        }
    }
}
