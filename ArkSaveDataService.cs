using System.IO;

namespace ObeliskLauncher;

public sealed record DeleteSaveDataResult(string Path, bool Deleted, string Message);

public sealed class ArkSaveDataService
{
    public string ResolveSavedDirectory(string? serverExecutablePath)
    {
        if (!string.IsNullOrWhiteSpace(serverExecutablePath))
        {
            var executable = new FileInfo(serverExecutablePath);
            var shooterGame = FindShooterGameDirectory(executable.Directory);
            if (shooterGame is not null)
            {
                return Path.Combine(shooterGame.FullName, "Saved");
            }
        }

        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        return Path.Combine(
            programFilesX86,
            "Steam",
            "steamapps",
            "common",
            "ARK Survival Ascended Dedicated Server",
            "ShooterGame",
            "Saved");
    }

    public string GetMapSaveDirectory(string? serverExecutablePath, MapSettings map)
    {
        return Path.Combine(ResolveSavedDirectory(serverExecutablePath), map.AltSaveDirectoryName.Trim());
    }

    public string GetClusterDirectory(string? serverExecutablePath, ClusterSettings cluster)
    {
        return Path.Combine(ResolveSavedDirectory(serverExecutablePath), "clusters", cluster.ClusterId.Trim());
    }

    public DeleteSaveDataResult DeleteMapSaveData(string? serverExecutablePath, MapSettings map)
    {
        var path = GetMapSaveDirectory(serverExecutablePath, map);
        return DeleteDirectoryInsideSaved(serverExecutablePath, path);
    }

    public IReadOnlyList<DeleteSaveDataResult> DeleteClusterSaveData(string? serverExecutablePath, ClusterSettings cluster)
    {
        var results = new List<DeleteSaveDataResult>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var map in cluster.Maps)
        {
            var path = GetMapSaveDirectory(serverExecutablePath, map);
            if (seen.Add(Path.GetFullPath(path)))
            {
                results.Add(DeleteDirectoryInsideSaved(serverExecutablePath, path));
            }
        }

        var clusterPath = GetClusterDirectory(serverExecutablePath, cluster);
        if (seen.Add(Path.GetFullPath(clusterPath)))
        {
            results.Add(DeleteDirectoryInsideSaved(serverExecutablePath, clusterPath));
        }

        return results;
    }

    private DeleteSaveDataResult DeleteDirectoryInsideSaved(string? serverExecutablePath, string targetPath)
    {
        var savedDirectory = ResolveSavedDirectory(serverExecutablePath);
        var root = EnsureTrailingSeparator(Path.GetFullPath(savedDirectory));
        var fullTarget = Path.GetFullPath(targetPath);

        if (!fullTarget.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            || string.Equals(fullTarget.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            return new DeleteSaveDataResult(fullTarget, false, "Путь вне ShooterGame\\Saved, удаление заблокировано.");
        }

        if (!Directory.Exists(fullTarget))
        {
            return new DeleteSaveDataResult(fullTarget, false, "Папка не найдена.");
        }

        try
        {
            Directory.Delete(fullTarget, recursive: true);
            return new DeleteSaveDataResult(fullTarget, true, "Удалено.");
        }
        catch (Exception ex)
        {
            return new DeleteSaveDataResult(fullTarget, false, ex.Message);
        }
    }

    private static string EnsureTrailingSeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
    }

    private static DirectoryInfo? FindShooterGameDirectory(DirectoryInfo? directory)
    {
        while (directory is not null)
        {
            if (directory.Name.Equals("ShooterGame", StringComparison.OrdinalIgnoreCase))
            {
                return directory;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
