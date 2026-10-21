using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Wattkin.Features;
using Wattkin.Hardware;
using Wattkin.Platform;

namespace Wattkin;

/// <summary>
/// "Help support this laptop": everything someone needs to add or fix their laptop, in one GitHub
/// issue. A report is saved on the desktop (what Wattkin can control here, in plain words, then the
/// read-only diagnostics), and GitHub's laptop-report form opens with the model, version and that
/// summary filled in, next to the file to drag in. Nothing is sent by Wattkin: the person sees it all
/// and posts it themselves. Nothing is written to the hardware either.
/// </summary>
internal static class LaptopReport
{
    private const string NewIssue = "https://github.com/CoppingEthan/Wattkin/issues/new";

    /// <summary>
    /// UI thread: asks first, then reads the hardware in the background (about 15 s, shown on
    /// <paramref name="button"/>) and opens the form and the report.
    /// </summary>
    public static async Task OfferAsync(Window? owner, Button button)
    {
        const string text = "Wattkin will save a report about this laptop on your desktop and open GitHub's form for posting it. It only reads the hardware (about 15 seconds).\n\n"
            + "Drag the report into the form's last box, add what works and what doesn't for you, and post it. Nothing is sent unless you post it.\n\nGo ahead?";
        var answer = owner is null
            ? MessageBox.Show(text, "Help support this laptop", MessageBoxButton.OKCancel, MessageBoxImage.Information)
            : MessageBox.Show(owner, text, "Help support this laptop", MessageBoxButton.OKCancel, MessageBoxImage.Information);
        if (answer != MessageBoxResult.OK)
        {
            return;
        }

        var label = button.Content;
        button.IsEnabled = false;
        button.Content = "Reading the hardware…";
        try
        {
            var app = App.Current;
            var state = app.Hardware.State;
            var backlight = app.Screen.HasBacklight;
            var model = app.Monitor.Info?.Model;
            var (path, url) = await Task.Run(() =>
            {
                var summary = Summary(state, app.Hardware.ReadFanCurves()?.HasGpuFan, backlight);
                var laptop = model ?? Monitoring.StaticSystemInfo.Read(new Monitoring.CpuTopology()).Model;
                return (Save(summary), IssueUrl(laptop, Installation.CurrentVersion.ToString(3), summary, state.MsiSoftwareRunning));
            });

            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            Log.Info($"Laptop report saved to {path}; GitHub's form opened.");
        }
        catch (Exception ex)
        {
            Log.Error("Laptop report failed", ex);
            MessageBox.Show(ex.Message, "Help support this laptop", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            button.Content = label;
            button.IsEnabled = true;
        }
    }

    /// <summary>The summary, then the full diagnostics, on the desktop. Its path.</summary>
    private static string Save(string summary)
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"Wattkin-laptop-report-{DateTime.Now:yyyyMMdd-HHmm}.txt");
        var text = $"{summary}\n\n{Diagnostics.Build()}";
        File.WriteAllText(path, text.Replace("\r\n", "\n").Replace("\n", Environment.NewLine), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return path;
    }

    /// <summary>What Wattkin can control on this laptop, in plain words: the start of the issue.</summary>
    /// <param name="gpuFan">Whether the fan table has a second fan; null when it couldn't be read.</param>
    internal static string Summary(HardwareState state, bool? gpuFan, bool backlight)
    {
        var caps = state.Caps;
        var lines = new List<string>
        {
            "Wattkin's own check (it only reads the hardware):",
            $"- Support: {SupportWords(state)}",
            $"- Firmware: EC {state.EcFirmware ?? "unknown"}, interface {state.WmiVersion ?? "unknown"}",
        };

        if (caps is not null && state.CanWrite)
        {
            lines.Add($"- Charge limit: yes{(state.EcChargeLimit is { } limit ? $" (now {limit}%)" : "")}");
            lines.Add($"- Performance modes: yes{(caps.HasSportMode ? ", Sport too" : "")}");
            lines.Add($"- Fans: {FanWords(caps, gpuFan)}");
            lines.Add($"- Keyboard light: {KeyboardWords(caps)}");
            lines.Add($"- GPU switch: {(caps.CanSwitchGpuMode && state.Gpu is { SwitchSupported: true } ? "yes" : "no")}");
        }
        else
        {
            lines.Add("- Controls: off (monitoring only)");
        }

        lines.Add($"- Screen brightness: {(backlight ? "backlight and extra dim" : "extra dim only (no backlight Windows controls)")}");
        lines.Add($"- MSI Center: {(state.MsiSoftwareRunning ? "running" : "not running")}");
        return string.Join("\n", lines);
    }

    private static string SupportWords(HardwareState state) => state.Caps switch
    {
        { Support: EcSupport.Verified } caps => $"verified on this firmware ({caps.MapSource})",
        { Support: EcSupport.Community } caps => $"msi-ec register map ({caps.MapSource}), passed Wattkin's safety check",
        { Support: EcSupport.Standard } => "MSI's standard layout, passed Wattkin's safety check",
        _ => $"monitoring only{(state.Problem is { } problem ? $": {problem}" : "")}",
    };

    private static string FanWords(EcCapabilities caps, bool? gpuFan)
    {
        var curves = !caps.HasFanCurves ? "modes only, no curves"
            : caps.HasFullFanTable ? "whole fan tables (temperatures too)"
            : gpuFan == false ? "curves (one fan)"
            : gpuFan == true ? "curves (two fans)"
            : "curves";
        return $"{curves}; Cooler Boost {(caps.HasCoolerBoost ? "yes" : "no")}";
    }

    private static string KeyboardWords(EcCapabilities caps) =>
        caps.HasKeyboardColor ? "USB Mystic Light (levels, colour, Wattkin's own auto-off)"
        : caps.HasKeyboardLight ? $"levels 0-{caps.KeyboardMaxLevel}{(caps.HasKeyboardTimeout ? ", auto-off" : "")}"
        : "not controllable";

    /// <summary>
    /// GitHub's laptop-report form (.github/ISSUE_TEMPLATE/laptop-report.yml) with its fields filled in
    /// through the address, as GitHub allows for issue forms. The report itself is dragged in: a file
    /// can't go in an address.
    /// </summary>
    internal static string IssueUrl(string model, string version, string summary, bool msiRunning)
    {
        var fields = new List<(string Name, string Value)>
        {
            ("template", "laptop-report.yml"),
            ("title", $"Laptop report: {model}"),
            ("model", model),
            ("version", version),
            ("what-happened", $"{summary}\n\nWhat works and what doesn't for me:\n"),
        };
        if (msiRunning)
        {
            fields.Add(("msi-center", "Still installed"));
        }

        return NewIssue + "?" + string.Join("&", fields.Select(f => $"{f.Name}={Uri.EscapeDataString(f.Value)}"));
    }
}
