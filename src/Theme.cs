using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace VpnLedger;

internal sealed class Theme
{
    public string Preference { get; private set; } = "system";
    private bool? last;
    private readonly string file;
    private static readonly (string Key, string Light, string Dark)[] Colors = {
        ("Surface","#FFFCF8","#222220"), ("Ink","#282720","#F4F1EB"), ("Muted","#706B62","#B6B1A7"),
        ("Line","#E6E0D8","#403E38"), ("Wash","#F3EFE9","#2E2D29"), ("Selected","#E6E0D7","#49443E"),
        ("Accent","#CE5244","#F28C81"), ("Tint","#FBE4DD","#46312D"), ("ChartLight","#F7AE9B","#A8675C")
    };
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd,int attr,ref int value,int size);
    public Theme(string folder)
    {
        file = Path.Combine(folder,"appearance.json");
        try
        {
            string? value = File.Exists(file) ? JsonSerializer.Deserialize<string>(File.ReadAllText(file)) : null;
            if (value is "light" or "dark") Preference = value;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
    }
    public void Choose(string preference)
    {
        Preference = preference;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file+".tmp",JsonSerializer.Serialize(preference));
        File.Move(file+".tmp",file,true);
    }
    public bool Apply(Window window)
    {
        bool dark = Preference == "dark" || (Preference == "system" &&
            Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize","AppsUseLightTheme",1) is 0);
        var hwnd = new WindowInteropHelper(window).Handle;
        if (last == dark && hwnd != IntPtr.Zero) return false;
        foreach (var (key,light,night) in Colors)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? night : light));
            brush.Freeze();
            window.Resources[key] = brush;
        }
        if (hwnd != IntPtr.Zero)
        {
            int enabled = dark ? 1 : 0;
            if (DwmSetWindowAttribute(hwnd,20,ref enabled,4) != 0) DwmSetWindowAttribute(hwnd,19,ref enabled,4);
            last = dark;
        }
        return true;
    }
}
