using System;
using System.Threading;
using System.Windows;

namespace VpnLedger;

internal static class App
{
    [STAThread]
    public static void Main()
    {
        using var singleton = new Mutex(true, @"Local\VpnLedger.Desktop.v1", out bool first);
        using var show = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\VpnLedger.Show.v1");
        if (!first) { show.Set(); return; }
        var application = new Application();
        var window = new MainWindow();
        var registration = ThreadPool.RegisterWaitForSingleObject(show, (_, _) =>
            application.Dispatcher.BeginInvoke(window.Reveal), null, -1, false);
        try { application.Run(window); }
        finally { registration.Unregister(null); }
    }
}
