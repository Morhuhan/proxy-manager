using ProxyManager.Models;

namespace ProxyManager.Services;

/// <summary>
/// Parses proxy strings in the formats sellers usually hand out:
/// <c>socks5://user:pass@host:port</c>, <c>user:pass@host:port</c>,
/// <c>host:port:user:pass</c>, <c>user:pass:host:port</c>, <c>host:port</c>.
/// </summary>
public static class ProxyParser
{
    public static (List<ProxyServer> Parsed, List<string> Failed) ParseMany(string text, ProxyType defaultType)
    {
        var parsed = new List<ProxyServer>();
        var failed = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            if (TryParse(line, defaultType) is { } proxy)
                parsed.Add(proxy);
            else
                failed.Add(line);
        }
        return (parsed, failed);
    }

    public static ProxyServer? TryParse(string line, ProxyType defaultType)
    {
        line = line.Trim();
        var type = defaultType;

        var schemeEnd = line.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd >= 0)
        {
            if (ParseScheme(line[..schemeEnd]) is not { } schemeType) return null;
            type = schemeType;
            line = line[(schemeEnd + 3)..].TrimEnd('/');
        }

        string host, port, user = "", pass = "";

        var at = line.LastIndexOf('@');
        if (at >= 0)
        {
            if (!SplitPair(line[..at], out user, out pass)) return null;
            if (!SplitPair(line[(at + 1)..], out host, out port)) return null;
            user = Uri.UnescapeDataString(user);
            pass = Uri.UnescapeDataString(pass);
        }
        else
        {
            var parts = line.Split(':');
            switch (parts.Length)
            {
                case 2:
                    (host, port) = (parts[0], parts[1]);
                    break;
                case >= 4 when IsPort(parts[1]):
                    // host:port:user:pass (the password may itself contain ':')
                    (host, port, user) = (parts[0], parts[1], parts[2]);
                    pass = string.Join(':', parts[3..]);
                    break;
                case 4 when IsPort(parts[3]):
                    (user, pass, host, port) = (parts[0], parts[1], parts[2], parts[3]);
                    break;
                default:
                    return null;
            }
        }

        if (host.Length == 0 || !IsPort(port)) return null;

        return new ProxyServer
        {
            Type = type,
            Host = host,
            Port = int.Parse(port),
            Username = user,
            Password = pass,
        };
    }

    private static ProxyType? ParseScheme(string scheme) => scheme.ToLowerInvariant() switch
    {
        "socks" or "socks5" or "socks5h" => ProxyType.Socks5,
        "http" or "https" => ProxyType.Http,
        _ => null,
    };

    private static bool SplitPair(string s, out string left, out string right)
    {
        var i = s.IndexOf(':');
        if (i <= 0)
        {
            (left, right) = ("", "");
            return false;
        }
        (left, right) = (s[..i], s[(i + 1)..]);
        return true;
    }

    private static bool IsPort(string s) => int.TryParse(s, out var p) && p is > 0 and <= 65535;
}
