using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using VpnLedger;

class Checks
{
    private static void Check(bool ok, string name)
    {
        if (!ok) throw new Exception(name);
        Console.WriteLine("PASS " + name);
    }
    static async Task Main(string[] args)
    {
        var start = new DateTimeOffset(DateTime.Today.AddHours(10));
        var ledger = new Ledger(new());
        Flow F(string id, int up, int down, Route route = Route.Proxy, string app = "browser.exe", int offset = -1) =>
            new(id, start.AddSeconds(offset), route, app, null, new(up,down));
        ledger.Sample(start, new() { F("a",100,200), F("b",90,30,Route.Direct) });
        var day = ledger.Days.Values.Single();
        Check(day.Sum.Total == 0, "first sample is a baseline");
        ledger.Sample(start.AddSeconds(2), new() { F("a",110,240), F("b",100,60,Route.Direct),
            F("c",3,5,app:Ledger.UnknownApp,offset:1), F("d",6,2,Route.Unknown,offset:1) });
        Check(day.Sum == new Bytes(13,45) && day.Apps.Count == 2, "proxy-only collection drops all other routes");
        Check(day.Apps[Ledger.UnknownApp].Total == 8, "unattributed usage remains visible");
        ledger.Sample(start.AddSeconds(3), new() { F("a",114,241,app:Ledger.UnknownApp) });
        Check(day.Apps["browser.exe"].Total == 55 && day.Apps[Ledger.UnknownApp].Total == 8, "retain known owner across socket disappearance");
        ledger.Sample(start.AddSeconds(30), new() { F("a",800,900) });
        ledger.Break();
        ledger.Sample(start.AddSeconds(31), new() { F("a",900,950) });
        Check(day.Sum.Total == 63, "gap and resume exclude unseen bytes");
        var manyApps = new Day();
        foreach (var (key,n) in new[] { ("a",80),("b",50),("c",30),("d",20),(Ledger.UnknownApp,1) })
            manyApps.Apps[key] = new(0,n);
        var tiles = MainWindow.MapItems(manyApps);
        Check(tiles.Count == 4 && tiles.Any(t => t.Key == Ledger.UnknownApp) && tiles.Single(t => t.Key == null).Usage.Total == 50 &&
              tiles.Sum(t => t.Usage.Total) == manyApps.Sum.Total, "treemap groups small apps and preserves unknown bytes");
        Check(MainWindow.MapItems(new Day()).Count == 0,"empty map");
        var clue = new Clue("127.0.0.1 · 50","example.test · 443","TCP / HTTPS","Domain","no unique owner");
        ledger.Sample(start.AddSeconds(32),new() { F("unknown",9,18,app:Ledger.UnknownApp,offset:32) with { Clue = clue } });
        Check(day.Evidence["unknown"].Usage == new Bytes(9,18), "unknown endpoint and observed delta");
        var global = new Ledger(new());
        GlobalSnapshot G(long bytes,long app) => new(new() { ["adapter"] = new("Wi-Fi",new(0,bytes)) },
            new() { new("id","direct.exe","id","Wi-Fi",new(0,app)) });
        global.SampleGlobal(start,G(123,0)); global.SampleGlobal(start.AddSeconds(1),G(323,25));
        global.SampleGlobal(start.AddSeconds(2),G(353,31));
        var globalDay = global.Days.Values.Single();
        Check(globalDay.Sum.Total == 230 && globalDay.Apps["direct.exe"].Total == 31,"independent physical total and OS application counters");
        global.Break(); global.SampleGlobal(start.AddSeconds(4),G(999,0));
        global.SampleGlobal(start.AddSeconds(5),G(1000,2));
        Check(globalDay.Sum.Total == 231 && globalDay.Apps["direct.exe"].Total == 33,"global pause and resume");
        var map = new Dictionary<string,string> { ["node"]="Trojan", ["local"]="Direct", ["group"]="Selector" };
        Check(Collector.Classify("node",map)==Route.Proxy && Collector.Classify("local",map)==Route.Direct &&
              Collector.Classify("REJECT",map)==Route.Ignored && Collector.Classify("group",map)==Route.Unknown &&
              Collector.Classify(null,map)==Route.Unknown, "classify actual egress types");
        string dir = Path.Combine(Path.GetTempPath(),"vpn-ledger-check-"+Guid.NewGuid().ToString("N"));
        try { var store = new Store(dir); store.Save(ledger.Days); store.Save(ledger.Days);
              Check(store.Load().Values.Single().Evidence["unknown"].Clue == clue && store.Load().Values.Single().Sum == day.Sum, "evidence persistence round trip");
              File.WriteAllText(Path.Combine(dir,"days.json"),"{\"2026-01-02\":{\"Apps\":{\"legacy.exe\":{\"Up\":4,\"Down\":8}},\"Direct\":{\"Up\":3,\"Down\":5}}}");
              Check(store.Load().Values.Single().Sum.Total == 12 && store.Load().Values.Single().Evidence.Count == 0,"v1 daily history remains readable"); }
        finally { if (Directory.Exists(dir)) { File.Delete(Path.Combine(dir,"days.json")); Directory.Delete(dir); } }
        foreach (var ip in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
        {
            using var listener = new TcpListener(ip,0);
            listener.Start();
            using var client = new TcpClient(ip.AddressFamily);
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            await client.ConnectAsync(ip,port);
            using var server = await listener.AcceptTcpClientAsync();
            using var udp = new UdpClient(new IPEndPoint(ip,0));
            var owners = new Owners();
            var src = ((IPEndPoint)client.Client.LocalEndPoint!).Port;
            var expected = Process.GetCurrentProcess().ProcessName.ToLowerInvariant() + ".exe";
            Check(owners.Find(true,ip.ToString(),src,ip.ToString(),port).App == expected &&
                  owners.Find(true,ip.ToString(),src,ip.ToString(),port+1).App == Ledger.UnknownApp, "TCP exact tuple "+ip);
            Check(owners.Find(true,ip.ToString(),src,"",port,true).App == expected &&
                  owners.Find(true,ip.ToString(),src,"",port).App == Ledger.UnknownApp &&
                  owners.Find(true,ip.ToString(),src,"",port+1,true).App == Ledger.UnknownApp, "missing fake-IP requires explicit mode and peer port "+ip);
            Check(owners.Find(false,ip.ToString(),((IPEndPoint)udp.Client.LocalEndPoint!).Port,"",0).App == expected,"UDP owner "+ip);
        }
        if (Array.IndexOf(args,"--live") >= 0)
        {
            using var collector = new Collector();
            var flows = await collector.Read(CancellationToken.None);
            int proxy = flows.Count(f => f.Route == Route.Proxy);
            int known = flows.Count(f => f.Route == Route.Proxy && f.App != Ledger.UnknownApp);
            Console.WriteLine($"LIVE {flows.Count} connections, {proxy} proxied, {known} attributed, {flows.Count(f => f.Route == Route.Direct)} direct.");
            var end = DateTimeOffset.Now;
            var sample = await GlobalCollector.Read(end.AddMinutes(-10),end,true,CancellationToken.None);
            Check(sample.Adapters.Count > 0 && sample.Apps?.Count > 0,"live global adapter and Windows per-app usage");
            Console.WriteLine($"GLOBAL {sample.Adapters.Count} adapters, {sample.Apps!.Count} attributed rows.");
        }
    }
}
