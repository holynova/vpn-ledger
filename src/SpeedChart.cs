using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace VpnLedger;

public sealed class SpeedChart : FrameworkElement
{
    public int Minutes = 15;
    public List<PointRate> Samples = new();
    private double[] up = Array.Empty<double>(), down = Array.Empty<double>();
    private DateTimeOffset end;
    public SpeedChart()
    {
        ToolTip = "等待采样";
        MouseMove += Hover;
    }
    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth < 1 || ActualHeight < 1) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        int n = Math.Max(1, (int)(ActualWidth / 5));
        up = new double[n]; down = new double[n];
        end = DateTimeOffset.Now;
        double bucket = Minutes * 60.0 / n;
        var start = end.AddMinutes(-Minutes);
        var coverage = new double[n];
        foreach (var p in Samples)
        {
            double hi = (p.End - start).TotalSeconds;
            double lo = hi - p.Seconds;
            if (hi <= 0 || lo >= n * bucket) continue;
            for (int i = Math.Max(0,(int)Math.Floor(lo / bucket)); i < n && i * bucket < hi; i++)
            {
                double overlap = Math.Min(hi, (i + 1) * bucket) - Math.Max(lo, i * bucket);
                coverage[i] += overlap;
                up[i] += p.Bytes.Up * overlap / p.Seconds;
                down[i] += p.Bytes.Down * overlap / p.Seconds;
            }
        }
        double peak = 1;
        for (int i = 0; i < n; i++)
            if (coverage[i] > 0) { up[i] /= coverage[i]; down[i] /= coverage[i]; peak = Math.Max(peak, up[i] + down[i]); }
        double floor = ActualHeight - 1, step = ActualWidth / n;
        dc.DrawLine(new Pen((Brush)FindResource("Line"),1), new(0, floor), new(ActualWidth, floor));
        var pale = (Brush)FindResource("ChartLight");
        var coral = (Brush)FindResource("Accent");
        for (int i = 0; i < n; i++)
        {
            double h = (up[i] + down[i]) / peak * (floor - 4);
            if (h <= 0) continue;
            h = Math.Max(2, h);
            double x = i * step, w = Math.Max(1,step - 1.4);
            dc.DrawRoundedRectangle(pale, null, new(x, floor - h, w, h), 1, 1);
            double d = h * down[i] / (up[i] + down[i]);
            if (d > 0) dc.DrawRoundedRectangle(coral, null, new(x, floor - d, w, d), 1, 1);
        }
    }
    private void Hover(object sender, MouseEventArgs args)
    {
        if (up.Length == 0 || ActualWidth == 0) return;
        int i = Math.Clamp((int)(args.GetPosition(this).X * up.Length / ActualWidth), 0, up.Length - 1);
        var at = end.AddMinutes(-Minutes * (1.0 - (double)i / up.Length));
        ToolTip = $"{at:HH:mm:ss}　↓ {Fmt.Size(down[i])}/s　↑ {Fmt.Size(up[i])}/s";
    }
}
