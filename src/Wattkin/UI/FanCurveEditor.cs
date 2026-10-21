using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Wattkin.Hardware;

namespace Wattkin.UI;

/// <summary>
/// A fan curve as two rows of vertical sliders, CPU fan and GPU fan: one per temperature step of the
/// laptop's own table, so it reads like the curve it is. The hottest step is shown but fixed: it stays
/// the firmware's, as on MSI Center's fan page.
/// </summary>
public sealed class FanCurveEditor : StackPanel
{
    private readonly Slider[] _cpu = new Slider[FanSpeeds.Steps];
    private readonly Slider[] _gpu = new Slider[FanSpeeds.Steps];
    private readonly Grid _cpuGrid = new();
    private readonly Grid _gpuGrid = new();
    private readonly TextBlock _gpuHeader = Header("GPU fan", top: 10);
    private FanCurves? _firmware;
    private bool _loading;

    public FanCurveEditor()
    {
        Children.Add(Header("CPU fan"));
        Children.Add(_cpuGrid);
        Children.Add(_gpuHeader);
        Children.Add(_gpuGrid);
    }

    public event Action? Changed;

    public FanSpeeds Value => new()
    {
        Cpu = [.. _cpu.Select(s => (int)(s?.Value ?? 0))],
        Gpu = [.. _gpu.Select(s => (int)(s?.Value ?? 0))],
    };

    /// <summary>Shows <paramref name="speeds"/> at the steps of the laptop's table (<paramref name="firmware"/>).</summary>
    public void Show(FanCurves firmware, FanSpeeds speeds)
    {
        _firmware = firmware;
        _loading = true;
        try
        {
            Build(_cpuGrid, _cpu, firmware.Cpu, speeds.Cpu);
            Build(_gpuGrid, _gpu, firmware.Gpu, speeds.Gpu);

            // A laptop with one fan: no GPU row (its table is all zeros and never written).
            _gpuHeader.Visibility = _gpuGrid.Visibility = firmware.HasGpuFan ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>What's wrong with the curve as set, or null (see <see cref="FanSpeeds.Problem"/>).</summary>
    public string? Problem(bool unrestricted)
    {
        var value = Value;
        if (value.Problem(unrestricted, _firmware) is { } problem)
        {
            return problem;
        }

        if (unrestricted || _firmware is not { } firmware)
        {
            return null;
        }

        // The fixed hottest step can't be slower than the one below it.
        foreach (var (name, speeds, table) in new[] { ("CPU", value.Cpu, firmware.Cpu), ("GPU", value.Gpu, firmware.Gpu) })
        {
            var top = table.Speeds[^1];
            if (!table.Absent && speeds[^1] > top)
            {
                return $"The {name} fan can't be faster than {top}% here: the hottest step is fixed at {top}%.";
            }
        }

        return null;
    }

    private void Build(Grid grid, Slider[] sliders, FanCurve table, int[] speeds)
    {
        grid.Children.Clear();
        grid.ColumnDefinitions.Clear();
        for (var i = 0; i <= FanSpeeds.Steps; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var fixedStep = i == FanSpeeds.Steps;
            var value = fixedStep ? table.Speeds[^1] : (i < speeds.Length ? speeds[i] : table.Speeds[i]);
            var label = new TextBlock { Text = Percent(value), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 4) };
            label.SetResourceReference(TextBlock.ForegroundProperty, fixedStep ? "Lc.Faint" : "Lc.Text");
            var slider = new Slider
            {
                Orientation = Orientation.Vertical,
                Height = 110,
                Minimum = 0,
                Maximum = FanSpeeds.Max,
                TickFrequency = 5,
                IsSnapToTickEnabled = true,
                SmallChange = 5,
                LargeChange = 10,
                Value = value,
                IsEnabled = !fixedStep,
                HorizontalAlignment = HorizontalAlignment.Center,
                ToolTip = fixedStep ? "Fixed by the laptop's firmware" : null,
            };
            slider.ValueChanged += (_, _) =>
            {
                label.Text = Percent((int)slider.Value);
                if (!_loading)
                {
                    Changed?.Invoke();
                }
            };

            // Below the first step, then "from" each step; the hottest one is the firmware's.
            var temp = i == 0 ? $"<{table.Temps[0]}°C" : $"{table.Temps[i - 1]}°C";
            var caption = new TextBlock { Text = fixedStep ? temp + "\nfixed" : temp, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0), FontSize = 11.5 };
            caption.SetResourceReference(TextBlock.ForegroundProperty, "Lc.SubText");

            var column = new StackPanel();
            column.Children.Add(label);
            column.Children.Add(slider);
            column.Children.Add(caption);
            Grid.SetColumn(column, i);
            grid.Children.Add(column);
            if (!fixedStep)
            {
                sliders[i] = slider;
            }
        }
    }

    private static string Percent(int value) => value == 0 ? "off" : value.ToString(CultureInfo.CurrentCulture) + "%";

    private static TextBlock Header(string text, double top = 0)
    {
        var block = new TextBlock { Text = text, Margin = new Thickness(0, top, 0, 6), FontWeight = FontWeights.SemiBold };
        block.SetResourceReference(TextBlock.ForegroundProperty, "Lc.Text");
        return block;
    }
}
