using System.IO;

namespace ObeliskLauncher;

public sealed class ServerLogProbe
{
    private static readonly string[] ReadyPatterns =
    [
        "GameNetDriver",
        "listening",
        "Server connected",
        "server is ready",
        "Match State Changed",
        "WaitingToStart",
        "Full startup"
    ];

    public bool LooksReady(string executablePath, DateTime startedAtUtc)
    {
        var logFile = FindNewestLogFile(executablePath, startedAtUtc);
        if (logFile is null)
        {
            return false;
        }

        var tail = ReadTail(logFile, 128 * 1024);
        if (string.IsNullOrWhiteSpace(tail))
        {
            return false;
        }

        var lower = tail.ToLowerInvariant();
        return ReadyPatterns.Any(pattern => lower.Contains(pattern.ToLowerInvariant()))
               && lower.Contains("listen");
    }

    private static string? FindNewestLogFile(string executablePath, DateTime startedAtUtc)
    {
        var executableDirectory = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrWhiteSpace(executableDirectory))
        {
            return null;
        }

        var candidates = EnumerateLogDirectories(executableDirectory)
            .Where(Directory.Exists)
            .SelectMany(directory => SafeEnumerateLogs(directory))
            .Where(file =>
            {
                try
                {
                    return File.GetLastWriteTimeUtc(file) >= startedAtUtc.AddMinutes(-1);
                }
                catch
                {
                    return false;
                }
            })
            .OrderByDescending(file => File.GetLastWriteTimeUtc(file))
            .ToList();

        return candidates.FirstOrDefault();
    }

    private static IEnumerable<string> EnumerateLogDirectories(string executableDirectory)
    {
        yield return Path.Combine(executableDirectory, "Saved", "Logs");

        var directory = new DirectoryInfo(executableDirectory);
        for (var i = 0; i < 5 && directory is not null; i++)
        {
            yield return Path.Combine(directory.FullName, "Saved", "Logs");
            yield return Path.Combine(directory.FullName, "ShooterGame", "Saved", "Logs");
            directory = directory.Parent;
        }
    }

    private static IEnumerable<string> SafeEnumerateLogs(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*.log", SearchOption.TopDirectoryOnly);
        }
        catch
        {
            return [];
        }
    }

    private static string ReadTail(string filePath, int maxBytes)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var length = stream.Length;
            var bytesToRead = (int)Math.Min(length, maxBytes);
            stream.Seek(-bytesToRead, SeekOrigin.End);
            var buffer = new byte[bytesToRead];
            _ = stream.Read(buffer, 0, bytesToRead);
            return System.Text.Encoding.UTF8.GetString(buffer);
        }
        catch
        {
            return string.Empty;
        }
    }
}
