using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VpnLedger;

internal sealed record RowData(string Name, string Initial, ImageSource? Icon, double Bar, string Amount, string Detail);
internal sealed class RankingRow : INotifyPropertyChanged
{
    public string Key { get; }
    public RowData Data { get; private set; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public RankingRow(string key, RowData data) => (Key, Data) = (key, data);
    public void Update(RowData next)
    {
        if (Data == next) return;
        Data = next;
        PropertyChanged?.Invoke(this,new(nameof(Data)));
    }
}

internal static class Fmt
{
    public static string Size(double n)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        int i = 0;
        while (n >= 1000 && i < units.Length - 1) { n /= 1000; i++; }
        return $"{n.ToString(i == 0 ? "0" : "0.#")} {units[i]}";
    }
}

internal sealed class AppIcons
{
    private readonly Dictionary<string, ImageSource?> cache = new(StringComparer.OrdinalIgnoreCase);
    public ImageSource? Get(string? path)
    {
        if (path == null || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\") || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        if (cache.TryGetValue(path, out var saved)) return saved;
        ImageSource? image = null;
        try
        {
            if (File.Exists(path))
            {
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (icon != null)
                {
                    image = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(24,24));
                    image.Freeze();
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception) { }
        return cache[path] = image;
    }
}
