using System.Globalization;
using System.IO;

namespace ObeliskLauncher;

public sealed class GameServerConfigService
{
    private const string ServerSettingsSection = "ServerSettings";
    private const string ShooterGameModeSection = "/Script/ShooterGame.ShooterGameMode";

    public string ResolveConfigDirectory(string? serverExecutablePath)
    {
        if (!string.IsNullOrWhiteSpace(serverExecutablePath))
        {
            var executable = new FileInfo(serverExecutablePath);
            var shooterGame = FindShooterGameDirectory(executable.Directory);
            if (shooterGame is not null)
            {
                return Path.Combine(shooterGame.FullName, "Saved", "Config", "WindowsServer");
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
            "Saved",
            "Config",
            "WindowsServer");
    }

    public GameServerSettings Load(string configDirectory, GameServerSettings fallback)
    {
        var settings = Clone(fallback);
        var gameUserSettings = IniDocument.Load(Path.Combine(configDirectory, "GameUserSettings.ini"));
        var game = IniDocument.Load(Path.Combine(configDirectory, "Game.ini"));

        settings.HarvestAmountMultiplier = gameUserSettings.GetDouble(ServerSettingsSection, "HarvestAmountMultiplier", settings.HarvestAmountMultiplier);
        settings.XpMultiplier = gameUserSettings.GetDouble(ServerSettingsSection, "XPMultiplier", settings.XpMultiplier);
        settings.TamingSpeedMultiplier = gameUserSettings.GetDouble(ServerSettingsSection, "TamingSpeedMultiplier", settings.TamingSpeedMultiplier);
        settings.ItemStackSizeMultiplier = gameUserSettings.GetDouble(ServerSettingsSection, "ItemStackSizeMultiplier", settings.ItemStackSizeMultiplier);
        settings.StructurePreventResourceRadiusMultiplier = gameUserSettings.GetDouble(ServerSettingsSection, "StructurePreventResourceRadiusMultiplier", settings.StructurePreventResourceRadiusMultiplier);
        settings.AutoSavePeriodMinutes = gameUserSettings.GetDouble(ServerSettingsSection, "AutoSavePeriodMinutes", settings.AutoSavePeriodMinutes);
        settings.DayCycleSpeedScale = gameUserSettings.GetDouble(ServerSettingsSection, "DayCycleSpeedScale", settings.DayCycleSpeedScale);
        settings.NightTimeSpeedScale = gameUserSettings.GetDouble(ServerSettingsSection, "NightTimeSpeedScale", settings.NightTimeSpeedScale);
        settings.DifficultyOffset = gameUserSettings.GetDouble(ServerSettingsSection, "DifficultyOffset", settings.DifficultyOffset);
        settings.ShowMapPlayerLocation = gameUserSettings.GetBool(ServerSettingsSection, "ShowMapPlayerLocation", settings.ShowMapPlayerLocation);
        settings.AllowThirdPersonPlayer = gameUserSettings.GetBool(ServerSettingsSection, "AllowThirdPersonPlayer", settings.AllowThirdPersonPlayer);
        settings.ServerCrosshair = gameUserSettings.GetBool(ServerSettingsSection, "ServerCrosshair", settings.ServerCrosshair);
        settings.AlwaysAllowStructurePickup = gameUserSettings.GetBool(ServerSettingsSection, "AlwaysAllowStructurePickup", settings.AlwaysAllowStructurePickup);
        settings.StructurePickupTimeAfterPlacement = gameUserSettings.GetDouble(ServerSettingsSection, "StructurePickupTimeAfterPlacement", settings.StructurePickupTimeAfterPlacement);

        settings.MatingIntervalMultiplier = game.GetDouble(ShooterGameModeSection, "MatingIntervalMultiplier", settings.MatingIntervalMultiplier);
        settings.EggHatchSpeedMultiplier = game.GetDouble(ShooterGameModeSection, "EggHatchSpeedMultiplier", settings.EggHatchSpeedMultiplier);
        settings.BabyMatureSpeedMultiplier = game.GetDouble(ShooterGameModeSection, "BabyMatureSpeedMultiplier", settings.BabyMatureSpeedMultiplier);
        settings.BabyCuddleIntervalMultiplier = game.GetDouble(ShooterGameModeSection, "BabyCuddleIntervalMultiplier", settings.BabyCuddleIntervalMultiplier);
        settings.BabyImprintAmountMultiplier = game.GetDouble(ShooterGameModeSection, "BabyImprintAmountMultiplier", settings.BabyImprintAmountMultiplier);
        settings.CropGrowthSpeedMultiplier = game.GetDouble(ShooterGameModeSection, "CropGrowthSpeedMultiplier", settings.CropGrowthSpeedMultiplier);

        return settings;
    }

    public void Save(string configDirectory, GameServerSettings settings)
    {
        Directory.CreateDirectory(configDirectory);

        var gameUserSettingsPath = Path.Combine(configDirectory, "GameUserSettings.ini");
        var gamePath = Path.Combine(configDirectory, "Game.ini");
        var gameUserSettings = IniDocument.Load(gameUserSettingsPath);
        var game = IniDocument.Load(gamePath);

        gameUserSettings.Set(ServerSettingsSection, "HarvestAmountMultiplier", Format(settings.HarvestAmountMultiplier));
        gameUserSettings.Set(ServerSettingsSection, "XPMultiplier", Format(settings.XpMultiplier));
        gameUserSettings.Set(ServerSettingsSection, "TamingSpeedMultiplier", Format(settings.TamingSpeedMultiplier));
        gameUserSettings.Set(ServerSettingsSection, "ItemStackSizeMultiplier", Format(settings.ItemStackSizeMultiplier));
        gameUserSettings.Set(ServerSettingsSection, "StructurePreventResourceRadiusMultiplier", Format(settings.StructurePreventResourceRadiusMultiplier));
        gameUserSettings.Set(ServerSettingsSection, "AutoSavePeriodMinutes", Format(settings.AutoSavePeriodMinutes));
        gameUserSettings.Set(ServerSettingsSection, "DayCycleSpeedScale", Format(settings.DayCycleSpeedScale));
        gameUserSettings.Set(ServerSettingsSection, "NightTimeSpeedScale", Format(settings.NightTimeSpeedScale));
        gameUserSettings.Set(ServerSettingsSection, "DifficultyOffset", Format(settings.DifficultyOffset));
        gameUserSettings.Set(ServerSettingsSection, "ShowMapPlayerLocation", Format(settings.ShowMapPlayerLocation));
        gameUserSettings.Set(ServerSettingsSection, "AllowThirdPersonPlayer", Format(settings.AllowThirdPersonPlayer));
        gameUserSettings.Set(ServerSettingsSection, "ServerCrosshair", Format(settings.ServerCrosshair));
        gameUserSettings.Set(ServerSettingsSection, "AlwaysAllowStructurePickup", Format(settings.AlwaysAllowStructurePickup));
        gameUserSettings.Set(ServerSettingsSection, "StructurePickupTimeAfterPlacement", Format(settings.StructurePickupTimeAfterPlacement));

        game.Set(ShooterGameModeSection, "MatingIntervalMultiplier", Format(settings.MatingIntervalMultiplier));
        game.Set(ShooterGameModeSection, "EggHatchSpeedMultiplier", Format(settings.EggHatchSpeedMultiplier));
        game.Set(ShooterGameModeSection, "BabyMatureSpeedMultiplier", Format(settings.BabyMatureSpeedMultiplier));
        game.Set(ShooterGameModeSection, "BabyCuddleIntervalMultiplier", Format(settings.BabyCuddleIntervalMultiplier));
        game.Set(ShooterGameModeSection, "BabyImprintAmountMultiplier", Format(settings.BabyImprintAmountMultiplier));
        game.Set(ShooterGameModeSection, "CropGrowthSpeedMultiplier", Format(settings.CropGrowthSpeedMultiplier));

        gameUserSettings.Save(gameUserSettingsPath);
        game.Save(gamePath);
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

    private static GameServerSettings Clone(GameServerSettings source)
    {
        return new GameServerSettings
        {
            HarvestAmountMultiplier = source.HarvestAmountMultiplier,
            XpMultiplier = source.XpMultiplier,
            TamingSpeedMultiplier = source.TamingSpeedMultiplier,
            ItemStackSizeMultiplier = source.ItemStackSizeMultiplier,
            StructurePreventResourceRadiusMultiplier = source.StructurePreventResourceRadiusMultiplier,
            AutoSavePeriodMinutes = source.AutoSavePeriodMinutes,
            DayCycleSpeedScale = source.DayCycleSpeedScale,
            NightTimeSpeedScale = source.NightTimeSpeedScale,
            DifficultyOffset = source.DifficultyOffset,
            ShowMapPlayerLocation = source.ShowMapPlayerLocation,
            AllowThirdPersonPlayer = source.AllowThirdPersonPlayer,
            ServerCrosshair = source.ServerCrosshair,
            AlwaysAllowStructurePickup = source.AlwaysAllowStructurePickup,
            StructurePickupTimeAfterPlacement = source.StructurePickupTimeAfterPlacement,
            MatingIntervalMultiplier = source.MatingIntervalMultiplier,
            EggHatchSpeedMultiplier = source.EggHatchSpeedMultiplier,
            BabyMatureSpeedMultiplier = source.BabyMatureSpeedMultiplier,
            BabyCuddleIntervalMultiplier = source.BabyCuddleIntervalMultiplier,
            BabyImprintAmountMultiplier = source.BabyImprintAmountMultiplier,
            CropGrowthSpeedMultiplier = source.CropGrowthSpeedMultiplier
        };
    }

    private static string Format(double value)
    {
        return value.ToString("0.######", CultureInfo.InvariantCulture);
    }

    private static string Format(bool value)
    {
        return value ? "True" : "False";
    }
}

internal sealed class IniDocument
{
    private readonly List<string> _lines;

    private IniDocument(IEnumerable<string> lines)
    {
        _lines = lines.ToList();
    }

    public static IniDocument Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? new IniDocument(File.ReadAllLines(path))
                : new IniDocument([]);
        }
        catch
        {
            return new IniDocument([]);
        }
    }

    public double GetDouble(string section, string key, double fallback)
    {
        var value = Get(section, key);
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }

    public bool GetBool(string section, string key, bool fallback)
    {
        var value = Get(section, key);
        if (bool.TryParse(value, out var parsed))
        {
            return parsed;
        }

        return value switch
        {
            "1" => true,
            "0" => false,
            _ => fallback
        };
    }

    public void Set(string section, string key, string value)
    {
        if (TryFindKey(section, key, out var keyIndex))
        {
            _lines[keyIndex] = $"{key}={value}";
            return;
        }

        var sectionIndex = FindSectionIndex(section);
        if (sectionIndex < 0)
        {
            if (_lines.Count > 0 && !string.IsNullOrWhiteSpace(_lines[^1]))
            {
                _lines.Add(string.Empty);
            }

            _lines.Add($"[{section}]");
            _lines.Add($"{key}={value}");
            return;
        }

        var insertIndex = sectionIndex + 1;
        while (insertIndex < _lines.Count && !IsSectionHeader(_lines[insertIndex]))
        {
            insertIndex++;
        }

        _lines.Insert(insertIndex, $"{key}={value}");
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Environment.CurrentDirectory);
        File.WriteAllLines(path, _lines);
    }

    private string? Get(string section, string key)
    {
        return TryFindKey(section, key, out var index)
            ? GetValue(_lines[index])
            : null;
    }

    private bool TryFindKey(string section, string key, out int keyIndex)
    {
        keyIndex = -1;
        var sectionIndex = FindSectionIndex(section);
        if (sectionIndex < 0)
        {
            return false;
        }

        for (var i = sectionIndex + 1; i < _lines.Count; i++)
        {
            var line = _lines[i];
            if (IsSectionHeader(line))
            {
                break;
            }

            if (TryReadKey(line, out var currentKey)
                && currentKey.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                keyIndex = i;
                return true;
            }
        }

        return false;
    }

    private int FindSectionIndex(string section)
    {
        for (var i = 0; i < _lines.Count; i++)
        {
            var line = _lines[i].Trim();
            if (line.Length < 3 || line[0] != '[' || line[^1] != ']')
            {
                continue;
            }

            var name = line[1..^1].Trim();
            if (name.Equals(section, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool TryReadKey(string line, out string key)
    {
        key = string.Empty;
        var trimmed = line.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] is ';' or '#')
        {
            return false;
        }

        var separator = trimmed.IndexOf('=');
        if (separator <= 0)
        {
            return false;
        }

        key = trimmed[..separator].Trim();
        return key.Length > 0;
    }

    private static string? GetValue(string line)
    {
        var separator = line.IndexOf('=');
        return separator < 0 ? null : line[(separator + 1)..].Trim();
    }

    private static bool IsSectionHeader(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length >= 3 && trimmed[0] == '[' && trimmed[^1] == ']';
    }
}
