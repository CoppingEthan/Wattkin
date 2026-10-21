using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Wattkin.Hardware;

namespace Wattkin.UI;

/// <summary>
/// A whole fan table, for laptops where Wattkin writes the temperatures too (<see cref="FanTable"/>):
/// for each fan a vertical slider per step for its speed, and under each step above the first the
/// temperature that starts it and the one under which the fan drops back, as in YAMDCC's editor.
/// Temperatures are steppers (− 45 +, or the arrow keys), never free text.
/// </summary>
public sealed class FanTableEditor : StackPanel
{
    private readonly Columns _cpu = new();
    private readonly Columns _gpu = new();
    private bool _loading;

    public FanTableEditor()
    {
        Children.Add(Header("CPU fan"));
        Children.Add(_cpu.Grid);
        Children.Add(Header("GPU fan", top: 12));
        Children.Add(_gpu.Grid);
        var hint = new TextBlock
        {
            Text = "A step starts when the temperature reaches \"Up at\" and ends when it falls below \"Down under\": the gap keeps the fan from hunting between two speeds. Change a temperature with − and +, or click it and use the arrow keys. Speeds are MSI's percentages; Cooler Boost is full speed.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0),
            FontSize = 11.5,
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Lc.SubText");
        Children.Add(hint);
    }

    public event Action? Changed;

    /// <summary>The table as set.</summary>
    public FanTable Value => new() { Cpu = _cpu.Read(), Gpu = _gpu.Read() };

    public void Show(FanTable table)
    {
        _loading = true;
        try
        {
            Build(_cpu, table.Cpu);
            Build(_gpu, table.Gpu);
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>What's wrong with the table as set, or null (see <see cref="FanTable.Problem"/>).</summary>
    public string? Problem(bool unrestricted) => Value.Problem(unrestricted);

    private void Build(Columns columns, FanSteps steps)
    {
        var grid = columns.Grid;
        grid.Children.Clear();
        grid.ColumnDefinitions.Clear();
        grid.RowDefinitions.Clear();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (var i = 0; i < FanSteps.Count; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        for (var r = 0; r < 5; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        Place(grid, RowLabel("Speed"), 1, 0);
        Place(grid, RowLabel("Up at °C"), 3, 0);
        Place(grid, RowLabel("Down under °C"), 4, 0);

        for (var i = 0; i < FanSteps.Count; i++)
        {
            var value = i < steps.Speeds.Length ? steps.Speeds[i] : 0;
            var label = new TextBlock { Text = Percent(value), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 4) };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Lc.Text");
            var slider = new Slider
            {
                Orientation = Orientation.Vertical,
                Height = 110,
                Minimum = 0,
                Maximum = FanSpeeds.Max,
                TickFrequency = 1,
                IsSnapToTickEnabled = true,
                SmallChange = 1,
                LargeChange = 5,
                Value = value,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            slider.ValueChanged += (_, _) =>
            {
                label.Text = Percent((int)slider.Value);
                Notify();
            };
            columns.Speeds[i] = slider;

            var caption = new TextBlock { Text = i == 0 ? "base" : $"step {i}", HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 4), FontSize = 11.5 };
            caption.SetResourceReference(TextBlock.ForegroundProperty, "Lc.SubText");

            Place(grid, label, 0, i + 1);
            Place(grid, slider, 1, i + 1);
            Place(grid, caption, 2, i + 1);
            if (i == 0)
            {
                // The base speed runs below step 1: it has no temperatures of its own.
                Place(grid, Dash(), 3, 1);
                Place(grid, Dash(), 4, 1);
                continue;
            }

            columns.Up[i - 1] = Stepper(FanTable.MinTemp, FanTable.MaxTemp, i - 1 < steps.Up.Length ? steps.Up[i - 1] : FanTable.MinTemp);
            columns.Down[i - 1] = Stepper(0, FanTable.MaxTemp, i - 1 < steps.Down.Length ? steps.Down[i - 1] : 0);
            Place(grid, columns.Up[i - 1], 3, i + 1);
            Place(grid, columns.Down[i - 1], 4, i + 1);
        }
    }

    private NumberStepper Stepper(int minimum, int maximum, int value)
    {
        var stepper = new NumberStepper(minimum, maximum, value);
        stepper.ValueChanged += Notify;
        return stepper;
    }

    private void Notify()
    {
        if (!_loading)
        {
            Changed?.Invoke();
        }
    }

    private static void Place(Grid grid, UIElement element, int row, int column)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    private static TextBlock RowLabel(string text)
    {
        var block = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), FontSize = 11.5 };
        block.SetResourceReference(TextBlock.ForegroundProperty, "Lc.SubText");
        return block;
    }

    private static TextBlock Dash()
    {
        var block = new TextBlock { Text = "–", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        block.SetResourceReference(TextBlock.ForegroundProperty, "Lc.Faint");
        return block;
    }

    private static string Percent(int value) => value == 0 ? "off" : value.ToString(CultureInfo.CurrentCulture) + "%";

    private static TextBlock Header(string text, double top = 0)
    {
        var block = new TextBlock { Text = text, Margin = new Thickness(0, top, 0, 6), FontWeight = FontWeights.SemiBold };
        block.SetResourceReference(TextBlock.ForegroundProperty, "Lc.Text");
        return block;
    }

    /// <summary>One fan's controls.</summary>
    private sealed class Columns
    {
        public Grid Grid { get; } = new();
        public Slider[] Speeds { get; } = new Slider[FanSteps.Count];
        public NumberStepper[] Up { get; } = new NumberStepper[FanSteps.Thresholds];
        public NumberStepper[] Down { get; } = new NumberStepper[FanSteps.Thresholds];

        public FanSteps Read() => new()
        {
            Speeds = [.. Speeds.Select(s => (int)(s?.Value ?? 0))],
            Up = [.. Up.Select(s => s?.Value ?? 0)],
            Down = [.. Down.Select(s => s?.Value ?? 0)],
        };
    }
}
