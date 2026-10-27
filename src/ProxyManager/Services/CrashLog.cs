namespace ProxyManager.Services;

/// <summary>Appends unhandled exceptions to %AppData%\ProxyManager\logs\crash.log.</summary>
public static class CrashLog
{
    public static string FilePath { get; } = Path.Combine(SettingsStore.DefaultDirectory, "logs", "crash.log");

    public static void Write(Exception exception, string source)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.AppendAllText(FilePath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}, version {AppInfo.Version}{Environment.NewLine}" +
                $"{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // Nothing sensible left to do.
        }
    }
}
