using System.Text.Json;
using ProxyManager.Services;

namespace ProxyManager.Tests;

public class ClashApiClientTests
{
    [Fact]
    public void Parses_connections_snapshot()
    {
        const string json = """
            {
              "downloadTotal": 1, "uploadTotal": 2,
              "connections": [
                { "id": "c1", "metadata": { "network": "tcp", "processPath": "C:\\ext\\claude.exe (PC\\radio)" }, "chains": ["direct"] },
                { "id": "c2", "metadata": { "network": "udp", "processPath": "" }, "chains": null }
              ]
            }
            """;

        var connections = ClashApiClient.ParseConnections(JsonDocument.Parse(json).RootElement);

        Assert.Equal(2, connections.Count);
        Assert.Equal(("c1", "tcp", @"C:\ext\claude.exe"), (connections[0].Id, connections[0].Network, connections[0].ProcessPath));
        Assert.Equal(["direct"], connections[0].Chains);
        Assert.Empty(connections[1].Chains);
    }

    [Theory]
    [InlineData(@"C:\a.exe (PC\user)", @"C:\a.exe")]
    [InlineData(@"C:\a.exe (1000)", @"C:\a.exe")]
    [InlineData(@"C:\Program Files (x86)\a.exe", @"C:\Program Files (x86)\a.exe")]
    [InlineData(@"C:\a.exe", @"C:\a.exe")]
    public void Strips_owner_suffix(string raw, string expected)
    {
        Assert.Equal(expected, ClashApiClient.StripUser(raw));
    }
}
