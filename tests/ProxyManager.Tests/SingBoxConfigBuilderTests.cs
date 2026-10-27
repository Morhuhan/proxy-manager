using System.Text.Json.Nodes;
using ProxyManager.Models;
using ProxyManager.Services;

namespace ProxyManager.Tests;

public class SingBoxConfigBuilderTests
{
    private const string Dir = @"C:\data";

    private static readonly ProxyServer Socks = new()
    {
        Id = "s", Type = ProxyType.Socks5, Host = "1.2.3.4", Port = 1080, Username = "u", Password = "p",
    };

    private static readonly ProxyServer Http = new() { Id = "h", Type = ProxyType.Http, Host = "5.6.7.8", Port = 8080 };

    private static AppRule Rule(string exe, string? proxyId, bool enabled = true, bool children = true) =>
        new() { ExePath = $@"C:\Apps\{exe}", ProxyId = proxyId, Enabled = enabled, IncludeChildren = children };

    [Fact]
    public void Every_valid_proxy_gets_an_outbound_a_rule_set_and_a_route()
    {
        var invalid = new ProxyServer { Id = "bad", Host = "" };
        var config = JsonNode.Parse(SingBoxConfigBuilder.Build([Socks, invalid, Http], Dir, controller: null))!;

        var outbounds = config["outbounds"]!.AsArray();
        Assert.Equal(["direct", "proxy-s", "proxy-h"], outbounds.Select(o => (string)o!["tag"]!));
        var socks = outbounds[1]!;
        Assert.Equal(("socks", "1.2.3.4", 1080, "5", "u", "p"),
            ((string)socks["type"]!, (string)socks["server"]!, (int)socks["server_port"]!,
             (string)socks["version"]!, (string)socks["username"]!, (string)socks["password"]!));
        Assert.Equal("http", (string)outbounds[2]!["type"]!);
        Assert.Null(outbounds[2]!["username"]);

        var route = config["route"]!;
        var ruleSets = route["rule_set"]!.AsArray();
        Assert.Equal(["apps-s", "apps-h"], ruleSets.Select(r => (string)r!["tag"]!));
        Assert.Equal(@"C:\data\apps-s.json", (string)ruleSets[0]!["path"]!);
        Assert.Equal("local", (string)ruleSets[0]!["type"]!);

        var rules = route["rules"]!.AsArray();
        Assert.Equal(3, rules.Count);
        Assert.Equal(("apps-s", "tcp", "proxy-s"),
            ((string)rules[0]!["rule_set"]!, (string)rules[0]!["network"]!, (string)rules[0]!["outbound"]!));
        Assert.Equal("proxy-h", (string)rules[1]!["outbound"]!);
        // UDP of proxied programs is blocked.
        Assert.Equal("reject", (string)rules[2]!["action"]!);
        Assert.Equal(["apps-s", "apps-h"], rules[2]!["rule_set"]!.AsArray().Select(n => (string)n!));

        Assert.Equal("direct", (string)route["final"]!);
        Assert.True((bool)route["find_process"]!);
        Assert.Null(config["experimental"]);
    }

    [Fact]
    public void Controller_enables_the_local_api()
    {
        var config = JsonNode.Parse(SingBoxConfigBuilder.Build([Socks], Dir, new ClashController(9123, "abc")))!;

        var api = config["experimental"]!["clash_api"]!;
        Assert.Equal("127.0.0.1:9123", (string)api["external_controller"]!);
        Assert.Equal("abc", (string)api["secret"]!);
    }

    [Fact]
    public void Config_does_not_depend_on_rules()
    {
        // Rules live in rule-set files, so changing them never requires a core restart.
        Assert.Equal(SingBoxConfigBuilder.Build([Socks], Dir, null), SingBoxConfigBuilder.Build([Socks], Dir, null));
    }

    [Fact]
    public void Tun_inbound_captures_all_traffic()
    {
        var tun = JsonNode.Parse(SingBoxConfigBuilder.Build([Socks], Dir, null))!["inbounds"]![0]!;

        Assert.Equal("tun", (string)tun["type"]!);
        Assert.True((bool)tun["auto_route"]!);
        Assert.True((bool)tun["strict_route"]!);
    }

    [Fact]
    public void Resolve_skips_disabled_rules_and_missing_or_invalid_proxies()
    {
        var invalid = new ProxyServer { Id = "bad", Host = "" };
        var resolved = SingBoxConfigBuilder.Resolve([Socks, invalid],
        [
            Rule("a.exe", "s", enabled: false),
            Rule("b.exe", "missing"),
            Rule("c.exe", null),
            Rule("d.exe", "bad"),
            Rule("e.exe", "s", children: false),
        ]);

        var rule = Assert.Single(resolved);
        Assert.Equal(("e.exe", "s", false), (rule.ProcessName, rule.Proxy.Id, rule.IncludeChildren));
    }

    [Fact]
    public void Resolve_keeps_the_first_rule_for_duplicate_programs()
    {
        var resolved = SingBoxConfigBuilder.Resolve([Socks, Http], [Rule("app.exe", "s"), Rule("APP.exe", "h")]);

        Assert.Equal("s", Assert.Single(resolved).Proxy.Id);
    }

    [Fact]
    public void Rule_set_lists_names_and_paths_as_separate_rules()
    {
        var ruleSet = JsonNode.Parse(SingBoxConfigBuilder.BuildRuleSet(["Code.exe"], [@"C:\x\claude.exe"]))!;

        Assert.Equal(3, (int)ruleSet["version"]!);
        var rules = ruleSet["rules"]!.AsArray();
        Assert.Equal(["Code.exe"], rules[0]!["process_name"]!.AsArray().Select(n => (string)n!));
        var paths = rules[1]!["process_path"]!.AsArray().Select(n => (string)n!).ToList();
        Assert.Contains(@"C:\x\claude.exe", paths);
    }

    [Fact]
    public void Empty_rule_set_is_still_valid()
    {
        var rules = JsonNode.Parse(SingBoxConfigBuilder.BuildRuleSet([], []))!["rules"]!.AsArray();

        // Only the placeholder path rule: sing-box needs at least one rule.
        var rule = Assert.Single(rules);
        Assert.Single(rule!["process_path"]!.AsArray());
    }
}
