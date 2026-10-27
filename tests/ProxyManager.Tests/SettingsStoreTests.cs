using ProxyManager.Models;
using ProxyManager.Services;

namespace ProxyManager.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pm-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Round_trips_settings_and_does_not_store_password_in_plain_text()
    {
        var store = new SettingsStore(_dir);
        var proxy = new ProxyServer { Name = "Test", Type = ProxyType.Http, Host = "h", Port = 1, Username = "u", Password = "secret-pass" };
        store.Save(new AppSettings
        {
            Proxies = [proxy],
            Rules = [new AppRule { ExePath = @"C:\a.exe", ProxyId = proxy.Id, Enabled = false }],
        });

        var json = File.ReadAllText(Path.Combine(_dir, "settings.json"));
        Assert.DoesNotContain("secret-pass", json);

        var loaded = store.Load();
        var p = Assert.Single(loaded.Proxies);
        Assert.Equal((proxy.Id, "Test", ProxyType.Http, "h", 1, "u", "secret-pass"),
            (p.Id, p.Name, p.Type, p.Host, p.Port, p.Username, p.Password));
        var r = Assert.Single(loaded.Rules);
        Assert.Equal((@"C:\a.exe", proxy.Id, false), (r.ExePath, r.ProxyId, r.Enabled));
    }

    [Fact]
    public void Missing_file_gives_empty_settings()
    {
        var settings = new SettingsStore(_dir).Load();
        Assert.Empty(settings.Proxies);
        Assert.Empty(settings.Rules);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }
}
