using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace FetchIt.Controls;

/// <summary>
/// Portable WASD house mark: hex + WASD key cluster.
/// </summary>
public sealed class WasdMark : Control
{
    public static readonly StyledProperty<IBrush?> HexFillProperty =
        AvaloniaProperty.Register<WasdMark, IBrush?>(nameof(HexFill), new SolidColorBrush(Colors.Black));

    public static readonly StyledProperty<IBrush?> AccentProperty =
        AvaloniaProperty.Register<WasdMark, IBrush?>(nameof(Accent), new SolidColorBrush(Colors.White));

    public static readonly StyledProperty<IBrush?> KeyFillProperty =
        AvaloniaProperty.Register<WasdMark, IBrush?>(nameof(KeyFill), new SolidColorBrush(Colors.Black));

    static WasdMark()
    {
        AffectsRender<WasdMark>(HexFillProperty, AccentProperty, KeyFillProperty);
        AffectsMeasure<WasdMark>(WidthProperty, HeightProperty);
    }

    public IBrush? HexFill
    {
        get => GetValue(HexFillProperty);
        set => SetValue(HexFillProperty, value);
    }

    public IBrush? Accent
    {
        get => GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    public IBrush? KeyFill
    {
        get => GetValue(KeyFillProperty);
        set => SetValue(KeyFillProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var side = double.IsNaN(Width) ? 24 : Width;
        var height = double.IsNaN(Height) ? side : Height;
        return new Size(side, height);
    }

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 1)
            return;

        var cx = Bounds.Width / 2;
        var cy = Bounds.Height / 2;
        var radius = size / 2 - Math.Max(1.2, size * 0.04);
        context.DrawGeometry(HexFill, new Pen(Accent, Math.Max(1.1, size * 0.055)), HexGeometry(cx, cy, radius));
        DrawKeys(context, cx, cy, radius);
    }

    private void DrawKeys(DrawingContext context, double cx, double cy, double radius)
    {
        var key = radius * 0.34;
        var gap = radius * 0.06;
        var stroke = new Pen(Accent, Math.Max(0.9, radius * 0.07));
        var w = new Rect(cx - key / 2, cy - key - gap / 2, key, key);
        var a = new Rect(cx - key * 1.5 - gap / 2, cy + gap / 2, key, key);
        var s = new Rect(cx - key / 2, cy + gap / 2, key, key);
        var d = new Rect(cx + key / 2 + gap / 2, cy + gap / 2, key, key);
        foreach (var rect in new[] { w, a, s, d })
            context.DrawRectangle(KeyFill, stroke, rect, 1.2, 1.2);
    }

    private static StreamGeometry HexGeometry(double cx, double cy, double radius)
    {
        var geometry = new StreamGeometry();
        using var ctx = geometry.Open();
        for (var i = 0; i < 6; i++)
        {
            var angle = Math.PI / 180 * (60 * i - 90);
            var point = new Point(cx + radius * Math.Cos(angle), cy + radius * Math.Sin(angle));
            if (i == 0)
                ctx.BeginFigure(point, true);
            else
                ctx.LineTo(point);
        }

        ctx.EndFigure(true);
        return geometry;
    }
}
