using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace ProxyManager.Services;

/// <summary>
/// Starts the app at logon through Task Scheduler. Unlike the Run registry key, a task can start
/// the app elevated without a UAC prompt, which the TUN adapter needs.
/// </summary>
public static class Autostart
{
    public const string Argument = "--autostart";

    // The installer's uninstaller removes every task in this folder.
    private const string TaskFolder = @"\ProxyManager\";

    private static string TaskName
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return TaskFolder + "Autostart-" + identity.User!.Value;
        }
    }

    public static bool IsEnabled() => RunSchtasks("/Query", "/TN", TaskName).ExitCode == 0;

    /// <summary>Creates or replaces the task, pointing it at the current executable.</summary>
    public static void Enable()
    {
        var xmlPath = Path.Combine(Path.GetTempPath(), $"ProxyManager-task-{Guid.NewGuid():N}.xml");
        try
        {
            // schtasks expects UTF-16 for XML task definitions.
            File.WriteAllText(xmlPath, BuildTaskXml(), Encoding.Unicode);
            var (code, output) = RunSchtasks("/Create", "/TN", TaskName, "/XML", xmlPath, "/F");
            if (code != 0)
                throw new InvalidOperationException($"Не удалось создать задачу автозапуска: {output}");
        }
        finally
        {
            File.Delete(xmlPath);
        }
    }

    public static void Disable()
    {
        if (!IsEnabled()) return;
        var (code, output) = RunSchtasks("/Delete", "/TN", TaskName, "/F");
        if (code != 0)
            throw new InvalidOperationException($"Не удалось удалить задачу автозапуска: {output}");
    }

    internal static string BuildTaskXml()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var exe = SecurityElement.Escape(Environment.ProcessPath!);
        var user = SecurityElement.Escape(identity.Name);
        var sid = identity.User!.Value;

        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Запуск Proxy Manager при входе в Windows</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{user}</UserId>
                  <Delay>PT5S</Delay>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{sid}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <!-- The default limit (72 hours) would kill a long-running tray app. -->
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{exe}</Command>
                  <Arguments>{Argument}</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    private static (int ExitCode, string Output) RunSchtasks(params string[] args)
    {
        var startInfo = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, (stderr + stdout.Result).Trim());
    }
}
