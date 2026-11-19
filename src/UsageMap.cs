using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace VpnLedger;

internal record MapItem(string? Key, string Name, Bytes Usage);
internal record TileLabel(string Name, string Symbol, ImageSource? Icon, string Amount, string Share, bool Leading);

public partial class MainWindow
{
    private bool showRanking;
    private string? selection;
    private readonly List<Button> mapButtons = new();

    internal static List<MapItem> MapItems(Day day)
    {
        var apps = day.Apps.Where(p => p.Value.Total > 0)
            .OrderByDescending(p => p.Value.Total).ThenBy(p => p.Key, StringComparer.Ordinal).ToList();
        var individual = apps.Take(apps.Count > 4 ? 3 : apps.Count).ToList();
        if (apps.Count > 4 && day.Apps.TryGetValue(Ledger.UnknownApp,out var unknown) && unknown.Total > 0 &&
            individual.All(p => p.Key != Ledger.UnknownApp))
            individual[^1] = new(Ledger.UnknownApp,unknown);
        var result = individual
            .Select(p => new MapItem(p.Key,AppNames.GetValueOrDefault(p.Key,p.Key),p.Value)).ToList();
        if (apps.Count > individual.Count)
        {
            var rest = apps.Except(individual).Aggregate(new Bytes(),(sum,p) => sum + p.Value);
            result.Add(new(null,$"其他 {apps.Count - individual.Count} 项",rest));
        }
        return result;
    }
    private void RenderMap(Day day)
    {
        var items = MapItems(day);
        long total = items.Sum(i => i.Usage.Total);
        if (selection == null || !day.Apps.ContainsKey(selection))
            selection = items.Find(i => i.Key == Ledger.UnknownApp)?.Key ?? items.FirstOrDefault()?.Key;
        if (mapButtons.Count != items.Count)
        {
            Tiles.Children.Clear(); Tiles.ColumnDefinitions.Clear(); Tiles.RowDefinitions.Clear(); mapButtons.Clear();
            for (int i = 0; i < Math.Min(2,items.Count); i++) Tiles.RowDefinitions.Add(new());
            int columns = Math.Max(1,items.Count-1);
            for (int i = 0; i < columns; i++) Tiles.ColumnDefinitions.Add(new());
            for (int i = 0; i < items.Count; i++)
            {
                var button = new Button { Style = (Style)FindResource("TileButton") };
                button.Click += TileClick;
                Grid.SetRow(button,i == 0 ? 0 : 1);
                Grid.SetColumn(button,Math.Max(0,i-1));
                Grid.SetColumnSpan(button,i == 0 ? columns : 1);
                Tiles.Children.Add(button); mapButtons.Add(button);
            }
        }
        if (items.Count > 0) Tiles.RowDefinitions[0].Height = new((double)items[0].Usage.Total / total,GridUnitType.Star);
        if (items.Count > 1) Tiles.RowDefinitions[1].Height = new((double)(total-items[0].Usage.Total)/total,GridUnitType.Star);
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i]; var button = mapButtons[i];
            if (i > 0) Tiles.ColumnDefinitions[i-1].Width = new((double)item.Usage.Total/total,GridUnitType.Star);
            string color = item.Key == Ledger.UnknownApp ? "Bean" : item.Key == null ? "Sand" : i switch { 0 => "Olive", 1 => "Clay", _ => "Ochre" };
            button.SetResourceReference(BackgroundProperty,color);
            button.SetResourceReference(ForegroundProperty,color is "Bean" or "Ochre" or "Sand" ? "MapDark" : "MapLight");
            button.BorderBrush = selection == item.Key && selection != null ? (Brush)Resources["Ink"] : Brushes.Transparent;
            var icon = item.Key == null ? null : icons.Get(ledger.Paths.GetValueOrDefault(item.Key));
            string symbol = icon != null ? "" : item.Key == Ledger.UnknownApp ? "◇" : item.Key == null ? "⋯" : item.Name[..1].ToUpperInvariant();
            var label = new TileLabel(item.Name,symbol,icon,Fmt.Size(item.Usage.Total),$"{(double)item.Usage.Total/total:P0}",i == 0);
            if (!Equals(button.DataContext,label)) button.DataContext = label;
            button.Tag = item;
            button.ToolTip = $"{label.Name}　{label.Amount}（{label.Share}）";
            AutomationProperties.SetName(button,item.Name);
        }
        Tiles.Visibility = showRanking ? Visibility.Collapsed : Visibility.Visible;
        RankingView.Visibility = showRanking ? Visibility.Visible : Visibility.Collapsed;
        SectionTitle.Text = showRanking ? "应用排行" : "用量分布";
        ViewSwitch.Content = showRanking ? "返回分布图" : "全部应用 ›";
        SortTotal.Visibility = showRanking ? Visibility.Visible : Visibility.Collapsed;
        SortSpeed.Visibility = showRanking && !global ? Visibility.Visible : Visibility.Collapsed;
        VpnMode.SetResourceReference(ForegroundProperty,global ? "Ink" : "Surface");
        GlobalMode.SetResourceReference(ForegroundProperty,global ? "Surface" : "Ink");
        VpnMode.Background = global ? Brushes.Transparent : (Brush)Resources["Accent"];
        GlobalMode.Background = global ? (Brush)Resources["Accent"] : Brushes.Transparent;
        Drawer.Visibility = selection != null ? Visibility.Visible : Visibility.Collapsed;
        if (selection == null) return;
        var use = day.Apps[selection];
        DrawerName.Text = AppNames.GetValueOrDefault(selection,selection);
        DrawerDown.Text = Fmt.Size(use.Down); DrawerUp.Text = Fmt.Size(use.Up);
        bool unknown = selection == Ledger.UnknownApp;
        var evidence = unknown ? day.Evidence.Values.MaxBy(o => o.Last) : null;
        TargetLabel.Text = unknown ? (global ? "网络" : "目标") : "进程";
        TargetValue.Text = unknown ? (global ? evidence?.Clue.Source : evidence?.Clue.Target) ?? "暂无线索" : selection;
        MetaLabel.Text = unknown ? (global ? "数据来源" : "协议") : "用量占比";
        MetaValue.Text = unknown ? (global ? "Windows" : evidence?.Clue.Transport ?? "—") : $"{(double)use.Total/Math.Max(1,total):P1}";
        WhenLabel.Text = unknown ? "最近观测" : "统计范围";
        WhenValue.Text = unknown ? evidence?.Last.ToLocalTime().ToString("HH:mm:ss") ?? "—" : global ? "全局" : "VPN";
        TargetValue.ToolTip = TargetValue.Text; MetaValue.ToolTip = MetaValue.Text; DrawerName.ToolTip = DrawerName.Text;
    }
    private void TileClick(object sender,RoutedEventArgs e)
    {
        var item = (MapItem)((Button)sender).Tag;
        if (item.Key == null) showRanking = true;
        else selection = item.Key;
        Render();
    }
    private void ViewClick(object sender,RoutedEventArgs e) { showRanking = !showRanking; Render(); }
    private void DetailsClick(object sender,RoutedEventArgs e)
    {
        if (selection != null) AppPage(ledger,Today,selection);
    }
}
