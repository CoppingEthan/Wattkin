using Wattkin.Settings;

namespace Wattkin.UI;

/// <summary>
/// How long without typing before auto-off, where Wattkin does it itself (the USB keyboard light): a
/// stepper that walks through a few durations within the settings' bounds (5 s to 5 min). A value from a
/// hand-edited file shows as the nearest one; only a change by the user is saved.
/// </summary>
internal sealed class AutoOffDelayStepper
{
    public static readonly int[] Choices = [5, 10, 15, 20, 30, 45, 60, 90, 120, 180, AppSettings.KeyboardAutoOffMaxSeconds];

    private bool _loading;

    public AutoOffDelayStepper()
    {
        Control = new NumberStepper(0, Choices.Length - 1, Nearest(10)) { Format = i => Fmt.Delay(Choices[i]), ToolTip = "How long without typing before the light goes off" };
        Control.ValueChanged += async () =>
        {
            if (!_loading)
            {
                await App.Current.Hardware.SetKeyboardAutoOffSecondsAsync(Choices[Control.Value]);
            }
        };
    }

    public NumberStepper Control { get; }

    public void Show(int seconds)
    {
        _loading = true;
        try
        {
            Control.Value = Nearest(seconds);
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>The index of the choice closest to <paramref name="seconds"/>.</summary>
    internal static int Nearest(int seconds) =>
        Array.IndexOf(Choices, Choices.MinBy(c => Math.Abs(c - seconds)));
}
