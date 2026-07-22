using System.Text;
using MyBookingGts.Configuration;

namespace MyBookingGts.Infrastructure;

public sealed class AppLogger
{
    private readonly object _sync = new();
    private readonly string _path;
    private readonly long _maximumBytes;
    private readonly int _retainedFiles;

    public AppLogger(LoggingConfig config)
    {
        _path = Path.GetFullPath(config.LogFilePath);
        _maximumBytes = config.MaximumLogFileMegabytes * 1024L * 1024L;
        _retainedFiles = config.RetainedLogFiles;

        var directory = Path.GetDirectoryName(_path)
                        ?? throw new InvalidOperationException("Log file directory cannot be determined.");
        Directory.CreateDirectory(directory);

        RotateIfNeeded();
    }

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);

    public void Error(Exception exception, string message)
    {
        Write("ERROR", $"{message}{Environment.NewLine}{exception}");
    }

    private void Write(string level, string message)
    {
        lock (_sync)
        {
            RotateIfNeeded();

            var normalized = message.Replace("\r\n", "\n").Replace('\r', '\n');
            var lines = normalized.Split('\n');
            var timestamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz");

            using var writer = new StreamWriter(_path, append: true, new UTF8Encoding(false));
            foreach (var line in lines)
            {
                var output = $"[{timestamp}] [{level}] {line}";
                Console.WriteLine(output);
                writer.WriteLine(output);
            }
        }
    }

    private void RotateIfNeeded()
    {
        var file = new FileInfo(_path);
        if (!file.Exists || file.Length < _maximumBytes)
        {
            return;
        }

        for (var index = _retainedFiles; index >= 1; index--)
        {
            var source = index == 1 ? _path : GetRotatedPath(index - 1);
            var destination = GetRotatedPath(index);

            if (!File.Exists(source))
            {
                continue;
            }

            if (File.Exists(destination))
            {
                File.Delete(destination);
            }

            File.Move(source, destination);
        }
    }

    private string GetRotatedPath(int index)
    {
        var directory = Path.GetDirectoryName(_path)!;
        var name = Path.GetFileNameWithoutExtension(_path);
        var extension = Path.GetExtension(_path);
        return Path.Combine(directory, $"{name}.{index}{extension}");
    }
}
