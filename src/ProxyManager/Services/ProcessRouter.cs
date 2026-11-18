using System.Management;
using ProxyManager.Models;

namespace ProxyManager.Services;

public sealed record ProcessEntry(int Pid, int ParentPid, string Name);

/// <summary>
/// Keeps the per-proxy rule-set files (which sing-box hot-reloads) in sync with the rules and with
/// the processes those programs launch. Programs are listed by name; their descendants (when the
/// rule includes children) by full path while they are running.
/// </summary>
/// <remarks>
/// sing-box can only match a path, not a process, so a descendant's path is listed only while every
/// running copy of that exe is a tracked descendant. Otherwise e.g. a Chrome started by Playwright
/// from VS Code would put the user's own Chrome behind the proxy too.
/// </remarks>
public sealed class ProcessRouter : IDisposable
{
    // Guards the ancestor walk against parent-PID cycles caused by PID reuse.
    private const int MaxDepth = 64;

    // Programs that are the user's own apps even when started from a tracked tree (a link clicked
    // in VS Code opens the browser as its child). Listing them would route the whole app and block
    // its UDP. They can still be routed with a rule of their own.
    private static readonly HashSet<string> StandaloneApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome.exe", "msedge.exe", "firefox.exe", "opera.exe", "brave.exe", "vivaldi.exe", "browser.exe",
        "iexplore.exe", "Discord.exe", "Telegram.exe", "slack.exe", "Teams.exe", "ms-teams.exe",
        "Zoom.exe", "Skype.exe", "WhatsApp.exe", "Spotify.exe", "steam.exe", "explorer.exe",
    };

    private static readonly string WindowsDirectory =
        Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";

    private readonly string _directory;
    private readonly IReadOnlyList<ProxyServer> _proxies;
    private readonly Func<IReadOnlyList<ProcessEntry>> _snapshot;
    private readonly Func<int, string?> _getPath;
    private readonly HashSet<string> _ownPaths;

    private readonly object _lock = new();
    private readonly Dictionary<int, Tracked> _tracked = [];
    // Running processes outside the tracked trees: pid -> exe path.
    private readonly Dictionary<int, string> _others = [];
    private readonly Dictionary<string, string> _written = [];
    private readonly Dictionary<string, HashSet<string>> _childPaths = [];
    private readonly HashSet<string> _sharedReported = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, ResolvedRule> _rules = new(StringComparer.OrdinalIgnoreCase);
    private ManagementEventWatcher? _startWatcher;
    private ManagementEventWatcher? _stopWatcher;
    private bool _disposed;

    /// <param name="proxies">Proxies present in the running config, in config order.</param>
    public ProcessRouter(string directory, IEnumerable<ProxyServer> proxies,
        Func<IReadOnlyList<ProcessEntry>>? snapshot = null, Func<int, string?>? getPath = null)
    {
        _directory = directory;
        _proxies = proxies.ToList();
        _snapshot = snapshot ?? QueryProcesses;
        _getPath = getPath ?? ProcessPaths.TryGet;
        _ownPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Environment.ProcessPath!, SingBoxService.CorePath };
        Directory.CreateDirectory(directory);
    }

    /// <summary>Raised on a background thread.</summary>
    public event Action<string>? Log;

    /// <summary>Raised on a background thread after rule-set files were rewritten.</summary>
    public event Action? Changed;

    /// <summary>PIDs of running programs from the rules and their tracked descendants.</summary>
    public IReadOnlySet<int> TrackedPids
    {
        get
        {
            lock (_lock)
                return _tracked.Keys.ToHashSet();
        }
    }

    /// <summary>Applies new rules: rescans running processes and rewrites the rule-set files.</summary>
    public void Update(IReadOnlyList<ResolvedRule> rules)
    {
        var known = _proxies.Select(p => p.Id).ToHashSet();
        var processes = _snapshot();
        bool changed;
        lock (_lock)
        {
            if (_disposed) return;
            // Rules for proxies added after the core started wait for a restart.
            _rules = rules.Where(r => known.Contains(r.Proxy.Id))
                .ToDictionary(r => r.ProcessName, StringComparer.OrdinalIgnoreCase);
            Rebuild(processes);
            changed = Flush();
        }
        if (changed) Changed?.Invoke();
    }

    /// <summary>Starts following process creation and exit.</summary>
    public void StartWatching()
    {
        _startWatcher = new ManagementEventWatcher(StartTraceQuery());
        _startWatcher.EventArrived += (_, e) => Guard(() =>
            HandleStart(Convert.ToInt32(e.NewEvent["ProcessID"]), Convert.ToInt32(e.NewEvent["ParentProcessID"])));
        _stopWatcher = new ManagementEventWatcher(StopTraceQuery());
        _stopWatcher.EventArrived += (_, e) => Guard(() => HandleStop(Convert.ToInt32(e.NewEvent["ProcessID"])));
        _startWatcher.Start();
        _stopWatcher.Start();
    }

    // WqlEventQuery's parser rejects property lists ("SELECT ProcessID, ... FROM") with
    // "Invalid parameter", so select everything.
    internal static WqlEventQuery StartTraceQuery() => new("SELECT * FROM Win32_ProcessStartTrace");

    internal static WqlEventQuery StopTraceQuery() => new("SELECT * FROM Win32_ProcessStopTrace");

    /// <summary>Proxy that sing-box will pick for a process, mirroring the route rule order; null = direct.</summary>
    public string? ExpectedProxyId(string processPath)
    {
        var name = Path.GetFileName(processPath);
        lock (_lock)
        {
            foreach (var proxy in _proxies)
            {
                if (_rules.TryGetValue(name, out var rule) && rule.Proxy.Id == proxy.Id)
                    return proxy.Id;
                if (_childPaths.TryGetValue(proxy.Id, out var paths) && paths.Contains(processPath))
                    return proxy.Id;
            }
            return null;
        }
    }

    internal void HandleStart(int pid, int parentPid)
    {
        bool changed;
        lock (_lock)
        {
            if (_disposed || _rules.Count == 0) return;
            var parent = _tracked.GetValueOrDefault(parentPid) is { Propagates: true } p ? p : null;

            // Event process names are truncated to 15 characters, so look up the real path.
            var path = _getPath(pid);
            if (path is null) return;
            if (_rules.TryGetValue(Path.GetFileName(path), out var rule))
            {
                // A program from the rules: matched by name, nothing to write.
                _tracked[pid] = new Tracked(rule.Proxy.Id, null, rule.IncludeChildren);
                return;
            }
            if (parent is null || _ownPaths.Contains(path))
            {
                _others[pid] = path;
                // A separate copy of a listed child exe: the path has to come off the list.
                if (!IsListedChildPath(path)) return;
            }
            else
            {
                _tracked[pid] = new Tracked(parent.ProxyId, path, true);
            }
            changed = Flush();
        }
        if (changed) Changed?.Invoke();
    }

    internal void HandleStop(int pid)
    {
        bool changed;
        lock (_lock)
        {
            if (_disposed) return;
            if (_tracked.Remove(pid, out var tracked))
            {
                if (tracked.Path is null) return;
            }
            else if (!_others.Remove(pid, out var path) || !IsTrackedChildPath(path))
            {
                return;
            }
            // Either a listed child exited, or the last outside copy of a child exe did.
            changed = Flush();
        }
        if (changed) Changed?.Invoke();
    }

    /// <summary>
    /// Children that are tracked (their own children are still routed) but never listed: system
    /// tools from the Windows folder (powershell, cmd, ping, conhost — shared by everything) and
    /// standalone apps such as browsers.
    /// </summary>
    internal static bool IsNeverInherited(string path) =>
        path.StartsWith(WindowsDirectory, StringComparison.OrdinalIgnoreCase)
        || StandaloneApps.Contains(Path.GetFileName(path));

    private bool IsListedChildPath(string path) => _childPaths.Values.Any(paths => paths.Contains(path));

    private bool IsTrackedChildPath(string path) =>
        _tracked.Values.Any(t => string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase));

    private void Rebuild(IReadOnlyList<ProcessEntry> processes)
    {
        _tracked.Clear();
        _others.Clear();
        var byPid = new Dictionary<int, ProcessEntry>();
        foreach (var p in processes)
            byPid.TryAdd(p.Pid, p);

        var memo = new Dictionary<int, Tracked?>();
        foreach (var p in processes)
            Classify(p, byPid, memo, 0);

        // Paths of everything else, to spot exes shared with the tracked trees.
        foreach (var p in processes)
        {
            if (!_tracked.ContainsKey(p.Pid) && _getPath(p.Pid) is { } path)
                _others[p.Pid] = path;
        }
    }

    private Tracked? Classify(ProcessEntry process, Dictionary<int, ProcessEntry> byPid, Dictionary<int, Tracked?> memo, int depth)
    {
        if (memo.TryGetValue(process.Pid, out var known)) return known;
        memo[process.Pid] = null;

        Tracked? result = null;
        if (_rules.TryGetValue(process.Name, out var rule))
        {
            result = new Tracked(rule.Proxy.Id, null, rule.IncludeChildren);
        }
        else if (depth < MaxDepth && process.ParentPid != process.Pid
                 && byPid.TryGetValue(process.ParentPid, out var parent)
                 && Classify(parent, byPid, memo, depth + 1) is { Propagates: true } owner
                 && _getPath(process.Pid) is { } path && !_ownPaths.Contains(path))
        {
            result = new Tracked(owner.ProxyId, path, true);
        }

        memo[process.Pid] = result;
        if (result is not null)
            _tracked[process.Pid] = result;
        return result;
    }

    /// <summary>Rewrites the rule-set files whose content changed. Must hold the lock.</summary>
    private bool Flush()
    {
        var shared = new HashSet<string>(_others.Values, StringComparer.OrdinalIgnoreCase);
        var changed = false;
        foreach (var proxy in _proxies)
        {
            var names = _rules.Values.Where(r => r.Proxy.Id == proxy.Id).Select(r => r.ProcessName).ToList();
            // Children that are themselves listed by name are covered by that rule.
            var candidates = _tracked.Values
                .Where(t => t.ProxyId == proxy.Id && t.Path is not null && !_rules.ContainsKey(Path.GetFileName(t.Path))
                            && !IsNeverInherited(t.Path))
                .Select(t => t.Path!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var paths = candidates.Where(p => !shared.Contains(p)).ToList();
            ReportShared(proxy, candidates.Where(shared.Contains));

            var content = SingBoxConfigBuilder.BuildRuleSet(names, paths);
            if (_written.TryGetValue(proxy.Id, out var previous) && previous == content) continue;

            try
            {
                // Written in place: sing-box watches the file and reloads it on change.
                File.WriteAllText(SingBoxConfigBuilder.RuleSetPath(_directory, proxy), content);
            }
            catch (IOException e)
            {
                Log?.Invoke($"Не удалось обновить список программ для «{proxy.DisplayName}»: {e.Message}");
                continue;
            }
            _written[proxy.Id] = content;
            changed = true;
            LogNewChildren(proxy, paths);
        }
        return changed;
    }

    private void LogNewChildren(ProxyServer proxy, List<string> paths)
    {
        var current = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        var previous = _childPaths.GetValueOrDefault(proxy.Id) ?? [];
        foreach (var path in current.Where(p => !previous.Contains(p)))
            Log?.Invoke($"Дочерний процесс {Path.GetFileName(path)} → «{proxy.DisplayName}»");
        _childPaths[proxy.Id] = current;
    }

    private void ReportShared(ProxyServer proxy, IEnumerable<string> paths)
    {
        foreach (var path in paths.Where(_sharedReported.Add))
        {
            Log?.Invoke($"Дочерний процесс {Path.GetFileName(path)} идёт напрямую, а не через «{proxy.DisplayName}»: " +
                        "этот же exe запущен и отдельно, и через прокси пошли бы все его копии");
        }
    }

    private void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            // Runs on a WMI thread: an exception here would take the whole app down.
            Log?.Invoke("Ошибка отслеживания процессов: " + e.Message);
        }
    }

    private static List<ProcessEntry> QueryProcesses()
    {
        using var searcher = new ManagementObjectSearcher("SELECT ProcessId, ParentProcessId, Name FROM Win32_Process");
        using var results = searcher.Get();
        var processes = new List<ProcessEntry>();
        foreach (var item in results)
        {
            using (item)
            {
                processes.Add(new ProcessEntry(
                    Convert.ToInt32(item["ProcessId"]),
                    Convert.ToInt32(item["ParentProcessId"]),
                    (string)item["Name"]));
            }
        }
        return processes;
    }

    public void Dispose()
    {
        _startWatcher?.Stop();
        _startWatcher?.Dispose();
        _stopWatcher?.Stop();
        _stopWatcher?.Dispose();

        lock (_lock)
        {
            _disposed = true;
            foreach (var proxy in _proxies)
            {
                try
                {
                    File.Delete(SingBoxConfigBuilder.RuleSetPath(_directory, proxy));
                }
                catch (IOException)
                {
                }
            }
        }
    }

    /// <summary>Path is null for programs matched by name (the rule roots).</summary>
    private sealed record Tracked(string ProxyId, string? Path, bool Propagates);
}
