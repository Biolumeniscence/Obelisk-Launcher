using System.Diagnostics;
using System.IO;
using System.Text;

namespace ObeliskLauncher;

public sealed record ParsedLaunchCommand(
    MapSettings Map,
    string? ClusterId,
    string CommonMods,
    string LaunchFlags);

public static class ArkServerLaunchCommand
{
    private static readonly string[] RemovedManagedFlags =
    [
        "-clusterid",
        "-mods"
    ];

    public static ProcessStartInfo CreateStartInfo(string executablePath, ClusterSettings cluster, MapSettings map)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            CreateNoWindow = false
        };

        foreach (var argument in BuildArguments(cluster, map))
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    public static string BuildDisplayCommand(string executablePath, ClusterSettings cluster, MapSettings map)
    {
        var parts = new List<string> { QuoteIfNeeded(Path.GetFileName(executablePath)) };
        parts.AddRange(BuildArguments(cluster, map).Select(QuoteIfNeeded));
        return string.Join(" ", parts);
    }

    public static string BuildArgumentString(ClusterSettings cluster, MapSettings map)
    {
        return string.Join(" ", BuildArguments(cluster, map).Select(QuoteIfNeeded));
    }

    public static IReadOnlyList<string> BuildArguments(ClusterSettings cluster, MapSettings map)
    {
        var arguments = new List<string>
        {
            BuildTravelArgument(map)
        };

        if (!string.IsNullOrWhiteSpace(cluster.CommonMods))
        {
            arguments.Add($"-mods={NormalizeMods(cluster.CommonMods)}");
        }

        foreach (var flag in SplitCommandLine(cluster.LaunchFlags))
        {
            if (IsManagedFlag(flag))
            {
                continue;
            }

            arguments.Add(flag);
        }

        arguments.Add($"-clusterid={cluster.ClusterId.Trim()}");
        return arguments;
    }

    public static string BuildTravelArgument(MapSettings map)
    {
        var builder = new StringBuilder();
        builder.Append(map.MapCode.Trim());
        builder.Append("?listen");
        builder.Append("?Port=").Append(map.Port);
        builder.Append("?QueryPort=").Append(map.QueryPort);
        builder.Append("?SessionName=").Append(map.SessionName.Trim());
        builder.Append("?AltSaveDirectoryName=").Append(map.AltSaveDirectoryName.Trim());
        builder.Append('?');
        return builder.ToString();
    }

    public static string NormalizeMods(string rawMods)
    {
        var mods = rawMods
            .Split([',', ';', ' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();

        return string.Join(",", mods);
    }

    public static bool TryParseLaunchLine(string line, out ParsedLaunchCommand? command, out string error)
    {
        command = null;
        error = string.Empty;

        var tokens = SplitCommandLine(line).ToList();
        if (tokens.Count == 0)
        {
            error = "Строка запуска пустая.";
            return false;
        }

        if (string.Equals(tokens[0], "start", StringComparison.OrdinalIgnoreCase))
        {
            tokens.RemoveAt(0);
        }

        var executableIndex = tokens.FindIndex(token => ServerExecutableLocator.IsSupportedServerExecutable(token));
        if (executableIndex < 0)
        {
            error = "В строке не найден ArkAscendedServer.exe.";
            return false;
        }

        var travelIndex = executableIndex + 1;
        if (travelIndex >= tokens.Count)
        {
            error = "После exe не найдена часть с картой и параметрами.";
            return false;
        }

        var map = ParseTravelArgument(tokens[travelIndex]);
        var commonMods = string.Empty;
        var clusterId = (string?)null;
        var flags = new List<string>();

        foreach (var token in tokens.Skip(travelIndex + 1))
        {
            if (token.StartsWith("-mods=", StringComparison.OrdinalIgnoreCase))
            {
                commonMods = NormalizeMods(token["-mods=".Length..]);
                continue;
            }

            if (token.StartsWith("-clusterid=", StringComparison.OrdinalIgnoreCase))
            {
                clusterId = token["-clusterid=".Length..].Trim();
                continue;
            }

            flags.Add(token);
        }

        command = new ParsedLaunchCommand(map, clusterId, commonMods, string.Join(" ", flags));
        return true;
    }

    public static IReadOnlyList<string> SplitCommandLine(string commandLine)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        foreach (var character in commandLine)
        {
            if (character == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(character) && !inQuotes)
            {
                AddCurrentToken();
                continue;
            }

            current.Append(character);
        }

        AddCurrentToken();
        return result;

        void AddCurrentToken()
        {
            if (current.Length == 0)
            {
                return;
            }

            result.Add(current.ToString());
            current.Clear();
        }
    }

    private static MapSettings ParseTravelArgument(string travelArgument)
    {
        var parts = travelArgument.Split('?', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var map = new MapSettings
        {
            MapCode = parts.Length > 0 ? parts[0] : "TheIsland_WP"
        };

        foreach (var part in parts.Skip(1))
        {
            var splitIndex = part.IndexOf('=');
            if (splitIndex <= 0)
            {
                continue;
            }

            var key = part[..splitIndex];
            var value = part[(splitIndex + 1)..];

            if (key.Equals("Port", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out var port))
            {
                map.Port = port;
            }
            else if (key.Equals("QueryPort", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out var queryPort))
            {
                map.QueryPort = queryPort;
            }
            else if (key.Equals("SessionName", StringComparison.OrdinalIgnoreCase))
            {
                map.SessionName = value;
                map.DisplayName = value;
            }
            else if (key.Equals("AltSaveDirectoryName", StringComparison.OrdinalIgnoreCase))
            {
                map.AltSaveDirectoryName = value;
            }
        }

        map.DisplayName = string.IsNullOrWhiteSpace(map.DisplayName) ? map.MapCode : map.DisplayName;
        return map;
    }

    private static bool IsManagedFlag(string flag)
    {
        return RemovedManagedFlags.Any(managedFlag =>
            flag.StartsWith($"{managedFlag}=", StringComparison.OrdinalIgnoreCase)
            || string.Equals(flag, managedFlag, StringComparison.OrdinalIgnoreCase));
    }

    private static string QuoteIfNeeded(string argument)
    {
        if (argument.Length == 0 || argument.Any(char.IsWhiteSpace))
        {
            return $"\"{argument.Replace("\"", "\\\"")}\"";
        }

        return argument;
    }
}
