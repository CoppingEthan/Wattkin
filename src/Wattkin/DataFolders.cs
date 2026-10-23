namespace Wattkin;

/// <summary>
/// Where Wattkin keeps its files: settings in %APPDATA%\Wattkin, logs and battery history in
/// %LOCALAPPDATA%\Wattkin. The portable build (Wattkin-portable.zip: self-contained, made with
/// WattkinPortable=true) keeps all of them in a "data" folder next to its exe instead, and never installs
/// itself: an installed self-contained copy would update to the Wattkin.exe that needs .NET.
/// </summary>
public static class DataFolders
{
    public static bool Portable { get; } = string.Equals(
        typeof(DataFolders).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .OfType<System.Reflection.AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "WattkinPortable")?.Value,
        "true", StringComparison.OrdinalIgnoreCase);

    private static readonly string? PortableData = Portable && Path.GetDirectoryName(Environment.ProcessPath) is { } folder ? Path.Combine(folder, "data") : null;

    /// <summary>settings.json.</summary>
    public static string Settings { get; } = PortableData ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Wattkin");

    /// <summary>Logs, battery history and reports.</summary>
    public static string Local { get; } = PortableData ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wattkin");
}
