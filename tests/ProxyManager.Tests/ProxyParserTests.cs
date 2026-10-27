using ProxyManager.Models;
using ProxyManager.Services;

namespace ProxyManager.Tests;

public class ProxyParserTests
{
    [Theory]
    [InlineData("socks5://user:pass@1.2.3.4:1080", ProxyType.Socks5, "1.2.3.4", 1080, "user", "pass")]
    [InlineData("socks5h://user:pass@proxy.example.com:1080", ProxyType.Socks5, "proxy.example.com", 1080, "user", "pass")]
    [InlineData("http://user:pass@1.2.3.4:8080/", ProxyType.Http, "1.2.3.4", 8080, "user", "pass")]
    [InlineData("HTTPS://1.2.3.4:3128", ProxyType.Http, "1.2.3.4", 3128, "", "")]
    [InlineData("user:pass@1.2.3.4:8000", ProxyType.Socks5, "1.2.3.4", 8000, "user", "pass")]
    [InlineData("1.2.3.4:8000:user:pass", ProxyType.Socks5, "1.2.3.4", 8000, "user", "pass")]
    [InlineData("1.2.3.4:8000:user:pa:ss", ProxyType.Socks5, "1.2.3.4", 8000, "user", "pa:ss")]
    [InlineData("user:pass:1.2.3.4:8000", ProxyType.Socks5, "1.2.3.4", 8000, "user", "pass")]
    [InlineData("  1.2.3.4:8000  ", ProxyType.Socks5, "1.2.3.4", 8000, "", "")]
    [InlineData("socks5://us%40er:p%3Ass@1.2.3.4:1080", ProxyType.Socks5, "1.2.3.4", 1080, "us@er", "p:ss")]
    public void Parses_common_formats(string line, ProxyType type, string host, int port, string user, string pass)
    {
        var proxy = ProxyParser.TryParse(line, ProxyType.Socks5);

        Assert.NotNull(proxy);
        Assert.Equal(type, proxy.Type);
        Assert.Equal(host, proxy.Host);
        Assert.Equal(port, proxy.Port);
        Assert.Equal(user, proxy.Username);
        Assert.Equal(pass, proxy.Password);
    }

    [Fact]
    public void Uses_default_type_when_scheme_is_missing()
    {
        Assert.Equal(ProxyType.Http, ProxyParser.TryParse("1.2.3.4:8080", ProxyType.Http)!.Type);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.2.3.4")]
    [InlineData("1.2.3.4:99999")]
    [InlineData("1.2.3.4:abc")]
    [InlineData("ftp://1.2.3.4:21")]
    [InlineData("a:b:c")]
    public void Rejects_garbage(string line)
    {
        Assert.Null(ProxyParser.TryParse(line, ProxyType.Socks5));
    }

    [Fact]
    public void ParseMany_reports_failed_lines_and_skips_comments()
    {
        var (parsed, failed) = ProxyParser.ParseMany("1.1.1.1:80\r\n# comment\n\nbad line\nsocks5://2.2.2.2:1080", ProxyType.Http);

        Assert.Equal(["1.1.1.1", "2.2.2.2"], parsed.Select(p => p.Host));
        Assert.Equal(["bad line"], failed);
    }
}
