using System.IO;
using System.Text.Json;

namespace ObeliskLauncher;

public sealed class LauncherSettings
{
    public string SelectedGame { get; set; } = "ASA";

    public string? ServerExecutablePath { get; set; }

    public bool HasCluster { get; set; } = true;

    public ClusterSettings Cluster { get; set; } = ClusterSettings.CreateDefault();

    public GameServerSettings GameSettings { get; set; } = GameServerSettings.CreateDefault();
}

public sealed class ClusterSettings
{
    public string Name { get; set; } = "Cluster Alpha";

    public string ClusterId { get; set; } = "ClusterAlpha";

    public string CommonMods { get; set; } = string.Empty;

    public string LaunchFlags { get; set; } = "-NoBattlEye -nosteamclient -game -server -log";

    public List<MapSettings> Maps { get; set; } = CreateDefaultMaps();

    public static ClusterSettings CreateDefault()
    {
        return new ClusterSettings();
    }

    public static List<MapSettings> CreateDefaultMaps()
    {
        return
        [
            new()
            {
                DisplayName = "The Island",
                MapCode = "TheIsland_WP",
                SessionName = "IslandServer",
                AltSaveDirectoryName = "Save1",
                Port = 7777,
                QueryPort = 27015,
                RconPort = 27020,
                MaxPlayers = 70,
                AccentColor = "#2F6F63",
                InitialsForeground = "#F4FFFB"
            }
        ];
    }
}

public sealed class MapSettings
{
    public string DisplayName { get; set; } = "The Island";

    public string MapCode { get; set; } = "TheIsland_WP";

    public string SessionName { get; set; } = "IslandServer";

    public string AltSaveDirectoryName { get; set; } = "Save1";

    public int Port { get; set; } = 7777;

    public int QueryPort { get; set; } = 27015;

    public bool RconEnabled { get; set; }

    public int RconPort { get; set; } = 27020;

    public string AdminPassword { get; set; } = string.Empty;

    public int MaxPlayers { get; set; } = 70;

    public string AccentColor { get; set; } = "#2F6F63";

    public string InitialsForeground { get; set; } = "#F4FFFB";
}

public sealed class GameServerSettings
{
    public double HarvestAmountMultiplier { get; set; } = 1;

    public double XpMultiplier { get; set; } = 1;

    public double TamingSpeedMultiplier { get; set; } = 1;

    public double ItemStackSizeMultiplier { get; set; } = 1;

    public double StructurePreventResourceRadiusMultiplier { get; set; } = 1;

    public double AutoSavePeriodMinutes { get; set; } = 15;

    public double DayCycleSpeedScale { get; set; } = 1;

    public double NightTimeSpeedScale { get; set; } = 1;

    public double DifficultyOffset { get; set; } = 1;

    public bool ShowMapPlayerLocation { get; set; } = true;

    public bool AllowThirdPersonPlayer { get; set; } = true;

    public bool ServerCrosshair { get; set; } = true;

    public bool AlwaysAllowStructurePickup { get; set; } = true;

    public double StructurePickupTimeAfterPlacement { get; set; } = 30;

    public double MatingIntervalMultiplier { get; set; } = 1;

    public double EggHatchSpeedMultiplier { get; set; } = 1;

    public double BabyMatureSpeedMultiplier { get; set; } = 1;

    public double BabyCuddleIntervalMultiplier { get; set; } = 1;

    public double BabyImprintAmountMultiplier { get; set; } = 1;

    public double CropGrowthSpeedMultiplier { get; set; } = 1;

    public static GameServerSettings CreateDefault()
    {
        return new GameServerSettings();
    }
}

public sealed class LauncherSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public LauncherSettingsStore()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        SettingsDirectory = Path.Combine(appData, "Obelisk Launcher");
        SettingsFilePath = Path.Combine(SettingsDirectory, "settings.json");
    }

    public string SettingsDirectory { get; }

    public string SettingsFilePath { get; }

    public LauncherSettings Load()
    {
        if (!File.Exists(SettingsFilePath))
        {
            return new LauncherSettings();
        }

        try
        {
            var json = File.ReadAllText(SettingsFilePath);
            var settings = JsonSerializer.Deserialize<LauncherSettings>(json) ?? new LauncherSettings();
            settings.Cluster ??= ClusterSettings.CreateDefault();
            settings.Cluster.Name = UseFallback(settings.Cluster.Name, "Cluster Alpha");
            settings.Cluster.ClusterId = UseFallback(settings.Cluster.ClusterId, "ClusterAlpha");
            settings.Cluster.LaunchFlags = UseFallback(settings.Cluster.LaunchFlags, "-NoBattlEye -nosteamclient -game -server -log");
            settings.Cluster.CommonMods ??= string.Empty;
            settings.Cluster.Maps ??= ClusterSettings.CreateDefaultMaps();
            settings.GameSettings ??= GameServerSettings.CreateDefault();
            if (settings.Cluster.Maps.Count == 0)
            {
                settings.Cluster.Maps = ClusterSettings.CreateDefaultMaps();
            }

            foreach (var map in settings.Cluster.Maps)
            {
                NormalizeMap(map);
            }

            NormalizeGameSettings(settings.GameSettings);
            return settings;
        }
        catch
        {
            return new LauncherSettings();
        }
    }

    public void Save(LauncherSettings settings)
    {
        Directory.CreateDirectory(SettingsDirectory);
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(SettingsFilePath, json);
    }

    private static string UseFallback(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static void NormalizeMap(MapSettings map)
    {
        map.DisplayName = UseFallback(map.DisplayName, "The Island");
        map.MapCode = UseFallback(map.MapCode, "TheIsland_WP");
        map.SessionName = UseFallback(map.SessionName, "IslandServer");
        map.AltSaveDirectoryName = UseFallback(map.AltSaveDirectoryName, "Save1");
        map.AccentColor = UseFallback(map.AccentColor, "#2F6F63");
        map.InitialsForeground = UseFallback(map.InitialsForeground, "#F4FFFB");

        if (map.Port <= 0)
        {
            map.Port = 7777;
        }

        if (map.QueryPort <= 0)
        {
            map.QueryPort = 27015;
        }

        if (map.RconPort <= 0)
        {
            map.RconPort = 27020;
        }

        map.AdminPassword ??= string.Empty;

        if (map.MaxPlayers <= 0)
        {
            map.MaxPlayers = 70;
        }
    }

    private static void NormalizeGameSettings(GameServerSettings settings)
    {
        settings.HarvestAmountMultiplier = PositiveOrDefault(settings.HarvestAmountMultiplier, 1);
        settings.XpMultiplier = PositiveOrDefault(settings.XpMultiplier, 1);
        settings.TamingSpeedMultiplier = PositiveOrDefault(settings.TamingSpeedMultiplier, 1);
        settings.ItemStackSizeMultiplier = PositiveOrDefault(settings.ItemStackSizeMultiplier, 1);
        settings.StructurePreventResourceRadiusMultiplier = PositiveOrDefault(settings.StructurePreventResourceRadiusMultiplier, 1);
        settings.AutoSavePeriodMinutes = PositiveOrDefault(settings.AutoSavePeriodMinutes, 15);
        settings.DayCycleSpeedScale = PositiveOrDefault(settings.DayCycleSpeedScale, 1);
        settings.NightTimeSpeedScale = PositiveOrDefault(settings.NightTimeSpeedScale, 1);
        settings.DifficultyOffset = PositiveOrDefault(settings.DifficultyOffset, 1);
        settings.StructurePickupTimeAfterPlacement = NonNegativeOrDefault(settings.StructurePickupTimeAfterPlacement, 30);
        settings.MatingIntervalMultiplier = PositiveOrDefault(settings.MatingIntervalMultiplier, 1);
        settings.EggHatchSpeedMultiplier = PositiveOrDefault(settings.EggHatchSpeedMultiplier, 1);
        settings.BabyMatureSpeedMultiplier = PositiveOrDefault(settings.BabyMatureSpeedMultiplier, 1);
        settings.BabyCuddleIntervalMultiplier = PositiveOrDefault(settings.BabyCuddleIntervalMultiplier, 1);
        settings.BabyImprintAmountMultiplier = PositiveOrDefault(settings.BabyImprintAmountMultiplier, 1);
        settings.CropGrowthSpeedMultiplier = PositiveOrDefault(settings.CropGrowthSpeedMultiplier, 1);
    }

    private static double PositiveOrDefault(double value, double fallback)
    {
        return double.IsFinite(value) && value > 0 ? value : fallback;
    }

    private static double NonNegativeOrDefault(double value, double fallback)
    {
        return double.IsFinite(value) && value >= 0 ? value : fallback;
    }
}
