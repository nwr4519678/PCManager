using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using System;

namespace PCManager.UI.Controls;

public class SkiaFallbackControl : Control
{
    public static readonly StyledProperty<double> CpuUsageProperty =
        AvaloniaProperty.Register<SkiaFallbackControl, double>(nameof(CpuUsage));

    public double CpuUsage
    {
        get => GetValue(CpuUsageProperty);
        set => SetValue(CpuUsageProperty, value);
    }

    private double _phase = 0;

    public SkiaFallbackControl()
    {
        DispatcherTimer.Run(() =>
        {
            _phase += 0.1;
            InvalidateVisual();
            return true;
        }, TimeSpan.FromMilliseconds(33));
    }

    public override void Render(DrawingContext context)
    {
        var rect = new Rect(Bounds.Size);
        context.FillRectangle(new SolidColorBrush(Color.Parse("#050510")), rect);

        // Draw a simple 2D Sine wave fallback
        var pen = new Pen(CpuUsage > 70 ? Brushes.Red : Brushes.Cyan, 2);
        
        double midY = Bounds.Height / 2;
        double width = Bounds.Width;
        double amp = 20 + (CpuUsage * 0.5);

        Point? lastPoint = null;
        for (double x = 0; x < width; x += 5)
        {
            double y = midY + Math.Sin(x * 0.05 + _phase) * amp;
            var currentPoint = new Point(x, y);
            
            if (lastPoint.HasValue)
            {
                context.DrawLine(pen, lastPoint.Value, currentPoint);
            }
            lastPoint = currentPoint;
        }

        context.DrawText(new FormattedText("GL_FALLBACK_ACTIVE", 
            System.Globalization.CultureInfo.CurrentCulture, 
            FlowDirection.LeftToRight, 
            new Typeface("Inter"), 10, Brushes.Gray), new Point(10, 10));
    }
}
