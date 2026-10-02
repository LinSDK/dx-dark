using System.Diagnostics;

namespace DxDark.Core;

/// <summary>Minimal local log file (%APPDATA%\DX Dark\dxdark.log, capped at ~1 MB). Never leaves the PC.</summary>
public static class Log
{
    private const long MaxBytes = 1_000_000;
    private static readonly object Gate = new();
    private static string? _path;

    public static string? FilePath => _path;

    public static void Initialize(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "dxdark.log");
    }

    public static void Info(string message) => Write("INFO ", message);

    public static void Warn(string message) => Write("WARN ", message);

    public static void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : $"{message}: {exception}");

    private static void Write(string level, string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}";
        Debug.WriteLine(line);
        if (_path is null)
        {
            return;
        }

        lock (Gate)
        {
            try
            {
                var file = new FileInfo(_path);
                if (file.Exists && file.Length > MaxBytes)
                {
                    File.Move(_path, _path + ".old", overwrite: true);
                }

                File.AppendAllText(_path, line + Environment.NewLine);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
