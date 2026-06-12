using System.Text.RegularExpressions;

namespace ObeliskLauncher;

public sealed record AsaConsoleTitleInfo(int Players, int MaxPlayers, int? ProcessId, string? SessionName, string? MapCode);

public static partial class AsaConsoleTitleParser
{
    public static bool TryParse(string title, out AsaConsoleTitleInfo info)
    {
        info = default!;

        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        var playerMatch = PlayersRegex().Match(title);
        if (!playerMatch.Success)
        {
            return false;
        }

        var players = int.Parse(playerMatch.Groups["players"].Value);
        var maxPlayers = int.Parse(playerMatch.Groups["max"].Value);
        var processMatch = ProcessRegex().Match(title);
        var processId = processMatch.Success ? int.Parse(processMatch.Groups["pid"].Value) : (int?)null;
        var parts = title.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var sessionName = parts.Length >= 2 ? parts[1] : null;
        var mapCode = parts.Length >= 3 ? parts[2] : null;
        info = new AsaConsoleTitleInfo(players, maxPlayers, processId, sessionName, mapCode);
        return true;
    }

    [GeneratedRegex(@"Players:\s*(?<players>\d+)\s*/\s*(?<max>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex PlayersRegex();

    [GeneratedRegex(@"Process\s+(?<pid>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ProcessRegex();
}
