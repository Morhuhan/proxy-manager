namespace ProxyManager.Models;

public sealed class AppSettings
{
    public List<ProxyServer> Proxies { get; set; } = [];
    public List<AppRule> Rules { get; set; } = [];

    /// <summary>Start the proxy right after the app starts.</summary>
    public bool ConnectOnStartup { get; set; }
}
