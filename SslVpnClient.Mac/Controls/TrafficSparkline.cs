using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace SslVpnClient.Mac.Controls;

/// <summary>
/// 简易实时流量折线图（本次连接采样）。
/// </summary>
public class TrafficSparkline : Control
{
    public static readonly StyledProperty<ObservableCollection<double>?> SamplesProperty =
        AvaloniaProperty.Register<TrafficSparkline, ObservableCollection<double>?>(nameof(Samples));

    public static readonly StyledProperty<IBrush> StrokeProperty =
        AvaloniaProperty.Register<TrafficSparkline, IBrush>(
            nameof(Stroke),
            new SolidColorBrush(Color.FromRgb(0x0F, 0x76, 0x6E)));

    public ObservableCollection<double>? Samples
    {
        get => GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }

    public IBrush Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    private ObservableCollection<double>? _subscribed;

    static TrafficSparkline()
    {
        AffectsRender<TrafficSparkline>(SamplesProperty, StrokeProperty);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != SamplesProperty)
        {
            return;
        }

        if (_subscribed != null)
        {
            _subscribed.CollectionChanged -= OnCollectionChanged;
            _subscribed = null;
        }

        if (change.NewValue is ObservableCollection<double> samples)
        {
            _subscribed = samples;
            samples.CollectionChanged += OnCollectionChanged;
        }

        InvalidateVisual();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        InvalidateVisual();

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 1 || h <= 1)
        {
            return;
        }

        var bg = new SolidColorBrush(Color.FromRgb(0xF8, 0xFA, 0xFC));
        context.DrawRectangle(bg, null, new RoundedRect(new Rect(0, 0, w, h), 8));

        var samples = Samples;
        if (samples == null || samples.Count < 2)
        {
            return;
        }

        var max = 1.0;
        foreach (var v in samples)
        {
            if (v > max)
            {
                max = v;
            }
        }

        var geometry = new StreamGeometry();
        using (var geo = geometry.Open())
        {
            var step = w / Math.Max(samples.Count - 1, 1);
            for (var i = 0; i < samples.Count; i++)
            {
                var x = i * step;
                var y = h - (samples[i] / max) * (h - 6) - 3;
                var pt = new Point(x, y);
                if (i == 0)
                {
                    geo.BeginFigure(pt, false);
                }
                else
                {
                    geo.LineTo(pt);
                }
            }

            geo.EndFigure(false);
        }

        var pen = new Pen(Stroke, 1.6)
        {
            LineJoin = PenLineJoin.Round,
            LineCap = PenLineCap.Round
        };
        context.DrawGeometry(null, pen, geometry);
    }
}
