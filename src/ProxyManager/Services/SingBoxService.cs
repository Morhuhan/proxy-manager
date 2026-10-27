using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace ProxyManager.Services;

public enum CoreState
{
    Stopped,
    Starting,
    Running,
    Failed,
}

/// <summary>Runs the sing-box core as a child process and forwards its log.</summary>
public sealed partial class SingBoxService(string workDirectory) : IDisposable
{
    // sing-box needs a moment to bring the TUN adapter up; if it dies within this window, startup failed.
    private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(2);

    private readonly JobObject _job = new();
    private Process? _process;
    private CoreState _state;

    public static string CorePath { get; } = Path.Combine(AppContext.BaseDirectory, "core", "sing-box.exe");

    public static bool CoreExists => File.Exists(CorePath);

    private string ConfigPath => Path.Combine(workDirectory, "sing-box.json");

    public CoreState State
    {
        get => _state;
        private set
        {
            if (_state == value) return;
            _state = value;
            StateChanged?.Invoke(value);
        }
    }

    /// <summary>Raised on a background thread.</summary>
    public event Action<CoreState>? StateChanged;

    /// <summary>Raised on a background thread.</summary>
    public event Action<string>? LogReceived;

    public async Task StartAsync(string config)
    {
        await StopAsync();
        if (!CoreExists)
            throw new FileNotFoundException($"Не найдено ядро sing-box: {CorePath}");

        KillOrphans();
        Directory.CreateDirectory(workDirectory);
        await File.WriteAllTextAsync(ConfigPath, config);

        State = CoreState.Starting;
        var process = new Process
        {
            StartInfo = new ProcessStartInfo(CorePath)
            {
                ArgumentList = { "run", "-c", ConfigPath, "-D", workDirectory, "--disable-color" },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            },
            EnableRaisingEvents = true,
        };
        process.OutputDataReceived += (_, e) => OnLine(e.Data);
        process.ErrorDataReceived += (_, e) => OnLine(e.Data);
        process.Exited += (_, _) => OnExited(process);

        process.Start();
        _job.Add(process);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _process = process;

        await Task.WhenAny(process.WaitForExitAsync(), Task.Delay(StartupGrace));
        if (process.HasExited)
        {
            var exitCode = process.ExitCode;
            _process = null;
            process.Dispose();
            DeleteConfig();
            State = CoreState.Failed;
            throw new InvalidOperationException($"sing-box завершился при запуске (код {exitCode}). Подробности в журнале.");
        }
        State = CoreState.Running;
        // The config holds proxy passwords in plain text; sing-box has read it by now.
        DeleteConfig();
    }

    public async Task StopAsync()
    {
        var process = _process;
        _process = null;
        if (process is null) return;

        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
        process.Dispose();
        DeleteConfig();
        State = CoreState.Stopped;
    }

    private void OnExited(Process process)
    {
        // Ignore exits we caused ourselves via StopAsync.
        if (!ReferenceEquals(process, _process)) return;
        _process = null;
        LogReceived?.Invoke($"sing-box завершился (код {process.ExitCode})");
        State = CoreState.Failed;
    }

    private void OnLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        LogReceived?.Invoke(AnsiEscape().Replace(line, ""));
    }

    /// <summary>A sing-box left over from a previous run would hold the TUN adapter name.</summary>
    private static void KillOrphans()
    {
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(CorePath)))
        {
            using (p)
            {
                try
                {
                    if (string.Equals(p.MainModule?.FileName, CorePath, StringComparison.OrdinalIgnoreCase))
                    {
                        p.Kill();
                        p.WaitForExit(3000);
                    }
                }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    // Not ours or already gone.
                }
            }
        }
    }

    private void DeleteConfig()
    {
        try
        {
            File.Delete(ConfigPath);
        }
        catch (IOException)
        {
        }
    }

    public void Dispose()
    {
        if (_process is { HasExited: false } process)
            process.Kill(entireProcessTree: true);
        _process?.Dispose();
        _job.Dispose();
        DeleteConfig();
    }

    [GeneratedRegex(@"\x1B\[[0-9;]*m")]
    private static partial Regex AnsiEscape();
}
