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
        ("Surface","#F7F1E3","#23251F"), ("Ink","#302F1D","#F4F1EB"), ("Muted","#68644F","#B6B1A7"),
        ("Line","#C4B99C","#403E38"), ("Wash","#F1EAD9","#2E2D29"), ("Selected","#D9D5BD","#49443E"),
        ("Accent","#566335","#BACD8D"), ("Tint","#FBE4DD","#46312D"), ("ChartLight","#D6DAB9","#424C35"),
        ("Olive","#586638","#435337"), ("Clay","#A85C45","#804B40"), ("Ochre","#E0AD4C","#D6A756"),
        ("Bean","#D2DA98","#AEBF84"), ("Sand","#D8C9AA","#C6B99C"),
        ("MapLight","#FFF9E9","#FFFAEE"), ("MapDark","#302F19","#25281B")
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
