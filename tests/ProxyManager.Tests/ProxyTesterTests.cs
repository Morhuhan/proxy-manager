using ProxyManager.Models;
using ProxyManager.Services;

namespace ProxyManager.Tests;

/// <summary>
/// Live check against a real proxy. Set PM_TEST_PROXY (e.g. socks5://user:pass@host:port) to run;
/// otherwise the test passes without doing anything.
/// </summary>
public class ProxyTesterTests
{
    [Fact]
    public async Task Live_proxy_returns_its_external_ip()
    {
        var line = Environment.GetEnvironmentVariable("PM_TEST_PROXY");
        if (string.IsNullOrWhiteSpace(line)) return;

        var proxy = ProxyParser.TryParse(line, ProxyType.Socks5);
        Assert.NotNull(proxy);

        var result = await ProxyTester.CheckAsync(proxy);

        Assert.True(result.Ok, result.Error);
        Assert.False(string.IsNullOrEmpty(result.Ip));
        Console.WriteLine($"{proxy.Type} {proxy.Address}: IP {result.Ip}, {result.Elapsed.TotalMilliseconds:0} ms");
    }

    [Fact]
    public async Task Wrong_password_is_reported()
    {
        var line = Environment.GetEnvironmentVariable("PM_TEST_PROXY");
        if (string.IsNullOrWhiteSpace(line)) return;

        var proxy = ProxyParser.TryParse(line, ProxyType.Socks5)!;
        if (!proxy.HasCredentials) return;
        proxy.Password += "-wrong";

        var result = await ProxyTester.CheckAsync(proxy);

        Assert.False(result.Ok);
        Assert.False(string.IsNullOrEmpty(result.Error));
    }
}
