using System.Text;

namespace Wattkin;

/// <summary>Small append-only log in %LOCALAPPDATA%\Wattkin\logs (<see cref="DataFolders"/>). Every EC write is recorded here.</summary>
public static class Log
{
    private const long MaxBytes = 2 * 1024 * 1024;
    private static readonly object Gate = new();

    public static string Directory { get; } = Environment.GetEnvironmentVariable("WATTKIN_LOG_DIR") is { Length: > 0 } custom
        ? custom
        : Path.Combine(DataFolders.Local, "logs");
    public static string FilePath { get; } = Path.Combine(Directory, "wattkin.log");

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(Directory);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > MaxBytes)
                {
                    File.Move(FilePath, FilePath + ".1", overwrite: true);
                }

                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch (IOException)
        {
            // Logging must never take the app down.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
