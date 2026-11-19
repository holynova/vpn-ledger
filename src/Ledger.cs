using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace VpnLedger;

public record Bytes(long Up = 0, long Down = 0)
{
    [System.Text.Json.Serialization.JsonIgnore] public long Total => Up + Down;
    public static Bytes operator +(Bytes a, Bytes b) => new(a.Up + b.Up, a.Down + b.Down);
    public Bytes Since(Bytes previous) => new(Math.Max(0, Up - previous.Up), Math.Max(0, Down - previous.Down));
}

public enum Route { Proxy, Direct, Unknown, Ignored }
public record Clue(string Source, string Target, string Transport, string Rule, string Reason);
public record Observation(Clue Clue, DateTimeOffset First, DateTimeOffset Last, Bytes Usage);
public record Flow(string Id, DateTimeOffset Start, Route Route, string App, string? Path, Bytes Counter, Clue? Clue = null);
public record PointRate(DateTimeOffset End, double Seconds, Bytes Bytes);
public sealed class Day
{
    public Dictionary<string, Bytes> Apps { get; set; } = new();
    public Dictionary<string, Observation> Evidence { get; set; } = new();
    public Dictionary<string, Bytes>? Interfaces { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public Bytes Sum => (Interfaces ?? Apps).Values.Aggregate(new Bytes(), (sum, n) => sum + n);
    public void Observe(string id, Clue clue, DateTimeOffset now, Bytes delta)
    {
        if (Evidence.TryGetValue(id, out var old))
            Evidence[id] = old with { Last = now, Usage = old.Usage + delta };
        else
        {
            if (Evidence.Count >= 80) Evidence.Remove(Evidence.MinBy(p => p.Value.Last).Key);
            Evidence[id] = new(clue,now,now,delta);
        }
    }
}

public sealed class Ledger
{
    public const string UnknownApp = "未识别应用";
    public SortedDictionary<string, Day> Days { get; }
    public Dictionary<string, string> Paths { get; } = new();
    public Dictionary<string, Bytes> Rates { get; private set; } = new();
    public List<PointRate> Points { get; } = new();
    public double Interval { get; private set; } = 1;
    private Dictionary<string, Flow> previous = new();
    public IEnumerable<Flow> ActiveUnknown => previous.Values.Where(f => f.App == UnknownApp && f.Clue != null);
    private DateTimeOffset? checkpoint;
    private Dictionary<string, AdapterReading> adapters = new();
    private Dictionary<string, Bytes> reportedApps = new();

    public Ledger(SortedDictionary<string, Day> days) => Days = days;
    public void Break()
    {
        previous.Clear();
        checkpoint = null;
        Rates.Clear();
        adapters.Clear();
        reportedApps.Clear();
    }
    public void Sample(DateTimeOffset now, List<Flow> flows)
    {
        var next = new Dictionary<string, Flow>();
        Rates = new();
        var key = now.LocalDateTime.ToString("yyyy-MM-dd");
        var elapsed = checkpoint.HasValue ? (now - checkpoint.Value).TotalSeconds : 0;
        bool contiguous = elapsed > 0 && elapsed <= 8 && checkpoint!.Value.LocalDateTime.Date == now.LocalDateTime.Date;
        if (!Days.TryGetValue(key, out var day)) Days[key] = day = new();
        while (Days.Count > 30) Days.Remove(Days.Keys.First());
        var total = new Bytes();
        foreach (var item in flows)
        {
            if (item.Route != Route.Proxy) continue;
            var flow = item;
            if (previous.TryGetValue(flow.Id, out var old) && flow.App == UnknownApp && old.App != UnknownApp)
                flow = flow with { App = old.App, Path = old.Path };
            if (!next.TryAdd(flow.Id, flow)) continue;
            if (flow.Path != null) Paths[flow.App] = flow.Path;
            if (!contiguous || (old == null && flow.Start < checkpoint!.Value)) continue;
            var delta = flow.Counter.Since(old?.Counter ?? new Bytes());
            if (delta.Total == 0) continue;
            day.Apps[flow.App] = day.Apps.GetValueOrDefault(flow.App, new()) + delta;
            Rates[flow.App] = Rates.GetValueOrDefault(flow.App, new()) + delta;
            total += delta;
            if (flow.App == UnknownApp && flow.Clue != null) day.Observe(flow.Id,flow.Clue,now,delta);
        }
        Interval = contiguous ? elapsed : 1;
        if (contiguous) Points.Add(new(now, elapsed, total));
        Points.RemoveAll(p => p.End <= now.AddMinutes(-15));
        previous = next;
        checkpoint = now;
    }
    public bool IsContinuous(DateTimeOffset now) => checkpoint.HasValue && now > checkpoint.Value &&
        now - checkpoint.Value <= TimeSpan.FromSeconds(8) && now.LocalDateTime.Date == checkpoint.Value.LocalDateTime.Date;

    public void SampleGlobal(DateTimeOffset now, GlobalSnapshot snapshot)
    {
        bool continuous = IsContinuous(now);
        string key = now.LocalDateTime.ToString("yyyy-MM-dd");
        if (!Days.TryGetValue(key,out var day)) Days[key] = day = new();
        day.Interfaces ??= new();
        while (Days.Count > 30) Days.Remove(Days.Keys.First());
        Interval = continuous ? (now - checkpoint!.Value).TotalSeconds : 1;
        var total = new Bytes();
        foreach (var (id, nic) in snapshot.Adapters)
        {
            if (!continuous || !adapters.TryGetValue(id,out var old)) continue;
            var delta = nic.Counter.Since(old.Counter);
            total += delta;
            day.Interfaces[nic.Name] = day.Interfaces.GetValueOrDefault(nic.Name,new()) + delta;
        }
        if (!continuous) reportedApps.Clear();
        if (continuous && snapshot.Apps != null)
        {
            foreach (var app in snapshot.Apps)
            {
                var prior = reportedApps.GetValueOrDefault(app.Id,new());
                var delta = app.Counter.Since(prior);
                reportedApps[app.Id] = prior + delta;
                if (delta.Total == 0) continue;
                day.Apps[app.Name] = day.Apps.GetValueOrDefault(app.Name,new()) + delta;
                if (app.Name == UnknownApp) day.Observe(app.Id,new(app.Adapter,app.Identity,"Windows 应用用量","",
                    "Windows 未提供可识别的软件名称。此数据源只提供按网络汇总的用量。"),now,delta);
            }
        }
        Rates.Clear();
        if (continuous) { Points.Add(new(now,Interval,total)); Rates["network"] = total; }
        Points.RemoveAll(p => p.End <= now.AddMinutes(-15));
        adapters = snapshot.Adapters;
        checkpoint = now;
    }
}

public sealed class Store
{
    private readonly string file;
    public Store(string folder, string filename = "days.json") => file = Path.Combine(folder, filename);
    public SortedDictionary<string, Day> Load()
    {
        if (!File.Exists(file)) return new();
        var data = JsonSerializer.Deserialize<SortedDictionary<string, Day>>(File.ReadAllBytes(file));
        if (data == null || data.Any(p => p.Value?.Apps == null || p.Value.Evidence == null ||
              p.Value.Apps.Values.Any(a => a == null) || p.Value.Evidence.Values.Any(o => o?.Clue == null || o.Usage == null)))
            throw new InvalidDataException("记录格式无效。");
        return data;
    }
    public void Save(SortedDictionary<string, Day> days)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file + ".tmp", JsonSerializer.SerializeToUtf8Bytes(days));
        File.Move(file + ".tmp", file, true);
    }
}
