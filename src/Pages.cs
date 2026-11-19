using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace VpnLedger;

public partial class MainWindow
{
    private sealed record View(string Title,Action Build);
    private readonly Stack<View> navigation = new();
    private void OpenPage(string title,Action build)
    {
        navigation.Push(new(title,build));
        Dashboard.Visibility = Visibility.Collapsed;
        Page.Visibility = Visibility.Visible;
        DrawPage();
        Back.Focus();
    }
    private void DrawPage()
    {
        var view = navigation.Peek();
        PageTitle.Text = view.Title;
        RefreshPage.Visibility = view.Title == "设置" ? Visibility.Collapsed : Visibility.Visible;
        PageContent.Children.Clear();
        view.Build();
        PageScroll.ScrollToTop();
    }
    private void RefreshClick(object sender,RoutedEventArgs e) => DrawPage();
    private void BackClick(object sender,RoutedEventArgs e)
    {
        navigation.Pop();
        if (navigation.Count > 0) DrawPage();
        else { Page.Visibility = Visibility.Collapsed; Dashboard.Visibility = Visibility.Visible; Render(); }
    }
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && navigation.Count > 0) { BackClick(this,e); e.Handled = true; }
        base.OnPreviewKeyDown(e);
    }
    private TextBlock Label(string text,bool muted = false,double size = 13)
    {
        var label = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
        label.SetResourceReference(TextBlock.ForegroundProperty,muted ? "Muted" : "Ink");
        return label;
    }
    private void Note(string text)
    {
        var label = Label(text,true,12); label.Margin = new(0,8,0,12); label.LineHeight = 19;
        PageContent.Children.Add(label);
    }
    private void Heading(string text)
    {
        var label = Label(text,false,14); label.FontWeight = FontWeights.SemiBold; label.Margin = new(0,14,0,4);
        PageContent.Children.Add(label);
    }
    private void Entry(string title,string description,string value,Action? click = null)
    {
        var grid = new Grid { Margin = new(2,9,2,9) };
        grid.ColumnDefinitions.Add(new());
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var left = new StackPanel { Margin = new(0,0,10,0) };
        var name = Label(title,false,14); name.FontWeight = FontWeights.SemiBold; left.Children.Add(name);
        if (description.Length > 0) { var sub = Label(description,true,12); sub.Margin = new(0,3,0,0); left.Children.Add(sub); }
        grid.Children.Add(left);
        var amount = Label(value,false,14); amount.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(amount,1); grid.Children.Add(amount);
        var rule = new Border { BorderThickness = new(0,0,0,1), Child = grid };
        rule.SetResourceReference(Border.BorderBrushProperty,"Line");
        if (click == null) { PageContent.Children.Add(rule); return; }
        var button = new Button { Content = rule, Padding = new(2,0,2,0), HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(button,title);
        button.Click += (_,_) => click();
        PageContent.Children.Add(button);
    }
    private void HistoryClick(object sender,RoutedEventArgs e) => OpenPage("历史记录",() =>
    {
        var dates = vpnLedger.Days.Keys.Concat(globalLedger.Days.Keys).Distinct().OrderByDescending(d => d).ToList();
        if (dates.Count == 0) Note("有用量记录后，可在这里按日期查看。");
        foreach (var date in dates)
        {
            Heading(date);
            foreach (bool all in new[] { false,true })
            {
                var book = all ? globalLedger : vpnLedger;
                if (!book.Days.TryGetValue(date,out var day)) continue;
                string mode = all ? "全局" : "VPN";
                Entry(mode + " 用量",$"↓ {Fmt.Size(day.Sum.Down)}　↑ {Fmt.Size(day.Sum.Up)}",
                    Fmt.Size(day.Sum.Total) + "   ›",() => DayPage(book,date,all));
            }
        }
    });
    private void DayPage(Ledger book,string date,bool all) => OpenPage(date,() =>
    {
        if (!book.Days.TryGetValue(date,out var day)) { Note("此日期已移出最近 30 天记录。"); return; }
        Entry(all ? "全局用量" : "VPN 用量",$"↓ 下载 {Fmt.Size(day.Sum.Down)}　↑ 上传 {Fmt.Size(day.Sum.Up)}",Fmt.Size(day.Sum.Total));
        if (all && day.Interfaces != null)
        {
            Heading("网卡"); foreach (var pair in day.Interfaces.OrderByDescending(x=>x.Value.Total))
                Entry(pair.Key,"",Fmt.Size(pair.Value.Total));
        }
        Heading("应用排行");
        if (day.Apps.Count == 0) Note("当日暂无可用的应用记录。");
        foreach (var (key,usage) in day.Apps.OrderByDescending(x => x.Value.Total))
            Entry(AppNames.GetValueOrDefault(key,key),$"↓ {Fmt.Size(usage.Down)}　↑ {Fmt.Size(usage.Up)}",
                Fmt.Size(usage.Total)+"   ›",() => AppPage(book,date,key));
    });
    private void AppClick(object sender,RoutedEventArgs e)
    {
        if (sender is Button { DataContext: RankingRow row }) { selection = row.Key; AppPage(ledger,Today,row.Key); }
    }
    private void AppPage(Ledger book,string date,string key) => OpenPage(key == Ledger.UnknownApp ? "未识别应用 · 连接线索" : "应用详情",() =>
    {
        var day = book.Days.GetValueOrDefault(date);
        var usage = day?.Apps.GetValueOrDefault(key) ?? new();
        Entry(AppNames.GetValueOrDefault(key,key),$"{date}   ·   {(book == globalLedger ? "全局" : "VPN")}",Fmt.Size(usage.Total));
        Entry("下载","",Fmt.Size(usage.Down)); Entry("上传","",Fmt.Size(usage.Up));
        if (key != Ledger.UnknownApp)
        {
            Heading("进程"); Note(book.Paths.GetValueOrDefault(key,key)); return;
        }
        var observations = day?.Evidence.Values.OrderByDescending(o => o.Last).ToArray() ?? Array.Empty<Observation>();
        if (date == Today && book == vpnLedger && !global && !paused)
        {
            var active = book.ActiveUnknown.OrderByDescending(f => f.Counter.Total).ToList();
            if (active.Count > 0)
            {
                Heading($"当前连接 · {active.Count} 条");
                foreach (var f in active)
                {
                    var c = f.Clue!;
                    Entry(c.Target,$"{c.Transport}　{c.Rule}\n来源　{c.Source}\n连接开始　{f.Start.ToLocalTime():HH:mm:ss}","活动中");
                }
            }
        }
        Heading("已记录线索");
        if (observations.Length == 0) { Note("这笔用量没有保存连接线索。旧版账本仅保留合计，无法还原当时的连接。"); return; }
        long explained = observations.Sum(o => o.Usage.Total);
        Note($"最近 {observations.Length} 条，共 {Fmt.Size(explained)}。每个日期最多保留 80 条。"+
            (usage.Total > explained ? $"另有 {Fmt.Size(usage.Total-explained)} 仅保留用量。" : ""));
        foreach (var o in observations)
        {
            var c = o.Clue;
            Entry(c.Target,$"{c.Transport}　{c.Rule}\n来源　{c.Source}\n观测　{o.First:HH:mm:ss} – {o.Last:HH:mm:ss}\n↓ {Fmt.Size(o.Usage.Down)}　↑ {Fmt.Size(o.Usage.Up)}\n{c.Reason}",Fmt.Size(o.Usage.Total));
        }
    });
    private void SettingsClick(object sender,RoutedEventArgs e) => OpenPage("设置",() =>
    {
        Heading("外观");
        foreach (var (key,name) in new[] { ("system","跟随系统"),("light","浅色"),("dark","深色") })
            Entry(name,"",theme.Preference == key ? "✓" : "",() => {
                try { theme.Choose(key); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { storageError = "外观偏好暂时无法保存。"; }
                theme.Apply(this); range.Background = Selected; Render(); DrawPage();
            });
        Heading("统计方式");
        Note("VPN 仅累计经 Mihomo 代理出口的连接，每秒采样，短连接可能漏记。点击未识别应用可查看近期端点、协议和观测时间。");
        Note("全局模式记录本机物理网卡的收发量，包括直连、VPN 出口和局域网。应用排行由 Windows 延迟提供，代理转发可能归在内核进程下。网卡与应用采用不同计数来源。");
        Heading("本机记录");
        Note("两种模式分别保存最近 30 个有记录的日期。切换模式后从当前时刻开始记录；每次启动默认监控 VPN。");
        Entry("退出账本","保存本次用量并停止采集","退出",() => { quitting = true; Close(); });
    });
}
