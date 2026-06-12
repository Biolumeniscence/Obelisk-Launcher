using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ObeliskLauncher;

public sealed record ServerExecutableCandidate(string FullPath, string Source);

public sealed class ServerExecutableLocator
{
    private static readonly string[] SupportedExecutableNames =
    [
        "ArkAscendedServer.exe",
        "ArkAscendedDedicatedServer.exe"
    ];

    private static readonly string[] LikelyServerFolders =
    [
        "ARK Survival Ascended Dedicated Server",
        "ARK Survival Ascended"
    ];

    public ServerExecutableCandidate? TryFind()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var commonRoot in EnumerateSteamCommonRoots())
        {
            if (!Directory.Exists(commonRoot))
            {
                continue;
            }

            foreach (var candidate in EnumerateCandidatesInCommonRoot(commonRoot))
            {
                if (!seen.Add(candidate) || !File.Exists(candidate))
                {
                    continue;
                }

                return new ServerExecutableCandidate(candidate, commonRoot);
            }
        }

        return null;
    }

    public static bool IsSupportedServerExecutable(string path)
    {
        var fileName = Path.GetFileName(path);
        return SupportedExecutableNames.Any(name => string.Equals(fileName, name, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> EnumerateCandidatesInCommonRoot(string commonRoot)
    {
        foreach (var folder in LikelyServerFolders)
        {
            foreach (var executableName in SupportedExecutableNames)
            {
                yield return Path.Combine(commonRoot, folder, "ShooterGame", "Binaries", "Win64", executableName);
                yield return Path.Combine(commonRoot, folder, executableName);
            }
        }

        foreach (var arkFolder in SafeEnumerateDirectories(commonRoot)
                     .Where(path => Path.GetFileName(path).Contains("ARK", StringComparison.OrdinalIgnoreCase)
                                    || Path.GetFileName(path).Contains("Ascended", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var executableName in SupportedExecutableNames)
            {
                foreach (var match in SafeEnumerateFiles(arkFolder, executableName))
                {
                    yield return match;
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateSteamCommonRoots()
    {
        var steamRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var steamPath in EnumerateSteamInstallPaths())
        {
            if (string.IsNullOrWhiteSpace(steamPath))
            {
                continue;
            }

            var normalizedSteamPath = NormalizePath(steamPath);
            steamRoots.Add(Path.Combine(normalizedSteamPath, "steamapps", "common"));

            var libraryFolders = Path.Combine(normalizedSteamPath, "steamapps", "libraryfolders.vdf");
            foreach (var libraryPath in ReadLibraryFolders(libraryFolders))
            {
                steamRoots.Add(Path.Combine(libraryPath, "steamapps", "common"));
            }
        }

        foreach (var root in steamRoots)
        {
            yield return root;
        }
    }

    private static IEnumerable<string> EnumerateSteamInstallPaths()
    {
        var registryPaths = new[]
        {
            @"HKEY_CURRENT_USER\Software\Valve\Steam",
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam",
            @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam"
        };

        foreach (var registryPath in registryPaths)
        {
            foreach (var valueName in new[] { "SteamPath", "InstallPath" })
            {
                if (TryGetRegistryString(registryPath, valueName) is string path)
                {
                    yield return path;
                }
            }
        }

        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFilesX86))
        {
            yield return Path.Combine(programFilesX86, "Steam");
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            yield return Path.Combine(programFiles, "Steam");
        }
    }

    private static string? TryGetRegistryString(string registryPath, string valueName)
    {
        try
        {
            return Registry.GetValue(registryPath, valueName, null) as string;
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> ReadLibraryFolders(string libraryFoldersPath)
    {
        if (!File.Exists(libraryFoldersPath))
        {
            yield break;
        }

        string text;
        try
        {
            text = File.ReadAllText(libraryFoldersPath);
        }
        catch
        {
            yield break;
        }

        foreach (Match match in Regex.Matches(text, "\"path\"\\s+\"(?<path>[^\"]+)\""))
        {
            var path = match.Groups["path"].Value.Replace(@"\\", @"\");
            if (!string.IsNullOrWhiteSpace(path))
            {
                yield return NormalizePath(path);
            }
        }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path);
        }
        catch
        {
            return [];
        }
    }

    private static IEnumerable<string> SafeEnumerateFiles(string path, string searchPattern)
    {
        try
        {
            return Directory.EnumerateFiles(path, searchPattern, SearchOption.AllDirectories);
        }
        catch
        {
            return [];
        }
    }

    private static string NormalizePath(string path)
    {
        return path.Replace('/', '\\').Trim();
    }
}
