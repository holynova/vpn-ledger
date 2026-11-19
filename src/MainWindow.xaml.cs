using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace VpnLedger;

public partial class MainWindow : Window
{
    private static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VpnLedger");
    private readonly Store vpnStore = new(Folder), globalStore = new(Folder,"global-days.json");
    private readonly Theme theme = new(Folder);
    private readonly HashSet<Store> readOnly = new();
    private readonly HashSet<Store> dirty = new();
    private readonly Ledger vpnLedger, globalLedger;
    private bool global;
    private Ledger ledger => global ? globalLedger : vpnLedger;
    private DateTimeOffset globalSince, nextReport;
    private string? reportNotice;
    private readonly Collector collector = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly Forms.NotifyIcon tray;
    private readonly AppIcons icons = new();
    private readonly ObservableCollection<RankingRow> ranking = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool paused, busy, closing, quitting, bySpeed, connected;
    private string? lastError, storageError;
    private int generation;
    private DateTime nextSave;
    private Button range;
    private static string Today => DateTime.Now.ToString("yyyy-MM-dd");
    private Brush Selected => (Brush)Resources["Selected"];
    private static readonly Dictionary<string,string> AppNames = new(StringComparer.OrdinalIgnoreCase) {
        ["chrome.exe"]="Google Chrome", ["msedge.exe"]="Microsoft Edge", ["firefox.exe"]="Firefox",
        ["code.exe"]="Visual Studio Code", ["msedgewebview2.exe"]="Edge WebView2", ["telegram.exe"]="Telegram",
        ["codex.exe"]="Codex", ["wechat.exe"]="微信", ["weixin.exe"]="微信", ["powershell.exe"]="Windows PowerShell",
        ["pwsh.exe"]="PowerShell", ["curl.exe"]="curl", ["chatgpt.exe"]="ChatGPT",
        ["svchost.exe"]="Windows 服务主机", ["lsass.exe"]="Windows 本地安全机构"
    };
    public MainWindow()
    {
        InitializeComponent();
        Width = Math.Min(Width,SystemParameters.WorkArea.Width);
        Height = Math.Min(Height,SystemParameters.WorkArea.Height);
        List.ItemsSource = ranking;
        vpnLedger = LoadLedger(vpnStore);
        globalLedger = LoadLedger(globalStore);
        theme.Apply(this);
        SourceInitialized += (_,_) => theme.Apply(this);
        range = DefaultRange;
        Chart.Samples = ledger.Points;
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开 VPN 账本", null, (_,_) => Reveal());
        menu.Items.Add("退出并保存", null, (_,_) => { quitting = true; Close(); });
        tray = new Forms.NotifyIcon {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application,
            Text = "VPN 流量账本", ContextMenuStrip = menu, Visible = true
        };
        tray.DoubleClick += (_,_) => Reveal();
        menu.Items[0].Font = new System.Drawing.Font(menu.Font, System.Drawing.FontStyle.Bold);
        timer.Tick += async (_,_) => await Tick();
        Loaded += async (_,_) => { Render(); timer.Start(); await Tick(); };
        Closing += OnClosing;
        Application.Current.SessionEnding += (_,_) => { quitting = true; Save(); };
    }
    private Ledger LoadLedger(Store store)
    {
        try { return new(store.Load()); }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { readOnly.Add(store); return new(new()); }
    }
    public void Reveal()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate(); Render();
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!quitting)
        {
            e.Cancel = true; Hide();
            tray.ShowBalloonTip(2200, "VPN 账本已收起", "双击托盘图标打开，右键可退出并保存。", Forms.ToolTipIcon.Info);
            return;
        }
        closing = true;
        timer.Stop(); lifetime.Cancel(); collector.Dispose(); Save();
        tray.Visible = false; tray.ContextMenuStrip?.Dispose(); tray.Icon?.Dispose(); tray.Dispose();
    }
    private void Save()
    {
        storageError = null;
        foreach (var store in dirty.ToArray())
        {
            if (readOnly.Contains(store)) continue;
            try { store.Save((store == vpnStore ? vpnLedger : globalLedger).Days); dirty.Remove(store); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { storageError = "记录暂时无法保存到本机，稍后会重试。"; }
        }
    }
    private async Task Tick()
    {
        if (theme.Apply(this)) { range.Background = Selected; Render(); }
        if (busy || closing) return;
        busy = true;
        int version = generation;
        try
        {
            if (!paused)
            {
                if (global)
                {
                    var now = DateTimeOffset.Now;
                    if (!ledger.IsContinuous(now)) { globalSince = now; nextReport = now.AddSeconds(2); reportNotice = null; }
                    bool query = now >= nextReport;
                    var snapshot = await Task.Run(() => GlobalCollector.Read(globalSince,now,query,lifetime.Token));
                    if (closing || version != generation) return;
                    ledger.SampleGlobal(now,snapshot);
                    connected = snapshot.Adapters.Count > 0;
                    if (query) nextReport = now.AddSeconds(8);
                    if (query || snapshot.Notice != null) reportNotice = snapshot.Notice;
                    lastError = reportNotice;
                }
                else
                {
                    var flows = await Task.Run(() => collector.Read(lifetime.Token));
                    if (closing || version != generation) return;
                    ledger.Sample(DateTimeOffset.Now, flows);
                    connected = true; lastError = null;
                }
                dirty.Add(global ? globalStore : vpnStore);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Net.Http.HttpRequestException
            or OperationCanceledException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException
            or System.Net.NetworkInformation.NetworkInformationException)
        {
            if (closing || version != generation) return;
            if (!global) collector.Dispose();
            ledger.Break(); connected = false;
            lastError = global ? "无法读取 Windows 网络用量，稍后会重试。" : e is IOException && e.Message.Contains("Mihomo") ? e.Message : "无法读取本机 Mihomo 连接。内核运行后将自动重连。";
        }
        finally { busy = false; }
        if (closing) return;
        if (DateTime.UtcNow >= nextSave) { Save(); nextSave = DateTime.UtcNow.AddSeconds(10); }
        tray.Text = $"{(global ? "全局" : "VPN")} · {(paused ? "已暂停" : connected ? "记录中" : "正在连接")} · 今日 {Fmt.Size(ledger.Days.GetValueOrDefault(Today)?.Sum.Total ?? 0)}";
        if (IsVisible && WindowState != WindowState.Minimized) Render();
    }
    private void Render()
    {
        var day = ledger.Days.GetValueOrDefault(Today) ?? new Day();
        var sum = day.Sum;
        string[] value = Fmt.Size(sum.Total).Split(' ');
        Total.Text = value[0]; Unit.Text = value[1]; Up.Text = Fmt.Size(sum.Up); Down.Text = Fmt.Size(sum.Down);
        UsageLabel.Text = global ? "今日全局用量" : "今日 VPN 用量";
        Status.Text = paused ? "已暂停" : connected ? "记录中" : lastError == null ? "正在连接" : global ? "等待网络" : "等待内核";
        StatusDot.Fill = (Brush)Resources[connected && !paused ? "Accent" : "Muted"];
        string? message = readOnly.Contains(global ? globalStore : vpnStore) ? "历史文件无法读取。本次仅在内存记录，原文件已保留。" :
            storageError ?? (paused ? "已暂停采集，点击「继续」恢复记录。" : lastError);
        Notice.Visibility = message == null ? Visibility.Collapsed : Visibility.Visible;
        NoticeText.Text = message;
        bool useSpeed = !global && bySpeed;
        SortTotal.Background = useSpeed ? Brushes.Transparent : Selected;
        SortSpeed.Background = useSpeed ? Selected : Brushes.Transparent;
        long rate = ledger.Rates.Values.Sum(n => n.Total);
        Speed.Text = Fmt.Size(rate / ledger.Interval) + "/s";
        Chart.InvalidateVisual();
        Count.Text = day.Apps.Count + (global ? " 项 · 系统延迟更新" : " 项");
        var rows = day.Apps.Select(kv => {
            double speed = ledger.Rates.GetValueOrDefault(kv.Key, new()).Total / ledger.Interval;
            double metric = useSpeed ? speed : kv.Value.Total;
            string name = AppNames.GetValueOrDefault(kv.Key, kv.Key);
            return new { Key = kv.Key, Name = name, Bytes = kv.Value, Metric = metric };
        }).OrderByDescending(a => a.Metric).ThenByDescending(a => a.Bytes.Total).ThenBy(a => a.Key).ToArray();
        double largest = Math.Max(1, rows.FirstOrDefault()?.Metric ?? 0);
        var existing = ranking.ToDictionary(a => a.Key);
        for (int i = 0; i < rows.Length; i++)
        {
            var a = rows[i];
            var icon = icons.Get(ledger.Paths.GetValueOrDefault(a.Key));
            string initial = icon != null || a.Name.Length == 0 ? "" : a.Name[..1].ToUpperInvariant();
            var data = new RowData(a.Key == Ledger.UnknownApp ? a.Name + "  ›" : a.Name, initial, icon, 136 * a.Metric / largest,
                Fmt.Size(a.Metric) + (useSpeed ? "/s" : ""),
                $"{a.Key}\n↓ 下载 {Fmt.Size(a.Bytes.Down)}　↑ 上传 {Fmt.Size(a.Bytes.Up)}");
            if (!existing.TryGetValue(a.Key, out var row)) { ranking.Insert(i, new(a.Key, data)); continue; }
            if (ranking[i] != row) ranking.Move(ranking.IndexOf(row),i);
            row.Update(data);
        }
        while (ranking.Count > rows.Length) ranking.RemoveAt(ranking.Count - 1);
        Empty.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = global ? "等待 Windows 应用记录" : "等待 VPN 流量";
        EmptyHint.Text = global ? "网卡速度实时更新，应用排行随后显示" : "产生代理流量后，应用会显示在这里";
        RenderMap(day);
    }
    private void RangeClick(object sender, RoutedEventArgs e)
    {
        range.Background = Brushes.Transparent; range = (Button)sender; range.Background = Selected;
        Chart.Minutes = int.Parse((string)range.Tag); Chart.InvalidateVisual();
    }
    private void SortClick(object sender, RoutedEventArgs e) { bySpeed = sender == SortSpeed; Render(); }
    private void PauseClick(object sender, RoutedEventArgs e)
    {
        paused = !paused; generation++; ledger.Break(); Pause.Content = paused ? "继续" : "暂停";
        Save(); Render();
    }
    private void ModeClick(object sender, RoutedEventArgs e)
    {
        bool next = sender == GlobalMode;
        if (global == next) return;
        Save();
        generation++;
        ledger.Break();
        global = next; selection = null;
        ledger.Break();
        connected = false; lastError = null; reportNotice = null;
        Chart.Samples = ledger.Points;
        Render();
        _ = Tick();
    }
}
