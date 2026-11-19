using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VpnLedger;

internal sealed class Collector : IDisposable
{
    private HttpClient? http;
    private DateTimeOffset refresh;
    private Dictionary<string, (string App, string? Path)> knownOwners = new();
    private Dictionary<string, string> types = new(StringComparer.Ordinal);
    private static readonly HashSet<string> ProxyTypes = new(StringComparer.OrdinalIgnoreCase) {
        "Shadowsocks","ShadowsocksR","Snell","Socks5","Http","Vmess","Vless","Trojan","Hysteria","Hysteria2",
        "Ssh","Tuic","WireGuard","AnyTLS","Mieru","Sudoku","Masque","TrustTunnel","OpenVPN","Tailscale","EasyTier",
        "Naive","Juicity", "AmneziaWG"
    };
    private static HttpClient Connect()
    {
        string? pipe = null;
        foreach (var entry in Directory.EnumerateFiles(@"\\.\pipe\"))
        {
            var name = Path.GetFileName(entry);
            if (!name.StartsWith("verge-mihomo-production", StringComparison.OrdinalIgnoreCase) &&
                !name.Equals("verge-mihomo", StringComparison.OrdinalIgnoreCase) &&
                !name.Equals("mihomo", StringComparison.OrdinalIgnoreCase)) continue;
            if (pipe != null) throw new IOException("检测到多个内核，请仅运行一个 Mihomo 实例。");
            pipe = name;
        }
        if (pipe == null) throw new IOException("请启动 Clash Verge，并确认 Mihomo 内核正在运行。");
        var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false };
        handler.ConnectCallback = async (_, token) => {
            var stream = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous,
                System.Security.Principal.TokenImpersonationLevel.Identification);
            try { await stream.ConnectAsync(token); return stream; }
            catch { stream.Dispose(); throw; }
        };
        return new HttpClient(handler) { BaseAddress = new Uri("http://localhost/"), Timeout = TimeSpan.FromSeconds(4) };
    }
    internal static Route Classify(string? leaf, Dictionary<string, string> map)
    {
        if (leaf == null) return Route.Unknown;
        string? type = map.GetValueOrDefault(leaf);
        if (type == null && leaf is "DIRECT" or "REJECT" or "REJECT-DROP" or "PASS") type = leaf;
        return type?.ToUpperInvariant() switch {
            "DIRECT" => Route.Direct,
            "REJECT" or "REJECTDROP" or "REJECT-DROP" or "PASS" or "DNS" => Route.Ignored,
            _ => type != null && ProxyTypes.Contains(type) ? Route.Proxy : Route.Unknown
        };
    }
    private async Task<JsonDocument> Get(string path, CancellationToken token)
    {
        using var reply = await http!.GetAsync(path, token).ConfigureAwait(false);
        reply.EnsureSuccessStatusCode();
        using var stream = await reply.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
    }
    private static string Field(JsonElement e, string key) =>
        e.TryGetProperty(key, out var field) ? field.ToString() : "";
    private static string Address(JsonElement e, string key) =>
        IPAddress.TryParse(Field(e,key), out var ip) ? Owners.Normalize(ip) : "";
    private static int Port(JsonElement e, string key) => int.TryParse(Field(e,key), out var n) ? n : 0;

    public async Task<List<Flow>> Read(CancellationToken token)
    {
        http ??= Connect();
        if (DateTimeOffset.UtcNow >= refresh)
        {
            using var proxies = await Get("proxies", token).ConfigureAwait(false);
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in proxies.RootElement.GetProperty("proxies").EnumerateObject())
                map[pair.Name] = Field(pair.Value, "type");
            // Provider-only nodes can be absent from /proxies.
            using var providers = await Get("providers/proxies", token).ConfigureAwait(false);
            foreach (var provider in providers.RootElement.GetProperty("providers").EnumerateObject())
                if (provider.Value.TryGetProperty("proxies", out var nodes) && nodes.ValueKind == JsonValueKind.Array)
                    foreach (var node in nodes.EnumerateArray()) map.TryAdd(Field(node,"name"), Field(node,"type"));
            types = map;
            refresh = DateTimeOffset.UtcNow.AddMinutes(1);
        }
        using var doc = await Get("connections",token).ConfigureAwait(false);
        var result = new List<Flow>();
        var connections = doc.RootElement.GetProperty("connections");
        if (connections.ValueKind == JsonValueKind.Null) { knownOwners.Clear(); return result; }
        var currentOwners = new Dictionary<string, (string App, string? Path)>();
        Owners? owners = null;
        var local = new HashSet<string>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            foreach (var address in adapter.GetIPProperties().UnicastAddresses) local.Add(Owners.Normalize(address.Address));
        foreach (var conn in connections.EnumerateArray())
        {
            var m = conn.GetProperty("metadata");
            var ip = Address(m,"sourceIP");
            if (!IPAddress.TryParse(ip,out var source) || (!IPAddress.IsLoopback(source) && !local.Contains(ip))) continue;
            var id = Field(conn,"id");
            if (id.Length == 0) continue;
            var chains = conn.GetProperty("chains");
            var route = Classify(chains.ValueKind == JsonValueKind.Array && chains.GetArrayLength() > 0 ? chains[0].GetString() : null, types);
            if (route != Route.Proxy) continue;
            string app = Ledger.UnknownApp;
            string? path = null;
            if (route == Route.Proxy)
            {
                path = Field(m,"processPath");
                string raw = string.IsNullOrEmpty(path) ? Field(m,"process") : path;
                if (!string.IsNullOrWhiteSpace(raw)) app = Path.GetFileName(raw).ToLowerInvariant();
                else if (knownOwners.TryGetValue(id, out var known)) (app, path) = known;
                else
                {
                    owners ??= new Owners();
                    bool tun = Field(m,"type").Equals("tun", StringComparison.OrdinalIgnoreCase);
                    var net = Field(m,"network");
                    var peer = Address(m,tun ? "destinationIP" : "inboundIP");
                    bool omittedFakeIp = tun && peer.Length == 0 && Field(m,"dnsMode") == "fake-ip";
                    if (net is "tcp" or "udp")
                        (app, path) = owners.Find(net == "tcp", ip, Port(m,"sourcePort"),
                            peer, Port(m,tun ? "destinationPort" : "inboundPort"), omittedFakeIp);
                }
                if (app != Ledger.UnknownApp) currentOwners[id] = (app, path);
            }
            DateTimeOffset.TryParse(Field(conn,"start"), out var start);
            Clue? clue = null;
            if (app == Ledger.UnknownApp)
            {
                string host = Field(m,"host"), destination = Address(m,"destinationIP");
                string target = string.Join(" · ",Array.FindAll(new[] { host, destination }, s => s.Length != 0));
                int port = Port(m,"destinationPort");
                clue = new($"{ip} · {Port(m,"sourcePort")}", $"{(target.Length == 0 ? "目标未报告" : target)} · {port}",
                    $"{Field(m,"network").ToUpperInvariant()} / {Field(m,"type")}", Field(conn,"rule"),
                    "内核未提供进程名，Windows 连接表也未返回可确认的所属进程。");
            }
            result.Add(new(id, start, route, app, string.IsNullOrWhiteSpace(path) ? null : path,
                new(conn.GetProperty("upload").GetInt64(), conn.GetProperty("download").GetInt64()), clue));
        }
        knownOwners = currentOwners;
        return result;
    }
    public void Dispose() { http?.Dispose(); http = null; refresh = default; knownOwners = new(); }
}
