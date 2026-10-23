using System.Net.Http;
using System.Text;
using Wattkin.Platform;
using Wattkin.Settings;

namespace Wattkin.Features;

public enum UpdatePhase
{
    Idle,
    Checking,
    UpToDate,
    Available,
    Installing,
    Failed,
}

public sealed record UpdateState(UpdatePhase Phase, string Message, ReleaseInfo? Available);

/// <summary>
/// Keeps the installed Wattkin current from GitHub releases. Its only network traffic: one request a
/// day for the latest release (plus "Check now"), and the download when there is a newer one. The new
/// exe is checked against the built-in release key before it replaces anything (<see cref="Releases"/>),
/// and it's swapped in only when restarting is harmless: no calibration running, no window open.
/// </summary>
internal sealed class Updater : IDisposable
{
    private const string LatestRelease = "https://api.github.com/repos/CoppingEthan/Wattkin/releases/latest";
    private static readonly TimeSpan CheckEvery = TimeSpan.FromHours(24);
    private static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan Tick = TimeSpan.FromHours(1);

    private readonly SettingsStore _store;
    private readonly Func<bool> _safeToRestart;
    private readonly Func<bool> _restartIntoUpdate;
    private readonly Timer _timer;
    private int _busy;

    /// <param name="safeToRestart">True when a restart now wouldn't interrupt anything.</param>
    /// <param name="restartIntoUpdate">Starts the swapped-in exe and exits; false if it couldn't start.</param>
    public Updater(SettingsStore store, Func<bool> safeToRestart, Func<bool> restartIntoUpdate)
    {
        _store = store;
        _safeToRestart = safeToRestart;
        _restartIntoUpdate = restartIntoUpdate;
        State = new UpdateState(UpdatePhase.Idle, Describe(store.Current.LastUpdateCheckUtc), null);
        _timer = new Timer(_ => _ = RunAsync(userAsked: false), null, FirstCheck, Tick);
    }

    public UpdateState State { get; private set; }

    public event Action? Changed;

    /// <summary>Updates replace the copy in Program Files, so only that copy, running as administrator, installs them.</summary>
    public static bool CanInstall => Installation.RunningInstalled && Elevation.IsElevated && Enabled;

    /// <summary>
    /// False in a fork's build that turned updates off (WattkinUpdates in Wattkin.csproj): CoppingEthan's
    /// releases would otherwise replace it as soon as one had a higher version.
    /// </summary>
    public static bool Enabled { get; } = !string.Equals(
        typeof(Updater).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .OfType<System.Reflection.AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "WattkinUpdates")?.Value,
        "false", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The hourly tick and "Check now". Checks GitHub when a day has passed (always when asked), and with
    /// automatic updates on installs a newer release once restarting is harmless.
    /// </summary>
    public async Task RunAsync(bool userAsked)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1)
        {
            return;
        }

        try
        {
            var settings = _store.Current;
            if (!Enabled)
            {
                Set(UpdatePhase.Idle, "Updates are turned off in this build.", null);
                return;
            }

            if (!userAsked && !settings.AutoUpdate)
            {
                return;
            }

            var due = settings.LastUpdateCheckUtc is not { } last || DateTime.UtcNow - last >= CheckEvery;
            if (userAsked || (due && State.Available is null))
            {
                await CheckAsync();
            }

            if (State.Available is { } release && settings.AutoUpdate && CanInstall && _safeToRestart())
            {
                await InstallAsync(release, automatic: true);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Update check failed", ex);
            Set(UpdatePhase.Failed, "Couldn't check for updates.", State.Available);
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    /// <summary>"Install now" when automatic updates are off.</summary>
    public async Task InstallNowAsync()
    {
        if (State.Available is not { } release || Interlocked.Exchange(ref _busy, 1) == 1)
        {
            return;
        }

        try
        {
            await InstallAsync(release, automatic: false);
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private async Task CheckAsync()
    {
        Set(UpdatePhase.Checking, "Checking for updates…", State.Available);
        using var http = Client();
        using var response = await http.GetAsync(LatestRelease);
        _store.Update(s => s.LastUpdateCheckUtc = DateTime.UtcNow);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            Set(UpdatePhase.Failed, "No public release found yet. Updates start once releases are published on GitHub.", null);
            return;
        }

        if (!response.IsSuccessStatusCode)
        {
            Set(UpdatePhase.Failed, $"GitHub didn't answer ({(int)response.StatusCode}). Trying again later.", State.Available);
            return;
        }

        var release = Releases.Parse(await response.Content.ReadAsStringAsync(), out var problem);
        if (release is null)
        {
            Set(UpdatePhase.Failed, problem ?? "No usable release.", null);
            return;
        }

        if (release.Version <= Installation.CurrentVersion)
        {
            Set(UpdatePhase.UpToDate, $"Up to date: {Installation.CurrentVersion.ToString(3)}. Checked {DateTime.Now:HH:mm}.", null);
            return;
        }

        var when = DataFolders.Portable ? "Download the new portable zip from GitHub."
            : _store.Current.AutoUpdate ? "It installs by itself when Wattkin's windows are closed." : "";
        Set(UpdatePhase.Available, $"Wattkin {release.Version.ToString(3)} is available. {when}".Trim(), release);
    }

    /// <param name="automatic">
    /// Not asked for by the user: restarting must still be harmless once the download is done (a slow
    /// one gives time to open Settings or start a calibration). If it isn't, the next hourly tick retries.
    /// </param>
    private async Task InstallAsync(ReleaseInfo release, bool automatic)
    {
        if (!CanInstall)
        {
            Set(UpdatePhase.Failed, "Updates install into the copy in Program Files, running as administrator.", release);
            return;
        }

        var version = release.Version.ToString(3);
        Set(UpdatePhase.Installing, $"Downloading Wattkin {version}…", release);
        using var http = Client();
        var exe = await DownloadAsync(http, release.ExeUrl, Releases.MaxExeBytes);
        var signature = await DownloadAsync(http, release.SignatureUrl, Releases.MaxSignatureBytes);
        if (exe is null || exe.Length != release.ExeSize || signature is null)
        {
            Set(UpdatePhase.Failed, $"The download of {version} didn't complete. Trying again later.", release);
            return;
        }

        if (!Releases.Verify(release.Version, exe, Encoding.ASCII.GetString(signature)))
        {
            Log.Warn($"Update {version} failed its signature check; not installed.");
            Set(UpdatePhase.Failed, $"Wattkin {version} failed its signature check, so it wasn't installed.", null);
            return;
        }

        if (automatic && !_safeToRestart())
        {
            Set(UpdatePhase.Available, $"Wattkin {version} is available. It installs by itself when Wattkin's windows are closed.", release);
            return;
        }

        if (Installation.StageUpdate(exe, release.Version) is { } error)
        {
            Set(UpdatePhase.Failed, error, release);
            return;
        }

        Log.Info($"Updating to Wattkin {version}: restarting.");
        if (!_restartIntoUpdate())
        {
            Installation.RollBack();
            Set(UpdatePhase.Failed, $"Wattkin {version} couldn't start, so the current version was kept.", release);
        }
    }

    private static HttpClient Client()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Wattkin/{Installation.CurrentVersion.ToString(3)}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    /// <summary>Reads at most <paramref name="limit"/> bytes; null if the file is bigger or the request fails.</summary>
    private static async Task<byte[]?> DownloadAsync(HttpClient http, Uri url, long limit)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > limit)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static string Describe(DateTime? lastCheckUtc) =>
        lastCheckUtc is { } t ? $"Last checked {t.ToLocalTime():d MMM HH:mm}." : "Not checked yet.";

    private void Set(UpdatePhase phase, string message, ReleaseInfo? available)
    {
        State = new UpdateState(phase, message, available);
        Changed?.Invoke();
    }

    public void Dispose() => _timer.Dispose();
}
