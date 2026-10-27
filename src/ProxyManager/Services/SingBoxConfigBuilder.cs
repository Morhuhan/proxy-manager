using System.Text.Json;
using System.Text.Json.Nodes;
using ProxyManager.Models;

namespace ProxyManager.Services;

/// <summary>A rule that takes effect: one program, one proxy.</summary>
public sealed record ResolvedRule(string ProcessName, ProxyServer Proxy, bool IncludeChildren);

/// <summary>
/// Builds a sing-box (1.14) config. A TUN inbound captures all traffic; each proxy gets a local
/// rule-set file listing its programs. sing-box reloads those files on change, so programs can be
/// added or moved between proxies without restarting the core — the config itself only depends on
/// the proxies.
/// </summary>
public static class SingBoxConfigBuilder
{
    public const string TunInterfaceName = "ProxyManager";
    public const string TunAddress = "172.19.0.1";

    // Keeps every rule-set non-empty and marked as a process rule-set; matches no real process.
    private const string PlaceholderPath = @"\\?\ProxyManager\no-process";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static string OutboundTag(ProxyServer proxy) => "proxy-" + proxy.Id;

    public static string RuleSetTag(ProxyServer proxy) => "apps-" + proxy.Id;

    public static string RuleSetPath(string directory, ProxyServer proxy) =>
        Path.Combine(directory, $"apps-{proxy.Id}.json");

    /// <summary>
    /// Rules that take effect: enabled, with an existing valid proxy. A program can only have one
    /// proxy: if it appears in several rules, the first one wins.
    /// </summary>
    public static List<ResolvedRule> Resolve(IEnumerable<ProxyServer> proxies, IEnumerable<AppRule> rules)
    {
        var proxyById = proxies.Where(p => p.IsValid).ToDictionary(p => p.Id);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resolved = new List<ResolvedRule>();
        foreach (var rule in rules)
        {
            if (rule.Enabled && rule.ProcessName.Length > 0 && rule.ProxyId is { } id
                && proxyById.TryGetValue(id, out var proxy) && seen.Add(rule.ProcessName))
            {
                resolved.Add(new ResolvedRule(rule.ProcessName, proxy, rule.IncludeChildren));
            }
        }
        return resolved;
    }

    public static string Build(IEnumerable<ProxyServer> proxies, string ruleSetDirectory, ClashController? controller)
    {
        var valid = proxies.Where(p => p.IsValid).ToList();

        var outbounds = new JsonArray { new JsonObject { ["type"] = "direct", ["tag"] = "direct" } };
        var ruleSets = new JsonArray();
        var routeRules = new JsonArray();

        foreach (var proxy in valid)
        {
            outbounds.Add(BuildOutbound(proxy));
            ruleSets.Add(new JsonObject
            {
                ["type"] = "local",
                ["tag"] = RuleSetTag(proxy),
                ["format"] = "source",
                ["path"] = RuleSetPath(ruleSetDirectory, proxy),
            });
            routeRules.Add(new JsonObject
            {
                ["rule_set"] = RuleSetTag(proxy),
                ["network"] = "tcp",
                ["outbound"] = OutboundTag(proxy),
            });
        }

        if (valid.Count > 0)
        {
            // Plain SOCKS5/HTTP proxies usually can't carry UDP. Blocking it (instead of letting it go
            // direct) keeps the real IP from leaking via QUIC/WebRTC; apps fall back to TCP.
            routeRules.Add(new JsonObject
            {
                ["rule_set"] = ToJsonArray(valid.Select(RuleSetTag)),
                ["action"] = "reject",
            });
        }

        var config = new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = "info", ["timestamp"] = true },
            ["dns"] = new JsonObject
            {
                ["servers"] = new JsonArray { new JsonObject { ["type"] = "local", ["tag"] = "local" } },
            },
            ["inbounds"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "tun",
                    ["tag"] = "tun-in",
                    ["interface_name"] = TunInterfaceName,
                    ["address"] = new JsonArray(TunAddress + "/30"),
                    ["auto_route"] = true,
                    // Also enables DNS leak protection on Windows (dns_mode defaults to "hijack").
                    ["strict_route"] = true,
                    ["stack"] = "mixed",
                },
            },
            ["outbounds"] = outbounds,
            ["route"] = new JsonObject
            {
                ["rule_set"] = ruleSets,
                ["rules"] = routeRules,
                ["final"] = "direct",
                // Process info is needed for rule-set matching and for the connection list.
                ["find_process"] = true,
                // Bind outbound sockets to the physical NIC so they don't loop back into the TUN.
                ["auto_detect_interface"] = true,
                ["default_domain_resolver"] = "local",
            },
        };

        if (controller is not null)
        {
            // Local control API: used to close connections that a rule change re-routes.
            config["experimental"] = new JsonObject
            {
                ["clash_api"] = new JsonObject
                {
                    ["external_controller"] = $"127.0.0.1:{controller.Port}",
                    ["secret"] = controller.Secret,
                },
            };
        }

        return config.ToJsonString(Indented);
    }

    /// <summary>Content of one proxy's rule-set file: programs by name plus launched child processes by path.</summary>
    public static string BuildRuleSet(IEnumerable<string> processNames, IEnumerable<string> processPaths)
    {
        var rules = new JsonArray();
        var names = processNames.ToList();
        // Separate headless rules are OR-ed; items inside one rule would be AND-ed.
        if (names.Count > 0)
            rules.Add(new JsonObject { ["process_name"] = ToJsonArray(names) });
        rules.Add(new JsonObject { ["process_path"] = ToJsonArray(processPaths.Prepend(PlaceholderPath)) });

        return new JsonObject { ["version"] = 3, ["rules"] = rules }.ToJsonString(Indented);
    }

    private static JsonObject BuildOutbound(ProxyServer proxy)
    {
        var outbound = new JsonObject
        {
            ["type"] = proxy.Type switch
            {
                ProxyType.Socks5 => "socks",
                ProxyType.Http => "http",
                _ => throw new ArgumentOutOfRangeException(nameof(proxy), proxy.Type, null),
            },
            ["tag"] = OutboundTag(proxy),
            ["server"] = proxy.Host,
            ["server_port"] = proxy.Port,
        };
        if (proxy.Type == ProxyType.Socks5)
            outbound["version"] = "5";
        if (proxy.HasCredentials)
        {
            outbound["username"] = proxy.Username;
            outbound["password"] = proxy.Password;
        }
        return outbound;
    }

    private static JsonArray ToJsonArray(IEnumerable<string> items) =>
        new(items.Select(i => (JsonNode)JsonValue.Create(i)).ToArray());
}
