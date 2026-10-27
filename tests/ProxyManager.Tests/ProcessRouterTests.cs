using System.Text.Json.Nodes;
using ProxyManager.Models;
using ProxyManager.Services;

namespace ProxyManager.Tests;

public sealed class ProcessRouterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pm-router-" + Guid.NewGuid().ToString("N"));
    private readonly ProxyServer _a = new() { Id = "a", Host = "1.1.1.1", Port = 1080 };
    private readonly ProxyServer _b = new() { Id = "b", Host = "2.2.2.2", Port = 1080 };

    // Simulated system: pid -> (parent, path)
    private readonly Dictionary<int, (int Parent, string Path)> _system = new()
    {
        [1] = (0, @"C:\Windows\explorer.exe"),
        [10] = (1, @"C:\VSCode\Code.exe"),
        [11] = (10, @"C:\ext\claude.exe"),
        [12] = (11, @"C:\Git\bin\bash.exe"),
        [20] = (1, @"C:\Other\other.exe"),
    };

    private ProcessRouter CreateRouter() => new(_dir, [_a, _b],
        snapshot: () => _system.Select(kv => new ProcessEntry(kv.Key, kv.Value.Parent, Path.GetFileName(kv.Value.Path))).ToList(),
        getPath: pid => _system.TryGetValue(pid, out var p) ? p.Path : null);

    private static ResolvedRule Rule(string name, ProxyServer proxy, bool children = true) => new(name, proxy, children);

    private List<string> Paths(ProxyServer proxy)
    {
        var json = JsonNode.Parse(File.ReadAllText(SingBoxConfigBuilder.RuleSetPath(_dir, proxy)))!;
        return json["rules"]!.AsArray()
            .SelectMany(r => r!["process_path"]?.AsArray().Select(n => (string)n!) ?? [])
            .Where(p => !p.StartsWith(@"\\?\"))
            .ToList();
    }

    private List<string> Names(ProxyServer proxy)
    {
        var json = JsonNode.Parse(File.ReadAllText(SingBoxConfigBuilder.RuleSetPath(_dir, proxy)))!;
        return json["rules"]!.AsArray()
            .SelectMany(r => r!["process_name"]?.AsArray().Select(n => (string)n!) ?? [])
            .ToList();
    }

    [Fact]
    public void Running_descendants_are_routed_with_their_ancestor()
    {
        using var router = CreateRouter();
        router.Update([Rule("Code.exe", _a)]);

        Assert.Equal(["Code.exe"], Names(_a));
        Assert.Equal([@"C:\ext\claude.exe", @"C:\Git\bin\bash.exe"], Paths(_a));
        Assert.Empty(Paths(_b));
        Assert.Equal("a", router.ExpectedProxyId(@"C:\ext\claude.exe"));
        Assert.Equal("a", router.ExpectedProxyId(@"C:\VSCode\Code.exe"));
        Assert.Null(router.ExpectedProxyId(@"C:\Other\other.exe"));
        Assert.Equal([10, 11, 12], router.TrackedPids.Order());
    }

    [Fact]
    public void Children_are_not_tracked_when_the_rule_excludes_them()
    {
        using var router = CreateRouter();
        router.Update([Rule("Code.exe", _a, children: false)]);

        Assert.Empty(Paths(_a));
        Assert.Null(router.ExpectedProxyId(@"C:\ext\claude.exe"));
    }

    [Fact]
    public void Explicit_rule_wins_over_inherited_proxy()
    {
        using var router = CreateRouter();
        router.Update([Rule("Code.exe", _a), Rule("claude.exe", _b)]);

        Assert.Equal(["claude.exe"], Names(_b));
        // bash.exe descends from claude.exe, so it follows claude's proxy.
        Assert.Equal([@"C:\Git\bin\bash.exe"], Paths(_b));
        Assert.Empty(Paths(_a));
        Assert.Equal("b", router.ExpectedProxyId(@"C:\ext\claude.exe"));
    }

    [Fact]
    public void New_child_is_added_and_removed_when_it_exits()
    {
        using var router = CreateRouter();
        router.Update([Rule("Code.exe", _a)]);
        var changes = 0;
        router.Changed += () => changes++;

        _system[30] = (10, @"C:\node\node.exe");
        router.HandleStart(30, 10);
        Assert.Contains(@"C:\node\node.exe", Paths(_a));

        router.HandleStop(30);
        Assert.DoesNotContain(@"C:\node\node.exe", Paths(_a));
        Assert.Equal(2, changes);
    }

    [Fact]
    public void Program_started_later_becomes_a_root()
    {
        _system.Remove(10);
        _system.Remove(11);
        _system.Remove(12);
        using var router = CreateRouter();
        router.Update([Rule("Code.exe", _a)]);

        _system[40] = (1, @"C:\VSCode\Code.exe");
        router.HandleStart(40, 1);
        _system[41] = (40, @"C:\ext\claude.exe");
        router.HandleStart(41, 40);

        Assert.Equal([@"C:\ext\claude.exe"], Paths(_a));
    }

    [Fact]
    public void Unrelated_processes_do_not_rewrite_files()
    {
        using var router = CreateRouter();
        router.Update([Rule("Code.exe", _a)]);
        var changes = 0;
        router.Changed += () => changes++;

        _system[50] = (20, @"C:\Other\child.exe");
        router.HandleStart(50, 20);
        router.HandleStop(50);

        Assert.Equal(0, changes);
    }

    [Fact]
    public void Dispose_removes_rule_set_files()
    {
        var router = CreateRouter();
        router.Update([]);
        Assert.True(File.Exists(SingBoxConfigBuilder.RuleSetPath(_dir, _a)));

        router.Dispose();

        Assert.False(File.Exists(SingBoxConfigBuilder.RuleSetPath(_dir, _a)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }
}
