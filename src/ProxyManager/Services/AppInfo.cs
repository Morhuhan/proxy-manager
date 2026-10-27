using System.Reflection;

namespace ProxyManager.Services;

public static class AppInfo
{
    /// <summary>Product version without the "+commit" build metadata.</summary>
    public static string Version { get; } =
        (typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?")
        .Split('+')[0];

    /// <summary>Version of the bundled sing-box, written by scripts/fetch-singbox.ps1.</summary>
    public static string CoreVersion { get; } = ReadCoreVersion();

    public static string LicensesDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "licenses");

    private static string ReadCoreVersion()
    {
        var path = Path.Combine(Path.GetDirectoryName(SingBoxService.CorePath)!, "VERSION");
        return File.Exists(path) ? File.ReadAllText(path).Trim() : "?";
    }
}
