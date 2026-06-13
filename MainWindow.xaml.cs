using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ObeliskLauncher;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private static readonly MapPreset[] MapPresets =
    [
        new("The Island", "TheIsland_WP", "#2F6F63"),
        new("Ragnarok", "Ragnarok_WP", "#5B3842"),
        new("Scorched Earth", "ScorchedEarth_WP", "#5D4A2E"),
        new("The Center", "TheCenter_WP", "#4B3B59"),
        new("Aberration", "Aberration_WP", "#29313B")
    ];

    private readonly Dictionary<ServerMapInstance, DispatcherTimer> _runtimeTimers = new();
    private readonly LauncherSettingsStore _settingsStore = new();
    private readonly AsaServerConsoleProbe _serverConsoleProbe = new();
    private readonly ArkSaveDataService _saveDataService = new();
    private readonly GameServerConfigService _gameConfigService = new();
    private readonly ServerExecutableLocator _serverExecutableLocator = new();
    private readonly SourceRconClient _rconClient = new();
    private readonly WindowsTitleProbe _titleProbe = new();
    private LauncherSettings _settings;
    private ServerCluster? _editingCluster;
    private ServerMapInstance? _editingMap;
    private ServerMapInstance? _rconInstance;
    private bool _isAddingMap;
    private bool _startupPromptHandled;
    private bool _suppressPresetChange;

    public MainWindow()
    {
        _settings = _settingsStore.Load();
        Clusters = CreateClusters(_settings);

        InitializeComponent();
        DataContext = this;
        Loaded += MainWindow_Loaded;
        RefreshOverview();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<ServerCluster> Clusters { get; }

    public string ClusterCountText => Clusters.Count.ToString();

    public bool CanCreateCluster => Clusters.Count == 0;

    public bool HasCluster => Clusters.Count > 0;

    public string OnlineMapsText
    {
        get
        {
            var total = Clusters.Sum(cluster => cluster.Instances.Count);
            var running = Clusters.Sum(cluster => cluster.Instances.Count(instance => instance.IsProcessActive));
            return $"{running} / {total}";
        }
    }

    public string HeaderStatusText
    {
        get
        {
            var instances = Clusters.SelectMany(cluster => cluster.Instances).ToList();
            if (instances.Any(instance => instance.Status == ServerRuntimeStatus.Crashed))
            {
                return "Ошибка";
            }

            if (instances.Any(instance => instance.Status == ServerRuntimeStatus.Starting))
            {
                return "Запуск";
            }

            if (instances.Any(instance => instance.Status is ServerRuntimeStatus.ReadyNoQuery or ServerRuntimeStatus.Online))
            {
                return "Работает";
            }

            return "Ожидание";
        }
    }

    public Brush HeaderStatusBrush
    {
        get
        {
            return HeaderStatusText switch
            {
                "Ошибка" => BrushFrom("#E25763"),
                "Запуск" => BrushFrom("#F2C94C"),
                "Работает" => BrushFrom("#6EE7B7"),
                _ => BrushFrom("#A7AEBC")
            };
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        var activeInstances = Clusters
            .SelectMany(cluster => cluster.Instances)
            .Where(instance => instance.IsProcessActive)
            .ToList();

        if (activeInstances.Count == 0)
        {
            base.OnClosing(e);
            return;
        }

        var result = MessageBox.Show(
            this,
            $"Сейчас запущено карт: {activeInstances.Count}. Остановить их перед закрытием Obelisk Launcher?\n\nДа - остановить серверы.\nНет - закрыть лаунчер и оставить серверы работать.\nОтмена - вернуться в приложение.",
            "Запущенные серверы",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Cancel)
        {
            e.Cancel = true;
            return;
        }

        if (result == MessageBoxResult.Yes)
        {
            foreach (var instance in activeInstances)
            {
                StopInstanceSynchronously(instance);
            }
        }

        base.OnClosing(e);
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_startupPromptHandled)
        {
            return;
        }

        _startupPromptHandled = true;
        InitializeServerExecutablePath();
        AttachAlreadyRunningServers();
    }

    private void InitializeServerExecutablePath()
    {
        if (!string.IsNullOrWhiteSpace(_settings.ServerExecutablePath) && File.Exists(_settings.ServerExecutablePath))
        {
            ApplyServerExecutablePath(_settings.ServerExecutablePath, "Используем сохраненный путь до dedicated server.");
            return;
        }

        var found = _serverExecutableLocator.TryFind();
        if (found is not null)
        {
            _settings.ServerExecutablePath = found.FullPath;
            ApplyServerExecutablePath(found.FullPath, $"ArkAscendedServer.exe найден автоматически: {found.Source}");
            SaveSettingsQuietly();
            return;
        }

        MarkServerExecutableMissing("ArkAscendedServer.exe не найден автоматически.");

        var result = MessageBox.Show(
            this,
            "Не удалось автоматически найти ArkAscendedServer.exe. Выбрать файл вручную?",
            "Obelisk Launcher",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            ChooseServerExecutableManually();
        }
    }

    private void SelectAscended_Click(object sender, RoutedEventArgs e)
    {
        SelectedGameName.Text = "ARK Survival Ascended";
        CurrentGameButton.Content = "ARK: Ascended";
        GamePickerOverlay.Visibility = Visibility.Collapsed;
        DemoStatusText.Text = "Выбран ASA. Серверы запускаются отдельными процессами карт.";
    }

    private void ShowGamePicker_Click(object sender, RoutedEventArgs e)
    {
        GamePickerOverlay.Visibility = Visibility.Visible;
    }

    private void ChooseExe_Click(object sender, RoutedEventArgs e)
    {
        ChooseServerExecutableManually();
    }

    private void ChooseServerExecutableManually()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Выберите ArkAscendedServer.exe",
            Filter = "ARK Ascended server|ArkAscendedServer.exe;ArkAscendedDedicatedServer.exe|Executable files|*.exe|All files|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        var initialDirectory = GetInitialExecutableDirectory();
        if (!string.IsNullOrWhiteSpace(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        if (dialog.ShowDialog(this) != true)
        {
            DemoStatusText.Text = "Выбор dedicated server отменен.";
            return;
        }

        if (!ServerExecutableLocator.IsSupportedServerExecutable(dialog.FileName))
        {
            MessageBox.Show(
                this,
                "Пока ожидается ArkAscendedServer.exe. Если у dedicated server другое имя, уточним и добавим его в список.",
                "Неверный exe",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        _settings.ServerExecutablePath = dialog.FileName;
        ApplyServerExecutablePath(dialog.FileName, "Путь до dedicated server выбран вручную.");
        SaveSettings();
    }

    private string? GetInitialExecutableDirectory()
    {
        if (!string.IsNullOrWhiteSpace(_settings.ServerExecutablePath))
        {
            var directory = Path.GetDirectoryName(_settings.ServerExecutablePath);
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            {
                return directory;
            }
        }

        return null;
    }

    private void ApplyServerExecutablePath(string path, string statusMessage)
    {
        ExeButton.Content = "EXE УКАЗАН";
        ExeButton.Background = BrushFrom("#173E33");
        ExeButton.BorderBrush = BrushFrom("#3AAE72");
        ExeButton.Foreground = BrushFrom("#E8FFF4");
        ExeStatusDot.Fill = BrushFrom("#6EE7B7");
        ExeStatusText.Text = $"{Path.GetFileName(path)} настроен";
        ExeHintText.Text = path;
        ExeHintText.Foreground = BrushFrom("#A9DBC8");
        DemoStatusText.Text = statusMessage;
    }

    private void MarkServerExecutableMissing(string statusMessage)
    {
        ExeButton.Content = "EXE НЕ УКАЗАН";
        ExeButton.Background = BrushFrom("#48171B");
        ExeButton.BorderBrush = BrushFrom("#E25763");
        ExeButton.Foreground = BrushFrom("#FFE5E7");
        ExeStatusDot.Fill = BrushFrom("#E25763");
        ExeStatusText.Text = "Путь к серверу не настроен";
        ExeHintText.Text = "Не указан. Нажми красную кнопку сверху и выбери ArkAscendedServer.exe.";
        ExeHintText.Foreground = BrushFrom("#CE9CA1");
        DemoStatusText.Text = statusMessage;
    }

    private async void ToggleInstance_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetInstance(sender, out var instance))
        {
            return;
        }

        if (!instance.IsProcessActive)
        {
            StartServerInstance(instance);
            return;
        }

        await StopServerInstanceAsync(instance);
    }

    private async void RestartInstance_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetInstance(sender, out var instance))
        {
            return;
        }

        await StopServerInstanceAsync(instance);
        StartServerInstance(instance);
    }

    private void ShowCommand_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetInstance(sender, out var instance))
        {
            return;
        }

        var cluster = FindCluster(instance);
        if (cluster is null)
        {
            return;
        }

        var executable = _settings.ServerExecutablePath ?? "ArkAscendedServer.exe";
        var command = instance.LastLaunchCommand;
        if (string.IsNullOrWhiteSpace(command))
        {
            command = ArkServerLaunchCommand.BuildDisplayCommand(executable, cluster.ToSettings(), instance.ToSettings());
        }

        MessageBox.Show(this, command, "Команда запуска", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OpenRcon_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetInstance(sender, out var instance))
        {
            return;
        }

        if (!EnsureRconReady(instance))
        {
            return;
        }

        _rconInstance = instance;
        RconMapTitleTextBlock.Text = $"RCON: {instance.DisplayName}";
        RconEndpointTextBlock.Text = $"127.0.0.1:{instance.RconPort}";
        RconCommandTextBox.Text = string.Empty;
        RconOutputTextBox.Text = "RCON готов. Можно отправить команду.";
        RconCommandOverlay.Visibility = Visibility.Visible;
        RconCommandTextBox.Focus();
    }

    private async void SendRconCommand_Click(object sender, RoutedEventArgs e)
    {
        await SendRconCommandFromInputAsync();
    }

    private async void RconSaveWorld_Click(object sender, RoutedEventArgs e)
    {
        await SendRconCommandAsync("SaveWorld");
    }

    private async void RconListPlayers_Click(object sender, RoutedEventArgs e)
    {
        await SendRconCommandAsync("ListPlayers");
    }

    private void RconBroadcast_Click(object sender, RoutedEventArgs e)
    {
        if (!RconCommandTextBox.Text.StartsWith("Broadcast ", StringComparison.OrdinalIgnoreCase))
        {
            RconCommandTextBox.Text = "Broadcast ";
            RconCommandTextBox.CaretIndex = RconCommandTextBox.Text.Length;
        }

        RconCommandTextBox.Focus();
    }

    private void CancelRcon_Click(object sender, RoutedEventArgs e)
    {
        _rconInstance = null;
        RconCommandOverlay.Visibility = Visibility.Collapsed;
    }

    private async Task SendRconCommandFromInputAsync()
    {
        var command = RconCommandTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(command))
        {
            ShowValidationError("Введи RCON команду.");
            return;
        }

        await SendRconCommandAsync(command);
    }

    private async Task SendRconCommandAsync(string command)
    {
        if (_rconInstance is null)
        {
            RconCommandOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        if (!EnsureRconReady(_rconInstance))
        {
            return;
        }

        RconOutputTextBox.Text = $"Отправляю: {command}";

        try
        {
            var response = await _rconClient.SendCommandAsync(
                "127.0.0.1",
                _rconInstance.RconPort,
                _rconInstance.AdminPassword,
                command);

            RconOutputTextBox.Text = response;
            DemoStatusText.Text = $"{_rconInstance.DisplayName}: RCON команда отправлена.";
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException or IOException or InvalidOperationException or ArgumentException)
        {
            RconOutputTextBox.Text = $"RCON не ответил: {ex.Message}";
            DemoStatusText.Text = $"{_rconInstance.DisplayName}: RCON не ответил.";
        }
    }

    private bool EnsureRconReady(ServerMapInstance instance)
    {
        if (!instance.IsProcessActive)
        {
            ShowValidationError("RCON доступен только когда карта запущена.");
            return false;
        }

        if (!instance.RconEnabled)
        {
            ShowValidationError("RCON выключен в настройках этой карты. Открой настройки карты, включи RCON, задай порт и ServerAdminPassword, затем перезапусти карту.");
            return false;
        }

        if (instance.RconPort is < 1 or > 65535)
        {
            ShowValidationError("RCONPort должен быть числом от 1 до 65535.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(instance.AdminPassword))
        {
            ShowValidationError("Для RCON нужен ServerAdminPassword в настройках карты.");
            return false;
        }

        return true;
    }

    private async void StartCluster_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetCluster(sender, out var cluster))
        {
            return;
        }

        foreach (var instance in cluster.Instances.Where(instance => !instance.IsProcessActive))
        {
            StartServerInstance(instance);
            await Task.Delay(250);
        }
    }

    private async void StopCluster_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetCluster(sender, out var cluster))
        {
            return;
        }

        foreach (var instance in cluster.Instances.Where(instance => instance.IsProcessActive).ToList())
        {
            await StopServerInstanceAsync(instance);
        }
    }

    private async void RestartCluster_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetCluster(sender, out var cluster))
        {
            return;
        }

        foreach (var instance in cluster.Instances.Where(instance => instance.IsProcessActive).ToList())
        {
            await StopServerInstanceAsync(instance);
        }

        await StartStoppedInstancesAsync(cluster.Instances);
    }

    private async void DeleteCluster_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetCluster(sender, out var cluster))
        {
            return;
        }

        var activeCount = cluster.Instances.Count(instance => instance.IsProcessActive);
        var clusterSettings = cluster.ToSettings();
        var clusterDataPath = _saveDataService.GetClusterDirectory(_settings.ServerExecutablePath, clusterSettings);
        var message = activeCount > 0
            ? $"В кластере сейчас запущено карт: {activeCount}.\n\nДа - остановить серверы, удалить кластер из лаунчера, сохранения карт и данные кластера.\nНет - остановить серверы и удалить только запись из лаунчера.\nОтмена - ничего не делать.\n\nДанные кластера:\n{clusterDataPath}"
            : $"Да - удалить кластер из лаунчера, сохранения карт и данные кластера.\nНет - удалить только запись из лаунчера.\nОтмена - ничего не делать.\n\nДанные кластера:\n{clusterDataPath}";

        var result = MessageBox.Show(
            this,
            message,
            "Удалить кластер",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Cancel)
        {
            return;
        }

        var deleteSaveData = result == MessageBoxResult.Yes;

        foreach (var instance in cluster.Instances.Where(instance => instance.IsProcessActive).ToList())
        {
            await StopServerInstanceAsync(instance);
        }

        var deleteResults = deleteSaveData
            ? _saveDataService.DeleteClusterSaveData(_settings.ServerExecutablePath, clusterSettings)
            : [];

        Clusters.Remove(cluster);
        _editingCluster = null;
        _editingMap = null;
        _settings.HasCluster = false;
        _settings.Cluster = ClusterSettings.CreateDefault();
        SaveSettings();
        RefreshOverview();
        ShowSaveDataDeleteWarnings(deleteResults);
        DemoStatusText.Text = deleteSaveData
            ? $"Кластер удален из лаунчера. {BuildSaveDataDeleteSummary(deleteResults)}"
            : "Кластер удален из лаунчера. Файлы сохранений оставлены на диске.";
    }

    private void ToggleClusterId_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetCluster(sender, out var cluster))
        {
            return;
        }

        cluster.ToggleClusterIdVisibility();
        DemoStatusText.Text = cluster.IsClusterIdVisible
            ? $"{cluster.Title}: Cluster ID временно показан."
            : $"{cluster.Title}: Cluster ID скрыт.";
    }

    private async Task RestartInstancesAsync(IReadOnlyCollection<ServerMapInstance> instances)
    {
        foreach (var instance in instances.Where(instance => instance.IsProcessActive).ToList())
        {
            await StopServerInstanceAsync(instance);
        }

        await StartStoppedInstancesAsync(instances);
    }

    private async Task StartStoppedInstancesAsync(IEnumerable<ServerMapInstance> instances)
    {
        await Task.Delay(500);

        foreach (var instance in instances.Where(instance => !instance.IsProcessActive))
        {
            StartServerInstance(instance);
            await Task.Delay(250);
        }
    }

    private void EditFirstCluster_Click(object sender, RoutedEventArgs e)
    {
        if (Clusters.FirstOrDefault() is { } cluster)
        {
            OpenClusterEditor(cluster);
        }
    }

    private void CreateCluster_Click(object sender, RoutedEventArgs e)
    {
        if (Clusters.Count >= 1)
        {
            DemoStatusText.Text = "Пока поддерживается только один кластер.";
            return;
        }

        var settings = ClusterSettings.CreateDefault();
        var cluster = CreateCluster(settings);
        Clusters.Add(cluster);
        _settings.HasCluster = true;
        _settings.Cluster = settings;
        SaveSettings();
        RefreshOverview();
        DemoStatusText.Text = $"{cluster.Title}: кластер создан.";
    }

    private void OpenGameSettings_Click(object sender, RoutedEventArgs e)
    {
        OpenGameSettingsEditor();
    }

    private void OpenGameSettingsEditor()
    {
        try
        {
            var configDirectory = _gameConfigService.ResolveConfigDirectory(_settings.ServerExecutablePath);
            _settings.GameSettings = _gameConfigService.Load(configDirectory, _settings.GameSettings);
            FillGameSettingsFields(_settings.GameSettings);
            GameConfigPathTextBlock.Text = configDirectory;
            GameSettingsOverlay.Visibility = Visibility.Visible;
            HarvestAmountTextBox.Focus();
            HarvestAmountTextBox.SelectAll();
        }
        catch (Exception ex)
        {
            ShowValidationError($"Не удалось открыть настройки игры: {ex.Message}");
        }
    }

    private void CancelGameSettings_Click(object sender, RoutedEventArgs e)
    {
        GameSettingsOverlay.Visibility = Visibility.Collapsed;
        DemoStatusText.Text = "Изменение настроек игры отменено.";
    }

    private async void SaveGameSettings_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadGameSettings(out var settings))
        {
            return;
        }

        var configDirectory = _gameConfigService.ResolveConfigDirectory(_settings.ServerExecutablePath);
        try
        {
            _gameConfigService.Save(configDirectory, settings);
            _settings.GameSettings = settings;
            SaveSettings();
            GameSettingsOverlay.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            ShowValidationError($"Не удалось сохранить настройки игры в {configDirectory}: {ex.Message}");
            return;
        }

        var activeInstances = Clusters
            .SelectMany(cluster => cluster.Instances)
            .Where(instance => instance.IsProcessActive)
            .ToList();

        if (activeInstances.Count == 0)
        {
            DemoStatusText.Text = "Настройки игры сохранены. Они применятся при следующем запуске карты.";
            return;
        }

        var result = MessageBox.Show(
            this,
            $"Настройки игры сохранены. Сейчас запущено карт: {activeInstances.Count}.\n\nПерезапустить запущенные серверы сейчас, чтобы применить изменения?",
            "Применить настройки",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            await RestartInstancesAsync(activeInstances);
            DemoStatusText.Text = "Настройки игры сохранены, запущенные серверы перезапущены.";
        }
        else
        {
            DemoStatusText.Text = "Настройки игры сохранены. Запущенные серверы применят их после следующего запуска.";
        }
    }

    private void EditCluster_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetCluster(sender, out var cluster))
        {
            OpenClusterEditor(cluster);
        }
    }

    private void OpenClusterEditor(ServerCluster cluster)
    {
        _editingCluster = cluster;
        ClusterNameTextBox.Text = cluster.Name;
        ClusterIdTextBox.Text = cluster.ClusterId;
        ClusterModsTextBox.Text = cluster.CommonMods;
        ClusterLaunchFlagsTextBox.Text = cluster.LaunchFlags;
        ClusterSettingsOverlay.Visibility = Visibility.Visible;
        ClusterNameTextBox.Focus();
        ClusterNameTextBox.SelectAll();
    }

    private void CancelClusterSettings_Click(object sender, RoutedEventArgs e)
    {
        _editingCluster = null;
        ClusterSettingsOverlay.Visibility = Visibility.Collapsed;
        DemoStatusText.Text = "Изменение настроек кластера отменено.";
    }

    private void SaveClusterSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_editingCluster is null)
        {
            ClusterSettingsOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        var name = ClusterNameTextBox.Text.Trim();
        var clusterId = ClusterIdTextBox.Text.Trim();
        var mods = NormalizeModsOrShowError(ClusterModsTextBox.Text);
        var launchFlags = ClusterLaunchFlagsTextBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(name))
        {
            ShowValidationError("Название профиля кластера не должно быть пустым.");
            return;
        }

        if (!ValidateSimpleToken(clusterId, "Cluster ID"))
        {
            return;
        }

        if (mods is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(launchFlags))
        {
            launchFlags = "-NoBattlEye -nosteamclient -game -server -log";
        }

        _editingCluster.UpdateSettings(name, clusterId, mods, launchFlags);
        PersistClusterSettings(_editingCluster);

        DemoStatusText.Text = $"{_editingCluster.Title}: настройки сохранены.";
        _editingCluster = null;
        ClusterSettingsOverlay.Visibility = Visibility.Collapsed;
    }

    private void AddMap_Click(object sender, RoutedEventArgs e)
    {
        var cluster = Clusters.FirstOrDefault();
        if (cluster is null)
        {
            ShowValidationError("Сначала создай кластер. Пока приложение поддерживает один кластер.");
            return;
        }

        _editingCluster = cluster;
        _editingMap = new ServerMapInstance(CreateNewMapSettings(cluster));
        _isAddingMap = true;
        OpenMapEditor(_editingMap);
    }

    private void EditMap_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetInstance(sender, out var instance))
        {
            return;
        }

        if (instance.IsProcessActive)
        {
            ShowValidationError("Останови карту перед изменением настроек запуска.");
            return;
        }

        _editingCluster = FindCluster(instance);
        _editingMap = instance;
        _isAddingMap = false;
        OpenMapEditor(instance);
    }

    private async void DeleteMap_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetInstance(sender, out var instance))
        {
            return;
        }

        var cluster = FindCluster(instance);
        if (cluster is null)
        {
            return;
        }

        var mapSettings = instance.ToSettings();
        var savePath = _saveDataService.GetMapSaveDirectory(_settings.ServerExecutablePath, mapSettings);
        var message = instance.IsProcessActive
            ? $"{instance.DisplayName} сейчас запущена.\n\nДа - остановить сервер, удалить карту из лаунчера и папку сохранения.\nНет - остановить сервер и удалить только запись из лаунчера.\nОтмена - ничего не делать.\n\nПапка сохранения:\n{savePath}"
            : $"Удалить карту {instance.DisplayName}?\n\nДа - удалить карту из лаунчера и папку сохранения.\nНет - удалить только запись из лаунчера.\nОтмена - ничего не делать.\n\nПапка сохранения:\n{savePath}";

        var result = MessageBox.Show(
            this,
            message,
            "Удалить карту",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Cancel)
        {
            return;
        }

        var deleteSaveData = result == MessageBoxResult.Yes;

        if (instance.IsProcessActive)
        {
            await StopServerInstanceAsync(instance);
        }

        var deleteResults = deleteSaveData
            ? new[] { _saveDataService.DeleteMapSaveData(_settings.ServerExecutablePath, mapSettings) }
            : [];

        cluster.Instances.Remove(instance);
        PersistClusterSettings(cluster);
        cluster.RefreshSummary();
        RefreshOverview();
        ShowSaveDataDeleteWarnings(deleteResults);
        DemoStatusText.Text = deleteSaveData
            ? $"{instance.DisplayName}: карта удалена из лаунчера. {BuildSaveDataDeleteSummary(deleteResults)}"
            : $"{instance.DisplayName}: карта удалена из лаунчера. Файлы сохранения оставлены на диске.";
    }

    private void ShowSaveDataDeleteWarnings(IEnumerable<DeleteSaveDataResult> results)
    {
        var failed = results
            .Where(result => !result.Deleted && !result.Message.Equals("Папка не найдена.", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (failed.Count == 0)
        {
            return;
        }

        var details = string.Join(
            Environment.NewLine + Environment.NewLine,
            failed.Select(result => $"{result.Path}{Environment.NewLine}{result.Message}"));

        MessageBox.Show(
            this,
            $"Не все файлы сохранений удалось удалить:{Environment.NewLine}{Environment.NewLine}{details}",
            "Удаление файлов сохранений",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private static string BuildSaveDataDeleteSummary(IEnumerable<DeleteSaveDataResult> results)
    {
        var list = results.ToList();
        if (list.Count == 0)
        {
            return "Файлы сохранений не удалялись.";
        }

        var deleted = list.Count(result => result.Deleted);
        var missing = list.Count(result => !result.Deleted && result.Message.Equals("Папка не найдена.", StringComparison.OrdinalIgnoreCase));
        var failed = list.Count - deleted - missing;
        var parts = new List<string>
        {
            $"папок удалено: {deleted}"
        };

        if (missing > 0)
        {
            parts.Add($"не найдено: {missing}");
        }

        if (failed > 0)
        {
            parts.Add($"ошибок: {failed}");
        }

        return string.Join(", ", parts) + ".";
    }

    private void OpenMapEditor(ServerMapInstance instance)
    {
        _suppressPresetChange = true;
        SelectPresetForMapCode(instance.MapCode);
        _suppressPresetChange = false;
        MapDisplayNameTextBox.Text = instance.DisplayName;
        MapCodeTextBox.Text = instance.MapCode;
        MapSessionNameTextBox.Text = instance.SessionName;
        MapSaveDirectoryTextBox.Text = instance.AltSaveDirectoryName;
        MapPortTextBox.Text = instance.Port.ToString();
        MapQueryPortTextBox.Text = instance.QueryPort.ToString();
        MapRconEnabledCheckBox.IsChecked = instance.RconEnabled;
        MapRconPortTextBox.Text = instance.RconPort.ToString();
        MapAdminPasswordBox.Password = instance.AdminPassword;
        MapMaxPlayersTextBox.Text = instance.MaxPlayers.ToString();
        MapSettingsOverlay.Visibility = Visibility.Visible;
        MapDisplayNameTextBox.Focus();
        MapDisplayNameTextBox.SelectAll();
    }

    private void MapPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressPresetChange || MapPresetComboBox.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        var mapCode = item.Tag as string;
        if (string.IsNullOrWhiteSpace(mapCode))
        {
            return;
        }

        var displayName = item.Content?.ToString() ?? mapCode;
        MapDisplayNameTextBox.Text = displayName;
        MapCodeTextBox.Text = mapCode;
        MapSessionNameTextBox.Text = $"{MakeLaunchToken(displayName)}Server";
        MapSaveDirectoryTextBox.Text = $"{MakeLaunchToken(displayName)}Save";
    }

    private void SelectPresetForMapCode(string mapCode)
    {
        foreach (var item in MapPresetComboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, mapCode, StringComparison.OrdinalIgnoreCase))
            {
                MapPresetComboBox.SelectedItem = item;
                return;
            }
        }

        MapPresetComboBox.SelectedIndex = MapPresetComboBox.Items.Count - 1;
    }

    private void CancelMapSettings_Click(object sender, RoutedEventArgs e)
    {
        _editingMap = null;
        _isAddingMap = false;
        MapSettingsOverlay.Visibility = Visibility.Collapsed;
        DemoStatusText.Text = "Изменение настроек карты отменено.";
    }

    private void SaveMapSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_editingCluster is null || _editingMap is null)
        {
            MapSettingsOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        if (!TryReadMapSettings(out var mapSettings))
        {
            return;
        }

        if (HasPortConflict(_editingCluster, _editingMap, mapSettings))
        {
            ShowValidationError("У другой карты уже используется один из этих портов. Port, QueryPort и RCONPort должны отличаться между картами.");
            return;
        }

        if (_isAddingMap)
        {
            _editingMap.UpdateSettings(mapSettings);
            _editingCluster.Instances.Add(_editingMap);
        }
        else
        {
            _editingMap.UpdateSettings(mapSettings);
        }

        PersistClusterSettings(_editingCluster);
        _editingCluster.RefreshSummary();
        RefreshOverview();

        DemoStatusText.Text = $"{_editingMap.DisplayName}: настройки карты сохранены.";
        _editingMap = null;
        _isAddingMap = false;
        MapSettingsOverlay.Visibility = Visibility.Collapsed;
    }

    private void ImportBat_Click(object sender, RoutedEventArgs e)
    {
        var cluster = Clusters.FirstOrDefault();
        if (cluster is null)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Импортировать .bat/.cmd с запуском карты",
            Filter = "Batch files|*.bat;*.cmd|Text files|*.txt|All files|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var errors = new List<string>();
        foreach (var line in File.ReadLines(dialog.FileName))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)
                || trimmed.StartsWith("rem ", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("::", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("@echo", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!ArkServerLaunchCommand.TryParseLaunchLine(trimmed, out var parsed, out var error) || parsed is null)
            {
                errors.Add(error);
                continue;
            }

            ImportParsedCommand(cluster, parsed);
            PersistClusterSettings(cluster);
            cluster.RefreshSummary();
            RefreshOverview();
            DemoStatusText.Text = $"Импортирована карта {parsed.Map.DisplayName} из {Path.GetFileName(dialog.FileName)}.";
            return;
        }

        var details = errors.Count > 0 ? $"\n\nПоследняя ошибка: {errors.Last()}" : string.Empty;
        ShowValidationError($"В файле не найдена поддерживаемая строка запуска ArkAscendedServer.exe.{details}");
    }

    private async void RefreshStatuses_Click(object sender, RoutedEventArgs e)
    {
        AttachAlreadyRunningServers();

        foreach (var instance in Clusters.SelectMany(cluster => cluster.Instances))
        {
            if (instance.IsProcessActive)
            {
                await TryUpdateFromServerConsoleAsync(instance);
                RefreshAfterInstanceChange(instance);
            }
        }

        DemoStatusText.Text = "Статусы серверов обновлены.";
    }

    private void StartServerInstance(ServerMapInstance instance)
    {
        if (!EnsureExecutableConfigured())
        {
            return;
        }

        var cluster = FindCluster(instance);
        if (cluster is null)
        {
            return;
        }

        if (instance.IsProcessActive)
        {
            DemoStatusText.Text = $"{instance.DisplayName}: процесс уже запущен.";
            return;
        }

        try
        {
            var executablePath = _settings.ServerExecutablePath!;
            var clusterSettings = cluster.ToSettings();
            var mapSettings = instance.ToSettings();
            var startInfo = ArkServerLaunchCommand.CreateStartInfo(executablePath, clusterSettings, mapSettings);
            var process = Process.Start(startInfo);
            if (process is null)
            {
                ShowValidationError("Windows не вернул запущенный процесс сервера.");
                return;
            }

            process.EnableRaisingEvents = true;
            process.Exited += (_, _) =>
            {
                if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
                {
                    _ = Dispatcher.BeginInvoke(() => OnServerProcessExited(instance, process));
                }
            };

            var command = ArkServerLaunchCommand.BuildDisplayCommand(executablePath, clusterSettings, mapSettings);
            instance.SetStarting(process, command);
            DemoStatusText.Text = $"{instance.DisplayName}: процесс запущен, ждём Server Ready в окне ASA.";
            RefreshAfterInstanceChange(instance);
            StartRuntimePolling(instance, process);
        }
        catch (Exception ex)
        {
            instance.SetCrashed();
            RefreshAfterInstanceChange(instance);
            ShowValidationError($"Не удалось запустить {instance.DisplayName}: {ex.Message}");
        }
    }

    private void AttachAlreadyRunningServers()
    {
        var attachedCount = AttachRunningServersFromCommandLine();

        foreach (var instance in Clusters.SelectMany(cluster => cluster.Instances))
        {
            if (instance.IsProcessActive)
            {
                continue;
            }

            var title = _titleProbe.FindTitleForServer(null, instance);
            if (!AsaConsoleTitleParser.TryParse(title ?? string.Empty, out var info) || info.ProcessId is null)
            {
                continue;
            }

            try
            {
                var process = Process.GetProcessById(info.ProcessId.Value);
                AttachProcessToInstance(instance, process, $"attached from console title: {title}");
                attachedCount++;
                DemoStatusText.Text = $"{instance.DisplayName}: подхвачен уже запущенный сервер PID {process.Id}.";
            }
            catch
            {
                // Ignore stale titles/processes; polling will continue for managed launches.
            }
        }

        if (attachedCount > 0)
        {
            DemoStatusText.Text = $"Подхвачено уже запущенных серверов: {attachedCount}.";
        }
    }

    private int AttachRunningServersFromCommandLine()
    {
        var attachedCount = 0;
        var seenProcessIds = new HashSet<int>(
            Clusters
                .SelectMany(cluster => cluster.Instances)
                .Where(instance => instance.ProcessId > 0)
                .Select(instance => instance.ProcessId));

        foreach (var process in EnumerateServerProcesses())
        {
            if (seenProcessIds.Contains(process.Id))
            {
                process.Dispose();
                continue;
            }

            var attached = false;
            try
            {
                var commandLine = ProcessCommandLineReader.TryRead(process);
                if (string.IsNullOrWhiteSpace(commandLine)
                    || !ArkServerLaunchCommand.TryParseLaunchLine(commandLine, out var parsed, out _)
                    || parsed is null)
                {
                    continue;
                }

                var match = FindMatchingInstance(parsed);
                if (match is null)
                {
                    continue;
                }

                AttachProcessToInstance(match, process, $"attached from command line: {BuildAttachSummary(parsed)}");
                seenProcessIds.Add(process.Id);
                attachedCount++;
                attached = true;
            }
            catch
            {
                // A protected or exiting process should not break status refresh.
            }
            finally
            {
                if (!attached)
                {
                    process.Dispose();
                }
            }
        }

        return attachedCount;
    }

    private static IEnumerable<Process> EnumerateServerProcesses()
    {
        var seen = new HashSet<int>();
        foreach (var processName in new[] { "ArkAscendedServer", "ArkAscendedDedicatedServer" })
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(processName);
            }
            catch
            {
                continue;
            }

            foreach (var process in processes)
            {
                if (seen.Add(process.Id))
                {
                    yield return process;
                }
                else
                {
                    process.Dispose();
                }
            }
        }
    }

    private ServerMapInstance? FindMatchingInstance(ParsedLaunchCommand parsed)
    {
        foreach (var cluster in Clusters)
        {
            if (!string.IsNullOrWhiteSpace(parsed.ClusterId)
                && !parsed.ClusterId.Equals(cluster.ClusterId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var instance in cluster.Instances)
            {
                if (instance.IsProcessActive)
                {
                    continue;
                }

                if (!parsed.Map.MapCode.Equals(instance.MapCode, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (parsed.Map.Port != instance.Port || parsed.Map.QueryPort != instance.QueryPort)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(parsed.Map.SessionName)
                    && !parsed.Map.SessionName.Equals(instance.SessionName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return instance;
            }
        }

        return null;
    }

    private void AttachProcessToInstance(ServerMapInstance instance, Process process, string source)
    {
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
            {
                _ = Dispatcher.BeginInvoke(() => OnServerProcessExited(instance, process));
            }
        };

        instance.AttachRunningProcess(process, source);
        StartRuntimePolling(instance, process);
        RefreshAfterInstanceChange(instance);
    }

    private static string BuildAttachSummary(ParsedLaunchCommand parsed)
    {
        var cluster = string.IsNullOrWhiteSpace(parsed.ClusterId) ? "no clusterid" : "cluster matched";
        return $"{parsed.Map.MapCode}, {parsed.Map.SessionName}, ports {parsed.Map.Port}/{parsed.Map.QueryPort}, {cluster}";
    }

    private async Task StopServerInstanceAsync(ServerMapInstance instance)
    {
        CancelRuntimeTimer(instance);

        if (!instance.IsProcessActive)
        {
            instance.SetOffline();
            RefreshAfterInstanceChange(instance);
            return;
        }

        var process = instance.AttachedProcess;
        if (process is null)
        {
            instance.SetOffline();
            RefreshAfterInstanceChange(instance);
            return;
        }

        instance.SetStopping();
        DemoStatusText.Text = $"{instance.DisplayName}: останавливаем процесс PID {instance.ProcessId}.";
        RefreshAfterInstanceChange(instance);

        var stopped = await Task.Run(() => StopProcess(process));

        if (ReferenceEquals(instance.AttachedProcess, process))
        {
            if (stopped)
            {
                instance.SetOffline();
                process.Dispose();
            }
            else
            {
                instance.SetOnlineOrStartingAfterFailedStop();
                DemoStatusText.Text = $"Не удалось остановить {instance.DisplayName}. Процесс всё ещё может работать.";
            }

            RefreshAfterInstanceChange(instance);
        }

        DemoStatusText.Text = stopped
            ? $"{instance.DisplayName}: остановлена."
            : $"{instance.DisplayName}: остановка не завершилась.";
    }

    private void StopInstanceSynchronously(ServerMapInstance instance)
    {
        CancelRuntimeTimer(instance);
        if (instance.AttachedProcess is not { } process)
        {
            return;
        }

        instance.SetStopping();
        if (StopProcess(process))
        {
            instance.SetOffline();
            process.Dispose();
        }
        else
        {
            instance.SetOnlineOrStartingAfterFailedStop();
        }
    }

    private static bool StopProcess(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return true;
            }

            if (process.CloseMainWindow())
            {
                process.WaitForExit(2_000);
            }

            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3_000);
            }

            return process.HasExited;
        }
        catch
        {
            // The process may exit between HasExited and Kill. The UI reconciles state via Exited.
            try
            {
                return process.HasExited;
            }
            catch
            {
                return false;
            }
        }
    }

    private void OnServerProcessExited(ServerMapInstance instance, Process process)
    {
        CancelRuntimeTimer(instance);

        if (!ReferenceEquals(instance.AttachedProcess, process))
        {
            return;
        }

        var wasStopping = instance.Status == ServerRuntimeStatus.Stopping;
        instance.SetOfflineFromExit(wasStopping);
        process.Dispose();
        RefreshAfterInstanceChange(instance);

        DemoStatusText.Text = wasStopping
            ? $"{instance.DisplayName}: процесс остановлен."
            : $"{instance.DisplayName}: процесс завершился сам.";
    }

    private void StartRuntimePolling(ServerMapInstance instance, Process process)
    {
        CancelRuntimeTimer(instance);

        var isPolling = false;
        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };

        timer.Tick += async (_, _) =>
        {
            if (isPolling)
            {
                return;
            }

            if (!ReferenceEquals(instance.AttachedProcess, process) || !instance.IsProcessActive)
            {
                CancelRuntimeTimer(instance);
                return;
            }

            isPolling = true;
            try
            {
                var consoleReady = await TryUpdateFromServerConsoleAsync(instance);
                if (!consoleReady)
                {
                    instance.RefreshRuntimeDisplay();
                }

                RefreshAfterInstanceChange(instance);
            }
            catch (Exception ex)
            {
                DemoStatusText.Text = $"{instance.DisplayName}: проверка статуса не удалась ({ex.GetType().Name}), продолжаем.";
            }
            finally
            {
                isPolling = false;
            }
        };

        _runtimeTimers[instance] = timer;
        timer.Start();
        _ = PollImmediatelyAsync(instance, process);
    }

    private async Task PollImmediatelyAsync(ServerMapInstance instance, Process process)
    {
        try
        {
            await Task.Delay(700);

            if (!ReferenceEquals(instance.AttachedProcess, process) || !instance.IsProcessActive)
            {
                return;
            }

            var consoleReady = await TryUpdateFromServerConsoleAsync(instance);
            if (!consoleReady)
            {
                instance.RefreshRuntimeDisplay();
            }

            RefreshAfterInstanceChange(instance);
        }
        catch (Exception ex)
        {
            DemoStatusText.Text = $"{instance.DisplayName}: первичная проверка статуса не удалась ({ex.GetType().Name}).";
        }
    }

    private async Task<bool> TryUpdateFromServerConsoleAsync(ServerMapInstance instance)
    {
        try
        {
            var status = await Task.Run(() => _serverConsoleProbe.TryRead(instance.AttachedProcess, instance));
            if (status is null)
            {
                instance.UpdateStatusProbeDetail("server console not found", preserveAuthoritativeReady: true);
                return false;
            }

            if (!status.IsReady)
            {
                instance.UpdateStatusProbeDetail(status.StatusText ?? "server console pending", preserveAuthoritativeReady: true);
                return false;
            }

            instance.SetReadyNoQuery(status.StatusText ?? "Server Ready", status.Uptime);
            return true;
        }
        catch (Exception ex)
        {
            instance.UpdateStatusProbeDetail($"server console error: {ex.GetType().Name}", preserveAuthoritativeReady: true);
            return false;
        }
    }

    private void CancelRuntimeTimer(ServerMapInstance instance)
    {
        if (_runtimeTimers.Remove(instance, out var timer))
        {
            timer.Stop();
        }
    }

    private bool EnsureExecutableConfigured()
    {
        if (!string.IsNullOrWhiteSpace(_settings.ServerExecutablePath) && File.Exists(_settings.ServerExecutablePath))
        {
            return true;
        }

        MarkServerExecutableMissing("Перед запуском карты нужно выбрать ArkAscendedServer.exe.");
        var result = MessageBox.Show(
            this,
            "Сначала нужно выбрать ArkAscendedServer.exe. Открыть выбор файла?",
            "Нет пути к серверу",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            ChooseServerExecutableManually();
        }

        return !string.IsNullOrWhiteSpace(_settings.ServerExecutablePath) && File.Exists(_settings.ServerExecutablePath);
    }

    private bool TryReadMapSettings(out MapSettings mapSettings)
    {
        mapSettings = new MapSettings();

        var displayName = MapDisplayNameTextBox.Text.Trim();
        var mapCode = MapCodeTextBox.Text.Trim();
        var sessionName = MapSessionNameTextBox.Text.Trim();
        var saveDirectory = MapSaveDirectoryTextBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(displayName))
        {
            ShowValidationError("Название карты в лаунчере не должно быть пустым.");
            return false;
        }

        if (!ValidateSimpleToken(mapCode, "Код карты")
            || !ValidateLaunchValue(sessionName, "SessionName")
            || !ValidateLaunchValue(saveDirectory, "AltSaveDirectoryName"))
        {
            return false;
        }

        if (!TryReadPort(MapPortTextBox.Text, "Port", out var port)
            || !TryReadPort(MapQueryPortTextBox.Text, "QueryPort", out var queryPort)
            || !TryReadPositiveInt(MapMaxPlayersTextBox.Text, "Max players", out var maxPlayers))
        {
            return false;
        }

        var rconEnabled = MapRconEnabledCheckBox.IsChecked == true;
        var rconPortText = MapRconPortTextBox.Text.Trim();
        var rconPort = _editingMap?.RconPort > 0 ? _editingMap.RconPort : 27020;
        var adminPassword = MapAdminPasswordBox.Password.Trim();

        if ((rconEnabled || !string.IsNullOrWhiteSpace(rconPortText))
            && !TryReadPort(rconPortText, "RCONPort", out rconPort))
        {
            return false;
        }

        if (port == queryPort)
        {
            ShowValidationError("Port и QueryPort должны быть разными.");
            return false;
        }

        if (rconEnabled && (rconPort == port || rconPort == queryPort))
        {
            ShowValidationError("RCONPort должен отличаться от Port и QueryPort.");
            return false;
        }

        if (!string.IsNullOrWhiteSpace(adminPassword) && !ValidateLaunchValue(adminPassword, "ServerAdminPassword"))
        {
            return false;
        }

        if (rconEnabled && string.IsNullOrWhiteSpace(adminPassword))
        {
            ShowValidationError("Если RCON включен, нужен ServerAdminPassword.");
            return false;
        }

        var accent = _editingMap?.AccentColor ?? PickAccentColor(Clusters.FirstOrDefault()?.Instances.Count ?? 0);
        var foreground = _editingMap?.InitialsForegroundColor ?? "#F4FFFB";

        mapSettings = new MapSettings
        {
            DisplayName = displayName,
            MapCode = mapCode,
            SessionName = sessionName,
            AltSaveDirectoryName = saveDirectory,
            Port = port,
            QueryPort = queryPort,
            RconEnabled = rconEnabled,
            RconPort = rconPort,
            AdminPassword = adminPassword,
            MaxPlayers = maxPlayers,
            AccentColor = accent,
            InitialsForeground = foreground
        };

        return true;
    }

    private void FillGameSettingsFields(GameServerSettings settings)
    {
        HarvestAmountTextBox.Text = FormatSetting(settings.HarvestAmountMultiplier);
        XpMultiplierTextBox.Text = FormatSetting(settings.XpMultiplier);
        TamingSpeedTextBox.Text = FormatSetting(settings.TamingSpeedMultiplier);
        ItemStackSizeTextBox.Text = FormatSetting(settings.ItemStackSizeMultiplier);
        StructurePreventRadiusTextBox.Text = FormatSetting(settings.StructurePreventResourceRadiusMultiplier);
        AutoSaveMinutesTextBox.Text = FormatSetting(settings.AutoSavePeriodMinutes);
        DayCycleSpeedTextBox.Text = FormatSetting(settings.DayCycleSpeedScale);
        NightSpeedTextBox.Text = FormatSetting(settings.NightTimeSpeedScale);
        DifficultyOffsetTextBox.Text = FormatSetting(settings.DifficultyOffset);
        StructurePickupTimeTextBox.Text = FormatSetting(settings.StructurePickupTimeAfterPlacement);
        MatingIntervalTextBox.Text = FormatSetting(settings.MatingIntervalMultiplier);
        EggHatchSpeedTextBox.Text = FormatSetting(settings.EggHatchSpeedMultiplier);
        BabyMatureSpeedTextBox.Text = FormatSetting(settings.BabyMatureSpeedMultiplier);
        BabyCuddleIntervalTextBox.Text = FormatSetting(settings.BabyCuddleIntervalMultiplier);
        BabyImprintAmountTextBox.Text = FormatSetting(settings.BabyImprintAmountMultiplier);
        CropGrowthSpeedTextBox.Text = FormatSetting(settings.CropGrowthSpeedMultiplier);

        ShowMapPlayerLocationCheckBox.IsChecked = settings.ShowMapPlayerLocation;
        AllowThirdPersonCheckBox.IsChecked = settings.AllowThirdPersonPlayer;
        ServerCrosshairCheckBox.IsChecked = settings.ServerCrosshair;
        StructurePickupCheckBox.IsChecked = settings.AlwaysAllowStructurePickup;
    }

    private bool TryReadGameSettings(out GameServerSettings settings)
    {
        settings = new GameServerSettings();

        if (!TryReadPositiveDouble(HarvestAmountTextBox.Text, "Множитель ресурсов", out var harvest)
            || !TryReadPositiveDouble(XpMultiplierTextBox.Text, "Множитель опыта", out var xp)
            || !TryReadPositiveDouble(TamingSpeedTextBox.Text, "Скорость приручения", out var taming)
            || !TryReadPositiveDouble(ItemStackSizeTextBox.Text, "Множитель стака предметов", out var stack)
            || !TryReadPositiveDouble(StructurePreventRadiusTextBox.Text, "Радиус запрета ресурсов около построек", out var structureRadius)
            || !TryReadPositiveDouble(AutoSaveMinutesTextBox.Text, "Автосохранение, минут", out var autoSave)
            || !TryReadPositiveDouble(DayCycleSpeedTextBox.Text, "Скорость цикла дня", out var day)
            || !TryReadPositiveDouble(NightSpeedTextBox.Text, "Скорость ночи", out var night)
            || !TryReadPositiveDouble(DifficultyOffsetTextBox.Text, "DifficultyOffset", out var difficulty)
            || !TryReadNonNegativeDouble(StructurePickupTimeTextBox.Text, "Время подбора построек", out var pickupTime)
            || !TryReadPositiveDouble(MatingIntervalTextBox.Text, "Интервал спаривания", out var mating)
            || !TryReadPositiveDouble(EggHatchSpeedTextBox.Text, "Скорость вылупления", out var hatch)
            || !TryReadPositiveDouble(BabyMatureSpeedTextBox.Text, "Скорость взросления детенышей", out var mature)
            || !TryReadPositiveDouble(BabyCuddleIntervalTextBox.Text, "Интервал заботы о детеныше", out var cuddle)
            || !TryReadPositiveDouble(BabyImprintAmountTextBox.Text, "Сила импринта", out var imprint)
            || !TryReadPositiveDouble(CropGrowthSpeedTextBox.Text, "Скорость роста растений", out var crop))
        {
            return false;
        }

        settings = new GameServerSettings
        {
            HarvestAmountMultiplier = harvest,
            XpMultiplier = xp,
            TamingSpeedMultiplier = taming,
            ItemStackSizeMultiplier = stack,
            StructurePreventResourceRadiusMultiplier = structureRadius,
            AutoSavePeriodMinutes = autoSave,
            DayCycleSpeedScale = day,
            NightTimeSpeedScale = night,
            DifficultyOffset = difficulty,
            StructurePickupTimeAfterPlacement = pickupTime,
            ShowMapPlayerLocation = ShowMapPlayerLocationCheckBox.IsChecked == true,
            AllowThirdPersonPlayer = AllowThirdPersonCheckBox.IsChecked == true,
            ServerCrosshair = ServerCrosshairCheckBox.IsChecked == true,
            AlwaysAllowStructurePickup = StructurePickupCheckBox.IsChecked == true,
            MatingIntervalMultiplier = mating,
            EggHatchSpeedMultiplier = hatch,
            BabyMatureSpeedMultiplier = mature,
            BabyCuddleIntervalMultiplier = cuddle,
            BabyImprintAmountMultiplier = imprint,
            CropGrowthSpeedMultiplier = crop
        };

        return true;
    }

    private static bool HasPortConflict(ServerCluster cluster, ServerMapInstance editedInstance, MapSettings newSettings)
    {
        var newPorts = GetPorts(newSettings).ToHashSet();
        return cluster.Instances
            .Where(instance => !ReferenceEquals(instance, editedInstance))
            .Any(instance => GetPorts(instance).Any(newPorts.Contains));
    }

    private static IEnumerable<int> GetPorts(MapSettings settings)
    {
        yield return settings.Port;
        yield return settings.QueryPort;
        if (settings.RconEnabled)
        {
            yield return settings.RconPort;
        }
    }

    private static IEnumerable<int> GetPorts(ServerMapInstance instance)
    {
        yield return instance.Port;
        yield return instance.QueryPort;
        if (instance.RconEnabled)
        {
            yield return instance.RconPort;
        }
    }

    private bool ValidateSimpleToken(string value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            ShowValidationError($"{fieldName} не должен быть пустым.");
            return false;
        }

        if (value.Any(char.IsWhiteSpace) || value.Contains('?') || value.Contains('"'))
        {
            ShowValidationError($"{fieldName} лучше хранить без пробелов, вопросительных знаков и кавычек.");
            return false;
        }

        return true;
    }

    private bool ValidateLaunchValue(string value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            ShowValidationError($"{fieldName} не должен быть пустым.");
            return false;
        }

        if (value.Contains('?') || value.Contains('"'))
        {
            ShowValidationError($"{fieldName} не должен содержать ? или кавычки.");
            return false;
        }

        return true;
    }

    private bool TryReadPort(string value, string fieldName, out int port)
    {
        if (!int.TryParse(value.Trim(), out port) || port is < 1 or > 65535)
        {
            ShowValidationError($"{fieldName} должен быть числом от 1 до 65535.");
            return false;
        }

        return true;
    }

    private bool TryReadPositiveInt(string value, string fieldName, out int number)
    {
        if (!int.TryParse(value.Trim(), out number) || number < 1)
        {
            ShowValidationError($"{fieldName} должен быть положительным числом.");
            return false;
        }

        return true;
    }

    private bool TryReadPositiveDouble(string value, string fieldName, out double number)
    {
        if (!TryReadDouble(value, fieldName, out number) || number <= 0)
        {
            ShowValidationError($"{fieldName} должен быть положительным числом.");
            return false;
        }

        return true;
    }

    private bool TryReadNonNegativeDouble(string value, string fieldName, out double number)
    {
        if (!TryReadDouble(value, fieldName, out number) || number < 0)
        {
            ShowValidationError($"{fieldName} должен быть числом 0 или больше.");
            return false;
        }

        return true;
    }

    private bool TryReadDouble(string value, string fieldName, out double number)
    {
        var normalized = value.Trim().Replace(',', '.');
        if (!double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out number) || !double.IsFinite(number))
        {
            ShowValidationError($"{fieldName} должен быть числом.");
            return false;
        }

        return true;
    }

    private static string FormatSetting(double value)
    {
        return value.ToString("0.######", CultureInfo.InvariantCulture);
    }

    private string? NormalizeModsOrShowError(string rawMods)
    {
        var normalized = ArkServerLaunchCommand.NormalizeMods(rawMods);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        var mods = normalized.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (mods.Any(mod => !long.TryParse(mod, out _)))
        {
            ShowValidationError("Моды должны быть указаны как числовые Steam Workshop ID, например: 947033,939228,952945.");
            return null;
        }

        return normalized;
    }

    private void ImportParsedCommand(ServerCluster cluster, ParsedLaunchCommand parsed)
    {
        if (!string.IsNullOrWhiteSpace(parsed.ClusterId))
        {
            cluster.UpdateSettings(cluster.Name, parsed.ClusterId, parsed.CommonMods, parsed.LaunchFlags);
        }
        else
        {
            cluster.UpdateSettings(cluster.Name, cluster.ClusterId, parsed.CommonMods, parsed.LaunchFlags);
        }

        var existing = cluster.Instances.FirstOrDefault(instance =>
            instance.MapCode.Equals(parsed.Map.MapCode, StringComparison.OrdinalIgnoreCase)
            || instance.SessionName.Equals(parsed.Map.SessionName, StringComparison.OrdinalIgnoreCase));

        parsed.Map.AccentColor = existing?.AccentColor ?? PickAccentColor(cluster.Instances.Count);
        parsed.Map.InitialsForeground = existing?.InitialsForegroundColor ?? "#F4FFFB";

        if (existing is not null && !existing.IsProcessActive)
        {
            existing.UpdateSettings(parsed.Map);
            return;
        }

        cluster.Instances.Add(new ServerMapInstance(parsed.Map));
    }

    private void PersistClusterSettings(ServerCluster cluster)
    {
        _settings.HasCluster = true;
        _settings.Cluster = cluster.ToSettings();
        SaveSettings();
    }

    private ServerCluster? FindCluster(ServerMapInstance instance)
    {
        return Clusters.FirstOrDefault(cluster => cluster.Instances.Contains(instance));
    }

    private static MapSettings CreateNewMapSettings(ServerCluster cluster)
    {
        var mapCount = cluster.Instances.Count;
        var preset = PickNextMapPreset(cluster);
        var token = MakeLaunchToken(preset.DisplayName);

        return new MapSettings
        {
            DisplayName = preset.DisplayName,
            MapCode = preset.MapCode,
            SessionName = $"{token}Server",
            AltSaveDirectoryName = $"{token}Save",
            Port = 7777 + (mapCount * 10),
            QueryPort = 27015 + mapCount,
            RconPort = 27020 + mapCount,
            MaxPlayers = 70,
            AccentColor = preset.AccentColor,
            InitialsForeground = "#F4FFFB"
        };
    }

    private static MapPreset PickNextMapPreset(ServerCluster cluster)
    {
        var usedMapCodes = cluster.Instances
            .Select(instance => instance.MapCode)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return MapPresets.FirstOrDefault(preset => !usedMapCodes.Contains(preset.MapCode))
               ?? new MapPreset($"Custom Map {cluster.Instances.Count + 1}", "TheIsland_WP", PickAccentColor(cluster.Instances.Count));
    }

    private static string MakeLaunchToken(string value)
    {
        var chars = value.Where(char.IsLetterOrDigit).ToArray();
        return chars.Length == 0 ? "Server" : new string(chars);
    }

    private static string PickAccentColor(int index)
    {
        string[] colors =
        [
            "#2F6F63",
            "#5D4A2E",
            "#29313B",
            "#4B3B59",
            "#31446A",
            "#5B3842"
        ];

        return colors[index % colors.Length];
    }

    private void RefreshAfterInstanceChange(ServerMapInstance instance)
    {
        var cluster = FindCluster(instance);
        cluster?.RefreshSummary();
        RefreshOverview();
    }

    private void RefreshOverview()
    {
        OnPropertyChanged(nameof(ClusterCountText));
        OnPropertyChanged(nameof(CanCreateCluster));
        OnPropertyChanged(nameof(HasCluster));
        OnPropertyChanged(nameof(OnlineMapsText));
        OnPropertyChanged(nameof(HeaderStatusText));
        OnPropertyChanged(nameof(HeaderStatusBrush));
    }

    private void SaveSettings()
    {
        try
        {
            _settingsStore.Save(_settings);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"Не удалось сохранить настройки: {ex.Message}",
                "Obelisk Launcher",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void SaveSettingsQuietly()
    {
        try
        {
            _settingsStore.Save(_settings);
        }
        catch (Exception ex)
        {
            DemoStatusText.Text = $"Настройки не сохранились: {ex.Message}";
        }
    }

    private void ShowValidationError(string message)
    {
        MessageBox.Show(this, message, "Проверь настройки", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static bool TryGetInstance(object sender, out ServerMapInstance instance)
    {
        instance = null!;

        if (sender is Button { Tag: ServerMapInstance taggedInstance })
        {
            instance = taggedInstance;
            return true;
        }

        return false;
    }

    private static bool TryGetCluster(object sender, out ServerCluster cluster)
    {
        cluster = null!;

        if (sender is Button { Tag: ServerCluster taggedCluster })
        {
            cluster = taggedCluster;
            return true;
        }

        return false;
    }

    private static ObservableCollection<ServerCluster> CreateClusters(LauncherSettings settings)
    {
        if (!settings.HasCluster)
        {
            return [];
        }

        return [CreateCluster(settings.Cluster)];
    }

    private static ServerCluster CreateCluster(ClusterSettings clusterSettings)
    {
        var cluster = new ServerCluster(
            clusterSettings.Name,
            clusterSettings.ClusterId,
            clusterSettings.CommonMods,
            clusterSettings.LaunchFlags,
            new ObservableCollection<ServerMapInstance>(clusterSettings.Maps.Select(map => new ServerMapInstance(map))));
        cluster.RefreshSummary();
        return cluster;
    }

    public static SolidColorBrush BrushFrom(string color)
    {
        return new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class ServerCluster : INotifyPropertyChanged
{
    private const string HiddenClusterIdMask = "**********";

    public ServerCluster(
        string name,
        string clusterId,
        string commonMods,
        string launchFlags,
        ObservableCollection<ServerMapInstance> instances)
    {
        Name = name;
        ClusterId = clusterId;
        CommonMods = commonMods;
        LaunchFlags = launchFlags;
        Instances = instances;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; private set; }

    public string Title => $"Кластер \"{Name}\"";

    public string ClusterId { get; private set; }

    public string CommonMods { get; private set; }

    public string LaunchFlags { get; private set; }

    public bool IsClusterIdVisible { get; private set; }

    public string ClusterIdDisplay => IsClusterIdVisible ? ClusterId : HiddenClusterIdMask;

    public string ClusterIdToggleTooltip => IsClusterIdVisible ? "Скрыть Cluster ID" : "Показать Cluster ID";

    public ObservableCollection<ServerMapInstance> Instances { get; }

    public string SummaryText { get; private set; } = string.Empty;

    public Brush SummaryBackground { get; private set; } = Brushes.Transparent;

    public Brush SummaryBorder { get; private set; } = Brushes.Transparent;

    public Brush SummaryForeground { get; private set; } = Brushes.White;

    public ClusterSettings ToSettings()
    {
        return new ClusterSettings
        {
            Name = Name,
            ClusterId = ClusterId,
            CommonMods = CommonMods,
            LaunchFlags = LaunchFlags,
            Maps = Instances.Select(instance => instance.ToSettings()).ToList()
        };
    }

    public void UpdateSettings(string name, string clusterId, string commonMods, string launchFlags)
    {
        Name = name;
        ClusterId = clusterId;
        CommonMods = commonMods;
        LaunchFlags = launchFlags;

        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(ClusterId));
        OnPropertyChanged(nameof(CommonMods));
        OnPropertyChanged(nameof(LaunchFlags));
        OnPropertyChanged(nameof(ClusterIdDisplay));
    }

    public void RefreshSummary()
    {
        var starting = Instances.Count(instance => instance.Status == ServerRuntimeStatus.Starting);
        var ready = Instances.Count(instance => instance.Status == ServerRuntimeStatus.ReadyNoQuery);
        var running = Instances.Count(instance => instance.Status == ServerRuntimeStatus.Online);
        var online = ready + running;
        var crashed = Instances.Count(instance => instance.Status == ServerRuntimeStatus.Crashed);

        if (starting > 0)
        {
            SummaryText = $"Запуск: {starting} карт стартует";
            SummaryBackground = MainWindow.BrushFrom("#2B2520");
            SummaryBorder = MainWindow.BrushFrom("#B78A43");
            SummaryForeground = MainWindow.BrushFrom("#FFE7B0");
        }
        else if (online > 0)
        {
            SummaryText = $"Онлайн: {online} карт";
            SummaryBackground = MainWindow.BrushFrom("#173E33");
            SummaryBorder = MainWindow.BrushFrom("#3AAE72");
            SummaryForeground = MainWindow.BrushFrom("#D9FFF0");
        }
        else if (crashed > 0)
        {
            SummaryText = $"Ошибка: {crashed} карт завершились";
            SummaryBackground = MainWindow.BrushFrom("#3B2424");
            SummaryBorder = MainWindow.BrushFrom("#C65A52");
            SummaryForeground = MainWindow.BrushFrom("#FFD3D7");
        }
        else
        {
            SummaryText = "Оффлайн: карты не запущены";
            SummaryBackground = MainWindow.BrushFrom("#2B2022");
            SummaryBorder = MainWindow.BrushFrom("#8B464B");
            SummaryForeground = MainWindow.BrushFrom("#FFD3D7");
        }

        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(SummaryBackground));
        OnPropertyChanged(nameof(SummaryBorder));
        OnPropertyChanged(nameof(SummaryForeground));
    }

    public void ToggleClusterIdVisibility()
    {
        IsClusterIdVisible = !IsClusterIdVisible;
        OnPropertyChanged(nameof(IsClusterIdVisible));
        OnPropertyChanged(nameof(ClusterIdDisplay));
        OnPropertyChanged(nameof(ClusterIdToggleTooltip));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class ServerMapInstance : INotifyPropertyChanged
{
    public ServerMapInstance(MapSettings settings)
    {
        UpdateSettings(settings);
        Status = ServerRuntimeStatus.Offline;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Process? AttachedProcess { get; private set; }

    public string DisplayName { get; private set; } = string.Empty;

    public string MapCode { get; private set; } = string.Empty;

    public string SessionName { get; private set; } = string.Empty;

    public string AltSaveDirectoryName { get; private set; } = string.Empty;

    public int Port { get; private set; }

    public int QueryPort { get; private set; }

    public bool RconEnabled { get; private set; }

    public int RconPort { get; private set; }

    public string AdminPassword { get; private set; } = string.Empty;

    public int MaxPlayers { get; private set; }

    public string AccentColor { get; private set; } = "#2F6F63";

    public Brush AccentBrush { get; private set; } = Brushes.Transparent;

    public string InitialsForegroundColor { get; private set; } = "#F4FFFB";

    public Brush InitialsForeground { get; private set; } = Brushes.White;

    public ServerRuntimeStatus Status { get; private set; }

    public int ProcessId { get; private set; }

    public DateTime? StartedAtUtc { get; private set; }

    public int PlayerCount { get; private set; }

    public int QueryMaxPlayers { get; private set; }

    public string QueryEndpoint { get; private set; } = string.Empty;

    public string StatusProbeDetail { get; private set; } = "waiting";

    public string LastLaunchCommand { get; private set; } = string.Empty;

    public string MapName => DisplayName;

    public string Initials
    {
        get
        {
            var words = DisplayName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length >= 2)
            {
                return $"{words[0][0]}{words[1][0]}".ToUpperInvariant();
            }

            return DisplayName.Length >= 2 ? DisplayName[..2].ToUpperInvariant() : DisplayName.ToUpperInvariant();
        }
    }

    public string Ports => RconEnabled
        ? $"{Port} / {QueryPort} / RCON {RconPort}"
        : $"{Port} / {QueryPort}";

    public string RuntimeDetails => ProcessId > 0
        ? $"{SessionName} - PID {ProcessId}{BuildQueryDetail()}"
        : SessionName;

    public string OnlineText => Status == ServerRuntimeStatus.Online
        ? $"{PlayerCount} / {Math.Max(QueryMaxPlayers, MaxPlayers)}"
        : Status == ServerRuntimeStatus.ReadyNoQuery
            ? $"? / {MaxPlayers}"
        : $"- / {MaxPlayers}";

    public bool IsProcessActive
    {
        get
        {
            try
            {
                return AttachedProcess is not null && !AttachedProcess.HasExited;
            }
            catch
            {
                return false;
            }
        }
    }

    public string StatusText => Status switch
    {
        ServerRuntimeStatus.Starting => StartedAtUtc is null ? "Starting" : $"Starting {FormatElapsed(DateTime.UtcNow - StartedAtUtc.Value)}",
        ServerRuntimeStatus.ReadyNoQuery => "Online",
        ServerRuntimeStatus.Online => "Online",
        ServerRuntimeStatus.Stopping => "Stopping",
        ServerRuntimeStatus.Crashed => "Crashed",
        _ => "Offline"
    };

    public Brush StatusBrush => Status switch
    {
        ServerRuntimeStatus.Starting => MainWindow.BrushFrom("#F2C94C"),
        ServerRuntimeStatus.ReadyNoQuery => MainWindow.BrushFrom("#6EE7B7"),
        ServerRuntimeStatus.Online => MainWindow.BrushFrom("#6EE7B7"),
        ServerRuntimeStatus.Stopping => MainWindow.BrushFrom("#F2994A"),
        ServerRuntimeStatus.Crashed => MainWindow.BrushFrom("#E25763"),
        _ => MainWindow.BrushFrom("#E25763")
    };

    public Brush CardBorderBrush => Status switch
    {
        ServerRuntimeStatus.Starting => MainWindow.BrushFrom("#55492E"),
        ServerRuntimeStatus.ReadyNoQuery => MainWindow.BrushFrom("#315448"),
        ServerRuntimeStatus.Online => MainWindow.BrushFrom("#315448"),
        ServerRuntimeStatus.Stopping => MainWindow.BrushFrom("#5B3D28"),
        ServerRuntimeStatus.Crashed => MainWindow.BrushFrom("#653438"),
        _ => MainWindow.BrushFrom("#30333B")
    };

    public string PrimaryActionText => IsProcessActive ? "Стоп" : "Старт";

    public string PrimaryActionTooltip => IsProcessActive
        ? "Остановить процесс этой карты"
        : "Запустить отдельный сервер этой карты";

    public Brush PrimaryActionBackground => IsProcessActive
        ? MainWindow.BrushFrom("#3B2424")
        : MainWindow.BrushFrom("#173E33");

    public Brush PrimaryActionBorder => IsProcessActive
        ? MainWindow.BrushFrom("#C65A52")
        : MainWindow.BrushFrom("#3AAE72");

    public bool CanRestart => IsProcessActive;

    public bool CanEdit => !IsProcessActive;

    public MapSettings ToSettings()
    {
        return new MapSettings
        {
            DisplayName = DisplayName,
            MapCode = MapCode,
            SessionName = SessionName,
            AltSaveDirectoryName = AltSaveDirectoryName,
            Port = Port,
            QueryPort = QueryPort,
            RconEnabled = RconEnabled,
            RconPort = RconPort,
            AdminPassword = AdminPassword,
            MaxPlayers = MaxPlayers,
            AccentColor = AccentColor,
            InitialsForeground = InitialsForegroundColor
        };
    }

    public void UpdateSettings(MapSettings settings)
    {
        DisplayName = settings.DisplayName.Trim();
        MapCode = settings.MapCode.Trim();
        SessionName = settings.SessionName.Trim();
        AltSaveDirectoryName = settings.AltSaveDirectoryName.Trim();
        Port = settings.Port;
        QueryPort = settings.QueryPort;
        RconEnabled = settings.RconEnabled;
        RconPort = settings.RconPort;
        AdminPassword = settings.AdminPassword?.Trim() ?? string.Empty;
        MaxPlayers = settings.MaxPlayers;
        AccentColor = settings.AccentColor;
        InitialsForegroundColor = settings.InitialsForeground;
        AccentBrush = MainWindow.BrushFrom(AccentColor);
        InitialsForeground = MainWindow.BrushFrom(InitialsForegroundColor);
        RaiseAll();
    }

    public void SetStarting(Process process, string launchCommand)
    {
        AttachedProcess = process;
        ProcessId = process.Id;
        StartedAtUtc = DateTime.UtcNow;
        PlayerCount = 0;
        QueryMaxPlayers = 0;
        QueryEndpoint = string.Empty;
        StatusProbeDetail = "process started";
        LastLaunchCommand = launchCommand;
        Status = ServerRuntimeStatus.Starting;
        RaiseRuntime();
    }

    public void AttachRunningProcess(Process process, string source)
    {
        AttachedProcess = process;
        ProcessId = process.Id;
        StartedAtUtc = DateTime.UtcNow;
        LastLaunchCommand = source;
        StatusProbeDetail = source;
        Status = ServerRuntimeStatus.Starting;
        RaiseRuntime();
    }

    public void SetOnline(int players, int maxPlayers, string endpoint)
    {
        if (IsProcessActive)
        {
            Status = ServerRuntimeStatus.Online;
            PlayerCount = Math.Max(0, players);
            QueryMaxPlayers = Math.Max(1, maxPlayers);
            QueryEndpoint = endpoint;
            StatusProbeDetail = endpoint;
            RaiseRuntime();
        }
    }

    public void UpdateStatusProbeDetail(string detail, bool preserveAuthoritativeReady = false)
    {
        if (preserveAuthoritativeReady && HasAuthoritativeReadySignal)
        {
            return;
        }

        StatusProbeDetail = detail;
        RaiseRuntime();
    }

    public void SetReadyNoQuery(
        string detail = "Server Ready",
        string? uptime = null,
        bool preserveAuthoritativeReady = false)
    {
        if (IsProcessActive && Status != ServerRuntimeStatus.Online)
        {
            var nextDetail = string.IsNullOrWhiteSpace(uptime) ? detail : $"{detail}, uptime {uptime}";
            Status = ServerRuntimeStatus.ReadyNoQuery;
            if (!preserveAuthoritativeReady || !HasAuthoritativeReadySignal)
            {
                StatusProbeDetail = nextDetail;
            }

            RaiseRuntime();
        }
    }

    private bool HasAuthoritativeReadySignal =>
        Status == ServerRuntimeStatus.ReadyNoQuery
        && (StatusProbeDetail.StartsWith("Server Ready", StringComparison.OrdinalIgnoreCase)
            || StatusProbeDetail.StartsWith("server console:", StringComparison.OrdinalIgnoreCase));

    public void SetOnlineOrStartingAfterFailedStop()
    {
        Status = PlayerCount > 0 || QueryMaxPlayers > 0
            ? ServerRuntimeStatus.Online
            : ServerRuntimeStatus.ReadyNoQuery;
        RaiseRuntime();
    }

    public void SetStopping()
    {
        Status = ServerRuntimeStatus.Stopping;
        RaiseRuntime();
    }

    public void SetOffline()
    {
        AttachedProcess = null;
        ProcessId = 0;
        StartedAtUtc = null;
        PlayerCount = 0;
        QueryMaxPlayers = 0;
        QueryEndpoint = string.Empty;
        StatusProbeDetail = "offline";
        Status = ServerRuntimeStatus.Offline;
        RaiseRuntime();
    }

    public void SetOfflineFromExit(bool expectedStop)
    {
        AttachedProcess = null;
        ProcessId = 0;
        StartedAtUtc = null;
        PlayerCount = 0;
        QueryMaxPlayers = 0;
        QueryEndpoint = string.Empty;
        StatusProbeDetail = expectedStop ? "offline" : "process exited";
        Status = expectedStop ? ServerRuntimeStatus.Offline : ServerRuntimeStatus.Crashed;
        RaiseRuntime();
    }

    public void SetCrashed()
    {
        AttachedProcess = null;
        ProcessId = 0;
        StartedAtUtc = null;
        PlayerCount = 0;
        QueryMaxPlayers = 0;
        QueryEndpoint = string.Empty;
        StatusProbeDetail = "launch failed";
        Status = ServerRuntimeStatus.Crashed;
        RaiseRuntime();
    }

    public void RefreshRuntimeDisplay()
    {
        RaiseRuntime();
    }

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(MapName));
        OnPropertyChanged(nameof(MapCode));
        OnPropertyChanged(nameof(SessionName));
        OnPropertyChanged(nameof(AltSaveDirectoryName));
        OnPropertyChanged(nameof(Port));
        OnPropertyChanged(nameof(QueryPort));
        OnPropertyChanged(nameof(RconEnabled));
        OnPropertyChanged(nameof(RconPort));
        OnPropertyChanged(nameof(AdminPassword));
        OnPropertyChanged(nameof(MaxPlayers));
        OnPropertyChanged(nameof(AccentColor));
        OnPropertyChanged(nameof(AccentBrush));
        OnPropertyChanged(nameof(InitialsForegroundColor));
        OnPropertyChanged(nameof(InitialsForeground));
        OnPropertyChanged(nameof(Initials));
        OnPropertyChanged(nameof(Ports));
        RaiseRuntime();
    }

    private void RaiseRuntime()
    {
        OnPropertyChanged(nameof(AttachedProcess));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(ProcessId));
        OnPropertyChanged(nameof(StartedAtUtc));
        OnPropertyChanged(nameof(PlayerCount));
        OnPropertyChanged(nameof(QueryMaxPlayers));
        OnPropertyChanged(nameof(QueryEndpoint));
        OnPropertyChanged(nameof(StatusProbeDetail));
        OnPropertyChanged(nameof(LastLaunchCommand));
        OnPropertyChanged(nameof(RuntimeDetails));
        OnPropertyChanged(nameof(OnlineText));
        OnPropertyChanged(nameof(IsProcessActive));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusBrush));
        OnPropertyChanged(nameof(CardBorderBrush));
        OnPropertyChanged(nameof(PrimaryActionText));
        OnPropertyChanged(nameof(PrimaryActionTooltip));
        OnPropertyChanged(nameof(PrimaryActionBackground));
        OnPropertyChanged(nameof(PrimaryActionBorder));
        OnPropertyChanged(nameof(CanRestart));
        OnPropertyChanged(nameof(CanEdit));
    }

    private string BuildQueryDetail()
    {
        if (Status == ServerRuntimeStatus.Online && !string.IsNullOrWhiteSpace(QueryEndpoint))
        {
            return $" - source {QueryEndpoint}";
        }

        return Status is ServerRuntimeStatus.Starting or ServerRuntimeStatus.ReadyNoQuery
            ? $" - {StatusProbeDetail}"
            : string.Empty;
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed.TotalHours >= 1)
        {
            return $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
        }

        return $"{elapsed.Minutes:00}:{elapsed.Seconds:00}";
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public enum ServerRuntimeStatus
{
    Offline,
    Starting,
    ReadyNoQuery,
    Online,
    Stopping,
    Crashed
}

public sealed record MapPreset(string DisplayName, string MapCode, string AccentColor);
