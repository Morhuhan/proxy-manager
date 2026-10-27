using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace ProxyManager.Services;

public sealed record ClashController(int Port, string Secret);

public sealed record CoreConnection(string Id, string Network, string ProcessPath, IReadOnlyList<string> Chains);

/// <summary>Client for sing-box's local Clash-compatible API (connection list / close).</summary>
public sealed class ClashApiClient : IDisposable
{
    private readonly HttpClient _http;

    public ClashApiClient(ClashController controller)
    {
        _http = new HttpClient(new SocketsHttpHandler { UseProxy = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{controller.Port}/"),
            Timeout = TimeSpan.FromSeconds(5),
        };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", controller.Secret);
    }

    /// <summary>Picks a free loopback port and a random secret for the API.</summary>
    public static ClashController Allocate()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return new ClashController(port, Convert.ToHexString(RandomNumberGenerator.GetBytes(16)));
    }

    public async Task<List<CoreConnection>> GetConnectionsAsync(CancellationToken ct = default)
    {
        using var doc = JsonDocument.Parse(await _http.GetStringAsync("connections", ct));
        return ParseConnections(doc.RootElement);
    }

    public async Task CloseAsync(string id, CancellationToken ct = default)
    {
        using var response = await _http.DeleteAsync("connections/" + Uri.EscapeDataString(id), ct);
    }

    internal static List<CoreConnection> ParseConnections(JsonElement root)
    {
        var result = new List<CoreConnection>();
        if (!root.TryGetProperty("connections", out var connections) || connections.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var c in connections.EnumerateArray())
        {
            var metadata = c.TryGetProperty("metadata", out var m) ? m : default;
            var chains = c.TryGetProperty("chains", out var ch) && ch.ValueKind == JsonValueKind.Array
                ? ch.EnumerateArray().Select(x => x.GetString() ?? "").ToList()
                : [];
            result.Add(new CoreConnection(
                String(c, "id"),
                String(metadata, "network"),
                StripUser(String(metadata, "processPath")),
                chains));
        }
        return result;
    }

    /// <summary>sing-box may append the owner: "C:\app.exe (PC\user)".</summary>
    internal static string StripUser(string processPath)
    {
        var i = processPath.LastIndexOf(" (", StringComparison.Ordinal);
        return i > 0 && processPath.EndsWith(')') && processPath[..i].EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processPath[..i]
            : processPath;
    }

    private static string String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : "";

    public void Dispose() => _http.Dispose();
}
