using System.ComponentModel;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Windows;
using System.Windows.Threading;
using ProxyManager.Services;
using ProxyManager.ViewModels;
using Forms = System.Windows.Forms;

namespace ProxyManager;

public partial class App : Application
{
    // Signalled by a second launch to bring the running instance's window up.
    private const string ShowEventName = @"Local\ProxyManager.Show";

    private Mutex? _singleInstance;
    private EventWaitHandle? _showEvent;
    private Forms.NotifyIcon? _tray;
    private Forms.ToolStripMenuItem? _toggleMenuItem;
    private MainViewModel? _viewModel;
    private MainWindow? _window;
    private bool _trayHintShown;
    private bool _exiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        RegisterCrashHandlers();

        // A running instance (even an elevated one) can be woken without a UAC prompt.
        if (SignalRunningInstance())
        {
            Shutdown();
            return;
        }

        if (!IsAdministrator())
        {
            RelaunchElevated(e.Args);
            Shutdown();
            return;
        }

        // Two instances would fight over the same TUN adapter.
        _singleInstance = new Mutex(true, @"Global\ProxyManager.SingleInstance", out var created);
        if (!created)
        {
            SignalRunningInstance();
            Shutdown();
            return;
        }
        ListenForShowRequests();

        _viewModel = new MainViewModel(new SettingsStore(SettingsStore.DefaultDirectory));
        _viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.StatusText))
                UpdateTray();
        };
        _window = new MainWindow { DataContext = _viewModel };
        _window.Closing += (_, args) =>
        {
            if (_exiting) return;
            // Closing the window keeps the proxy running in the tray; "Exit" is in the tray menu.
            args.Cancel = true;
            _window.Hide();
            if (!_trayHintShown)
            {
                _tray?.ShowBalloonTip(3000, "Proxy Manager", "Программа продолжает работать в области уведомлений.", Forms.ToolTipIcon.Info);
                _trayHintShown = true;
            }
        };

        _tray = CreateTrayIcon();
        UpdateTray();

        // Started by Task Scheduler at logon: stay in the tray.
        if (!e.Args.Contains(Autostart.Argument))
            _window.Show();

        await _viewModel.OnAppStartedAsync();
    }

    private void RegisterCrashHandlers()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            CrashLog.Write(e.Exception, "UI");
            MessageBox.Show($"Непредвиденная ошибка: {e.Exception.Message}\n\nПодробности записаны в {CrashLog.FilePath}",
                "Proxy Manager", MessageBoxButton.OK, MessageBoxImage.Error);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception exception)
                CrashLog.Write(exception, "AppDomain");
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLog.Write(e.Exception, "Task");
            e.SetObserved();
        };
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void RelaunchElevated(string[] args)
    {
        var startInfo = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = true,
            Verb = "runas",
        };
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        try
        {
            Process.Start(startInfo)?.Dispose();
        }
        catch (Win32Exception)
        {
            // The UAC prompt was declined.
            MessageBox.Show("Proxy Manager нужны права администратора, чтобы создать сетевой адаптер для прокси.",
                "Proxy Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static bool SignalRunningInstance()
    {
        if (!EventWaitHandleAcl.TryOpenExisting(ShowEventName, EventWaitHandleRights.Modify, out var showEvent))
            return false;
        using (showEvent)
            showEvent.Set();
        return true;
    }

    private void ListenForShowRequests()
    {
        // The event is created elevated; grant the (non-elevated) current user the right to signal it.
        using var identity = WindowsIdentity.GetCurrent();
        var security = new EventWaitHandleSecurity();
        security.AddAccessRule(new EventWaitHandleAccessRule(identity.User!,
            EventWaitHandleRights.Synchronize | EventWaitHandleRights.Modify, AccessControlType.Allow));
        _showEvent = EventWaitHandleAcl.Create(false, EventResetMode.AutoReset, ShowEventName, out _, security);

        var thread = new Thread(() =>
        {
            while (_showEvent.WaitOne())
                Dispatcher.BeginInvoke(ShowWindow);
        })
        {
            IsBackground = true,
            Name = "ShowRequests",
        };
        thread.Start();
    }

    private Forms.NotifyIcon CreateTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Открыть", null, (_, _) => ShowWindow());
        _toggleMenuItem = new Forms.ToolStripMenuItem("", null, (_, _) => _viewModel?.ToggleCoreCommand.Execute(null));
        menu.Items.Add(_toggleMenuItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => ExitApp());

        using var iconStream = GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"))!.Stream;
        var tray = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(iconStream, Forms.SystemInformation.SmallIconSize),
            ContextMenuStrip = menu,
            Visible = true,
        };
        tray.DoubleClick += (_, _) => ShowWindow();
        return tray;
    }

    private void UpdateTray()
    {
        if (_tray is null || _viewModel is null) return;
        var text = "Proxy Manager — " + _viewModel.StatusText;
        // NotifyIcon.Text is limited to 63 characters.
        _tray.Text = text.Length > 63 ? text[..63] : text;
        _toggleMenuItem!.Text = _viewModel.IsActive ? "Отключить прокси" : "Включить прокси";
    }

    private void ShowWindow()
    {
        if (_window is null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void ExitApp()
    {
        _exiting = true;
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        _viewModel?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // _showEvent is left to process teardown: the listener thread is blocked on it.
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
