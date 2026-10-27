using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Management;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ProxyManager.Models;
using ProxyManager.Services;

namespace ProxyManager.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private const int MaxLogLines = 3000;

    // Runtime-only properties that must not trigger a save.
    private static readonly HashSet<string> TransientProperties =
    [
        nameof(ProxyServer.CheckState), nameof(ProxyServer.CheckResult),
    ];

    private readonly SettingsStore _store;
    private readonly SingBoxService _core;
    private readonly DispatcherTimer _saveTimer;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

    private readonly DispatcherTimer _reconcileTimer;
    private readonly SemaphoreSlim _routingGate = new(1, 1);

    private ProcessRouter? _router;
    private ClashApiClient? _clash;
    private string? _appliedProxies;
    private CoreState _state;
    private bool _hasPendingChanges;
    private string _errorText = "";
    private ProxyServer? _selectedProxy;
    private string _importText = "";
    private string _importStatus = "";
    private ProxyType _importType = ProxyType.Socks5;
    private bool _connectOnStartup;
    private bool _startWithWindows;

    public MainViewModel(SettingsStore store)
    {
        _store = store;
        _core = new SingBoxService(store.Directory);
        _core.StateChanged += s => _dispatcher.BeginInvoke(() => OnCoreStateChanged(s));
        _core.LogReceived += line => _dispatcher.BeginInvoke(() => AppendLog(line));

        var settings = store.Load();
        Proxies = new ObservableCollection<ProxyServer>(settings.Proxies);
        Rules = new ObservableCollection<AppRule>(settings.Rules);
        foreach (var item in Proxies.Cast<INotifyPropertyChanged>().Concat(Rules))
            item.PropertyChanged += OnItemChanged;
        Proxies.CollectionChanged += OnCollectionChanged;
        Rules.CollectionChanged += OnCollectionChanged;
        SelectedProxy = Proxies.FirstOrDefault();
        _connectOnStartup = settings.ConnectOnStartup;
        // Task Scheduler is the source of truth: the task may have been removed outside the app.
        _startWithWindows = Autostart.IsEnabled();

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _saveTimer.Tick += (_, _) => SaveNow();
        _reconcileTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _reconcileTimer.Tick += async (_, _) =>
        {
            _reconcileTimer.Stop();
            await ReconcileConnectionsAsync();
        };

        ToggleCoreCommand = new AsyncRelayCommand(ToggleCoreAsync, () => State != CoreState.Starting);
        ApplyCommand = new AsyncRelayCommand(StartAsync, () => State == CoreState.Running);
        DismissErrorCommand = new RelayCommand(() => ErrorText = "");
        AddProxyCommand = new RelayCommand(AddProxy);
        RemoveProxyCommand = new RelayCommand(RemoveSelectedProxy, () => SelectedProxy is not null);
        CheckProxyCommand = new AsyncRelayCommand(p => CheckAsync((ProxyServer)p!), p => p is ProxyServer { IsValid: true });
        CheckAllCommand = new AsyncRelayCommand(() => Task.WhenAll(Proxies.Where(p => p.IsValid).Select(CheckAsync)), () => Proxies.Count > 0);
        ImportCommand = new RelayCommand(Import, () => !string.IsNullOrWhiteSpace(ImportText));
        RemoveRuleCommand = new RelayCommand(p => Rules.Remove((AppRule)p!));
        ClearLogCommand = new RelayCommand(Log.Clear);
        CopyLogCommand = new RelayCommand(() => Clipboard.SetText(string.Join(Environment.NewLine, Log)), () => Log.Count > 0);
        OpenDataFolderCommand = new RelayCommand(() => OpenInShell(_store.Directory));
        OpenLicensesCommand = new RelayCommand(() => OpenInShell(AppInfo.LicensesDirectory));
    }

    public ObservableCollection<ProxyServer> Proxies { get; }
    public ObservableCollection<AppRule> Rules { get; }
    public ObservableCollection<string> Log { get; } = [];

    public static ProxyType[] ProxyTypes { get; } = Enum.GetValues<ProxyType>();

    public ICommand ToggleCoreCommand { get; }
    public ICommand ApplyCommand { get; }
    public ICommand DismissErrorCommand { get; }
    public ICommand AddProxyCommand { get; }
    public ICommand RemoveProxyCommand { get; }
    public ICommand CheckProxyCommand { get; }
    public ICommand CheckAllCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand RemoveRuleCommand { get; }
    public ICommand ClearLogCommand { get; }
    public ICommand CopyLogCommand { get; }
    public ICommand OpenDataFolderCommand { get; }
    public ICommand OpenLicensesCommand { get; }

    public string VersionText => $"Proxy Manager {AppInfo.Version} · ядро sing-box {AppInfo.CoreVersion}";

    public bool ConnectOnStartup
    {
        get => _connectOnStartup;
        set
        {
            if (Set(ref _connectOnStartup, value))
                ScheduleSave();
        }
    }

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (_startWithWindows == value) return;
            try
            {
                if (value) Autostart.Enable();
                else Autostart.Disable();
                _startWithWindows = value;
            }
            catch (InvalidOperationException e)
            {
                ErrorText = e.Message;
            }
            // Also re-syncs the checkbox when the change failed.
            OnPropertyChanged();
        }
    }

    public CoreState State
    {
        get => _state;
        private set
        {
            if (!Set(ref _state, value)) return;
            OnPropertyChanged(nameof(IsActive));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(ToggleText));
            UpdatePendingChanges();
        }
    }

    /// <summary>True while the core is starting or running.</summary>
    public bool IsActive => State is CoreState.Starting or CoreState.Running;

    public string ToggleText => IsActive ? "Остановить" : "Запустить";

    public string StatusText => State switch
    {
        CoreState.Starting => "Запуск…",
        CoreState.Running => $"Работает · программ через прокси: {ActiveRuleCount}",
        CoreState.Failed => "Ошибка",
        _ => "Остановлено",
    };

    private int ActiveRuleCount => SingBoxConfigBuilder.Resolve(Proxies, Rules).Count;

    /// <summary>The settings differ from the config the running core was started with.</summary>
    public bool HasPendingChanges
    {
        get => _hasPendingChanges;
        private set => Set(ref _hasPendingChanges, value);
    }

    public string ErrorText
    {
        get => _errorText;
        private set => Set(ref _errorText, value);
    }

    public ProxyServer? SelectedProxy
    {
        get => _selectedProxy;
        set => Set(ref _selectedProxy, value);
    }

    public string ImportText
    {
        get => _importText;
        set => Set(ref _importText, value);
    }

    public string ImportStatus
    {
        get => _importStatus;
        private set => Set(ref _importStatus, value);
    }

    public ProxyType ImportType
    {
        get => _importType;
        set => Set(ref _importType, value);
    }

    // ---- Core ----

    private Task ToggleCoreAsync() => IsActive ? StopAsync() : StartAsync();

    /// <summary>Called once when the app starts.</summary>
    public async Task OnAppStartedAsync()
    {
        // Point an existing autostart task at the current exe (the app may have been moved or updated).
        if (StartWithWindows)
        {
            try
            {
                Autostart.Enable();
            }
            catch (InvalidOperationException e)
            {
                AppendLog(e.Message);
            }
        }

        if (ConnectOnStartup && ActiveRuleCount > 0)
            await StartAsync();
    }

    private async Task StartAsync()
    {
        ErrorText = "";
        if (!SingBoxService.CoreExists)
        {
            ErrorText = $"Не найдено ядро sing-box ({SingBoxService.CorePath}). Запустите scripts\\fetch-singbox.ps1.";
            return;
        }
        var proxies = Proxies.Where(p => p.IsValid).ToList();
        if (proxies.Count == 0)
        {
            ErrorText = "Добавьте хотя бы один прокси на вкладке «Прокси».";
            return;
        }

        SaveNow();
        AppendLog("— запуск sing-box —");
        await StopCoreAndRoutingAsync();
        var controller = ClashApiClient.Allocate();
        try
        {
            // The rule-set files must exist before sing-box reads its config.
            var router = new ProcessRouter(_store.Directory, proxies);
            router.Log += line => _dispatcher.BeginInvoke(() => AppendLog(line));
            router.Changed += () => _dispatcher.BeginInvoke(ScheduleReconcile);
            _router = router;
            var rules = SingBoxConfigBuilder.Resolve(Proxies, Rules);
            await Task.Run(() =>
            {
                router.Update(rules);
                router.StartWatching();
            });

            await _core.StartAsync(SingBoxConfigBuilder.Build(proxies, _store.Directory, controller));
            _clash = new ClashApiClient(controller);
            _appliedProxies = ProxySignature();
            await ResetConnectionsOutsideTunAsync();
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or Win32Exception or ManagementException)
        {
            StopRouting();
            AppendLog(e.Message);
            ErrorText = e.Message;
            State = CoreState.Failed;
        }
        OnPropertyChanged(nameof(StatusText));
        UpdatePendingChanges();
    }

    private async Task StopAsync()
    {
        await StopCoreAndRoutingAsync();
        AppendLog("— sing-box остановлен —");
    }

    private async Task StopCoreAndRoutingAsync()
    {
        // Core first: it reads the rule-set files the router owns.
        await _core.StopAsync();
        StopRouting();
    }

    private void StopRouting()
    {
        _reconcileTimer.Stop();
        _router?.Dispose();
        _router = null;
        _clash?.Dispose();
        _clash = null;
        _appliedProxies = null;
    }

    private void OnCoreStateChanged(CoreState state)
    {
        // Startup failures are reported by StartAsync; this catches a crash while running.
        if (state == CoreState.Failed && State == CoreState.Running)
        {
            ErrorText = "sing-box неожиданно остановился. Подробности на вкладке «Журнал».";
            StopRouting();
        }
        State = state;
    }

    /// <summary>Pushes rule changes to the running core without restarting it.</summary>
    private async Task UpdateRoutingAsync()
    {
        if (_router is not { } router) return;
        var rules = SingBoxConfigBuilder.Resolve(Proxies, Rules);
        // Serialized so an older rule set can't overwrite a newer one.
        await _routingGate.WaitAsync();
        try
        {
            await Task.Run(() => router.Update(rules));
            await ResetConnectionsOutsideTunAsync();
        }
        catch (ManagementException e)
        {
            AppendLog("Не удалось обновить список процессов: " + e.Message);
        }
        finally
        {
            _routingGate.Release();
        }
    }

    /// <summary>Programs started before the proxy keep connections that bypass the TUN; reset them.</summary>
    private async Task ResetConnectionsOutsideTunAsync()
    {
        if (_router is not { } router) return;
        var pids = router.TrackedPids;
        var reset = await Task.Run(() => TcpConnections.ResetOutsideTun(pids));
        if (reset > 0)
            AppendLog($"Переподключено соединений, открытых до включения прокси: {reset}");
    }

    private void ScheduleReconcile()
    {
        // Give sing-box a moment to reload the rule-set file before re-checking connections.
        _reconcileTimer.Stop();
        _reconcileTimer.Start();
    }

    /// <summary>Closes connections whose route no longer matches the rules; the programs reconnect.</summary>
    private async Task ReconcileConnectionsAsync()
    {
        if (_clash is not { } clash || _router is not { } router) return;
        try
        {
            var closed = 0;
            foreach (var connection in await clash.GetConnectionsAsync())
            {
                if (connection.Network != "tcp" || connection.ProcessPath.Length == 0) continue;
                var expected = router.ExpectedProxyId(connection.ProcessPath) is { } id
                    ? SingBoxConfigBuilder.OutboundTag(Proxies.First(p => p.Id == id))
                    : "direct";
                if (connection.Chains.Contains(expected)) continue;

                await clash.CloseAsync(connection.Id);
                closed++;
            }
            if (closed > 0)
                AppendLog($"Переподключено соединений после изменения правил: {closed}");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            AppendLog("Не удалось обновить активные соединения: " + e.Message);
        }
    }

    /// <summary>The part of the config that requires a core restart: the proxies themselves.</summary>
    private string ProxySignature() => SingBoxConfigBuilder.Build(Proxies, _store.Directory, controller: null);

    private void UpdatePendingChanges() =>
        HasPendingChanges = State == CoreState.Running && ProxySignature() != _appliedProxies;

    private void AppendLog(string line)
    {
        Log.Add(line);
        while (Log.Count > MaxLogLines)
            Log.RemoveAt(0);
    }

    // ---- Proxies ----

    private void AddProxy()
    {
        var proxy = new ProxyServer();
        Proxies.Add(proxy);
        SelectedProxy = proxy;
    }

    private void RemoveSelectedProxy()
    {
        if (SelectedProxy is not { } proxy) return;

        var users = Rules.Where(r => r.ProxyId == proxy.Id).ToList();
        if (users.Count > 0)
        {
            var answer = MessageBox.Show(
                $"Прокси «{proxy.DisplayName}» используют программ: {users.Count}. Удалить? Эти программы будут работать напрямую.",
                "Удаление прокси", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;
        }

        foreach (var rule in users)
            rule.ProxyId = null;
        var index = Proxies.IndexOf(proxy);
        Proxies.Remove(proxy);
        SelectedProxy = Proxies.Count > 0 ? Proxies[Math.Min(index, Proxies.Count - 1)] : null;
    }

    private static async Task CheckAsync(ProxyServer proxy)
    {
        proxy.CheckState = CheckState.Checking;
        proxy.CheckResult = "проверка…";
        var result = await ProxyTester.CheckAsync(proxy);
        proxy.CheckState = result.Ok ? CheckState.Ok : CheckState.Failed;
        proxy.CheckResult = result.Ok
            ? $"IP {result.Ip} · {result.Elapsed.TotalMilliseconds:0} мс"
            : result.Error ?? "ошибка";
    }

    private void Import()
    {
        var (parsed, failed) = ProxyParser.ParseMany(ImportText, ImportType);
        foreach (var proxy in parsed)
            Proxies.Add(proxy);
        if (parsed.Count > 0)
            SelectedProxy = parsed[0];

        // Leave only the lines that need fixing.
        ImportText = string.Join(Environment.NewLine, failed);
        ImportStatus = failed.Count == 0
            ? $"Добавлено: {parsed.Count}"
            : $"Добавлено: {parsed.Count}. Не распознано строк: {failed.Count} — они остались в поле.";
    }

    // ---- Rules ----

    public void AddRules(IEnumerable<string> exePaths)
    {
        var defaultProxy = SelectedProxy ?? Proxies.FirstOrDefault();
        foreach (var path in exePaths)
        {
            if (Rules.Any(r => string.Equals(r.ExePath, path, StringComparison.OrdinalIgnoreCase)))
                continue;
            Rules.Add(new AppRule { ExePath = path, ProxyId = defaultProxy?.Id });
        }
    }

    // ---- Persistence ----

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (INotifyPropertyChanged item in e.OldItems ?? Array.Empty<object>())
            item.PropertyChanged -= OnItemChanged;
        foreach (INotifyPropertyChanged item in e.NewItems ?? Array.Empty<object>())
            item.PropertyChanged += OnItemChanged;
        ScheduleSave();
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is { } name && TransientProperties.Contains(name)) return;
        ScheduleSave();
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveNow()
    {
        _saveTimer.Stop();
        _store.Save(new AppSettings { Proxies = [.. Proxies], Rules = [.. Rules], ConnectOnStartup = ConnectOnStartup });
        OnPropertyChanged(nameof(StatusText));
        UpdatePendingChanges();
        if (State == CoreState.Running)
            _ = UpdateRoutingAsync();
    }

    private static void OpenInShell(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
    }

    public void Dispose()
    {
        if (_saveTimer.IsEnabled)
            SaveNow();
        _core.Dispose();
        StopRouting();
    }
}
