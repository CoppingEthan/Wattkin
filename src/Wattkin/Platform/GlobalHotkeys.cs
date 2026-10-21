using System.Runtime.InteropServices;
using System.Windows.Interop;
using Wattkin.Native;

namespace Wattkin.Platform;

/// <summary>
/// System-wide key combinations (RegisterHotKey) on a hidden window of the UI thread: Windows sends
/// WM_HOTKEY whichever app has the focus. Only one program can hold a combination; f.lux holds
/// Alt+Page Up / Page Down while it runs, so <see cref="Register"/> fails until it's closed.
/// </summary>
internal sealed class GlobalHotkeys : IDisposable
{
    private readonly HwndSource _window;
    private readonly Dictionary<int, Action> _actions = [];

    /// <summary>Call on the UI thread (the window lives there).</summary>
    public GlobalHotkeys()
    {
        _window = new HwndSource(new HwndSourceParameters("Wattkin hotkeys") { WindowStyle = 0, ExtendedWindowStyle = User32.WS_EX_TOOLWINDOW, Width = 0, Height = 0 });
        _window.AddHook(Hook);
    }

    /// <summary>Holds <paramref name="key"/> with <paramref name="modifiers"/>. Null on success, else why not.</summary>
    public string? Register(int id, uint modifiers, uint key, Action pressed)
    {
        if (_actions.ContainsKey(id))
        {
            return null;
        }

        if (!User32.RegisterHotKey(_window.Handle, id, modifiers | User32.MOD_NOREPEAT, key))
        {
            var error = Marshal.GetLastWin32Error();
            return error == 1409 ? "another app is using these keys" : $"Windows refused them (error {error})";
        }

        _actions[id] = pressed;
        return null;
    }

    public void UnregisterAll()
    {
        foreach (var id in _actions.Keys)
        {
            User32.UnregisterHotKey(_window.Handle, id);
        }

        _actions.Clear();
    }

    public bool IsRegistered(int id) => _actions.ContainsKey(id);

    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == User32.WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Log.Error("Hotkey handler failed", ex);
            }
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        _window.Dispose();
    }
}
