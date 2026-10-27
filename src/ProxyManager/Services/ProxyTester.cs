using System.Diagnostics;
using System.Net;
using System.Net.Http;
using ProxyManager.Models;

namespace ProxyManager.Services;

public sealed record ProxyCheckResult(bool Ok, string? Ip, TimeSpan Elapsed, string? Error);

/// <summary>Checks a proxy by fetching the external IP through it.</summary>
public static class ProxyTester
{
    private static readonly Uri IpEndpoint = new("https://api.ipify.org");
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public static async Task<ProxyCheckResult> CheckAsync(ProxyServer proxy, CancellationToken ct = default)
    {
        var scheme = proxy.Type == ProxyType.Socks5 ? "socks5" : "http";
        var webProxy = new WebProxy($"{scheme}://{proxy.Host}:{proxy.Port}");
        if (proxy.HasCredentials)
            webProxy.Credentials = new NetworkCredential(proxy.Username, proxy.Password);

        using var handler = new SocketsHttpHandler { Proxy = webProxy, UseProxy = true };
        using var client = new HttpClient(handler) { Timeout = Timeout };

        var sw = Stopwatch.StartNew();
        try
        {
            var ip = (await client.GetStringAsync(IpEndpoint, ct)).Trim();
            return new ProxyCheckResult(true, ip, sw.Elapsed, null);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ProxyCheckResult(false, null, sw.Elapsed, "таймаут");
        }
        catch (HttpRequestException e)
        {
            return new ProxyCheckResult(false, null, sw.Elapsed, Describe(e));
        }
        catch (UriFormatException)
        {
            return new ProxyCheckResult(false, null, sw.Elapsed, "некорректный адрес");
        }
    }

    private static string Describe(HttpRequestException e) => e switch
    {
        { StatusCode: HttpStatusCode.ProxyAuthenticationRequired } => "неверный логин или пароль",
        { HttpRequestError: HttpRequestError.ProxyTunnelError } => "прокси отклонил соединение",
        { HttpRequestError: HttpRequestError.NameResolutionError } => "не удалось найти хост",
        { HttpRequestError: HttpRequestError.ConnectionError } => "нет соединения с прокси",
        _ => e.InnerException?.Message ?? e.Message,
    };
}
