using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Wattkin.UI;

/// <summary>
/// A whole number between − and + buttons; the arrow keys change it too once it has the focus. No text
/// box, so nothing (like Windows 11's clear button) covers the value while it's edited. The mouse wheel
/// is left to the page, so scrolling past a stepper never changes it.
/// </summary>
public sealed class NumberStepper : Border
{
    private readonly TextBlock _text = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MinWidth = 24, TextAlignment = TextAlignment.Center };
    private int _value;

    public NumberStepper(int minimum, int maximum, int value)
    {
        Minimum = minimum;
        Maximum = maximum;
        _value = Math.Clamp(value, minimum, maximum);
        Focusable = true;
        CornerRadius = new CornerRadius(4);
        BorderThickness = new Thickness(1);
        Margin = new Thickness(0, 2, 0, 2);
        HorizontalAlignment = HorizontalAlignment.Center;
        SetResourceReference(BorderBrushProperty, "Lc.CardBorder");
        SetResourceReference(BackgroundProperty, "Lc.Window");
        _text.SetResourceReference(TextBlock.ForegroundProperty, "Lc.Text");

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var minus = Step("−", -1);
        var plus = Step("+", 1);
        Grid.SetColumn(_text, 1);
        Grid.SetColumn(plus, 2);
        grid.Children.Add(minus);
        grid.Children.Add(_text);
        grid.Children.Add(plus);
        Child = grid;
        Show();

        PreviewKeyDown += (_, e) =>
        {
            var delta = e.Key switch { Key.Up or Key.Right => 1, Key.Down or Key.Left => -1, Key.PageUp => 5, Key.PageDown => -5, _ => 0 };
            if (delta != 0)
            {
                Value += delta;
                e.Handled = true;
            }
        };
        MouseLeftButtonDown += (_, _) => Focus();
    }

    public int Minimum { get; }
    public int Maximum { get; }

    /// <summary>How the value reads (e.g. an index into a list of durations); the number itself by default.</summary>
    public Func<int, string>? Format
    {
        get => _format;
        set
        {
            _format = value;
            Show();
        }
    }

    private Func<int, string>? _format;

    public event Action? ValueChanged;

    public int Value
    {
        get => _value;
        set
        {
            var clamped = Math.Clamp(value, Minimum, Maximum);
            if (clamped == _value)
            {
                return;
            }

            _value = clamped;
            Show();
            ValueChanged?.Invoke();
        }
    }

    private void Show() => _text.Text = _format?.Invoke(_value) ?? _value.ToString(CultureInfo.CurrentCulture);

    private RepeatButton Step(string glyph, int delta)
    {
        var button = new RepeatButton
        {
            Content = glyph,
            Width = 22,
            MinWidth = 0,
            MinHeight = 0,
            Height = 24,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = System.Windows.Media.Brushes.Transparent,
            Focusable = false,
            Delay = 400,
            Interval = 80,
        };
        button.SetResourceReference(Control.ForegroundProperty, "Lc.SubText");
        button.Click += (_, _) => Value += delta;
        return button;
    }
}
