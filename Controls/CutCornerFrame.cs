using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace FetchIt.Controls;

public sealed class CutCornerFrame : Decorator
{
    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<CutCornerFrame, IBrush?>(nameof(Stroke), new SolidColorBrush(Colors.White));

    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<CutCornerFrame, IBrush?>(nameof(Fill));

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<CutCornerFrame, double>(nameof(StrokeThickness), 1.0);

    public static readonly StyledProperty<double> CutSizeProperty =
        AvaloniaProperty.Register<CutCornerFrame, double>(nameof(CutSize), 14.0);

    public static readonly StyledProperty<bool> ShowCornerMarksProperty =
        AvaloniaProperty.Register<CutCornerFrame, bool>(nameof(ShowCornerMarks), true);

    static CutCornerFrame()
    {
        AffectsRender<CutCornerFrame>(StrokeProperty, FillProperty, StrokeThicknessProperty, CutSizeProperty, ShowCornerMarksProperty);
        AffectsArrange<CutCornerFrame>(CutSizeProperty);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public double CutSize
    {
        get => GetValue(CutSizeProperty);
        set => SetValue(CutSizeProperty, value);
    }

    public bool ShowCornerMarks
    {
        get => GetValue(ShowCornerMarksProperty);
        set => SetValue(ShowCornerMarksProperty, value);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Clip = CreateGeometry(finalSize.Width, finalSize.Height, CutSize);
        return base.ArrangeOverride(finalSize);
    }

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0)
            return;

        var cut = EffectiveCut(w, h, CutSize);
        var geometry = CreateGeometry(w, h, cut, inset: StrokeThickness / 2);

        if (Fill is not null)
            context.DrawGeometry(Fill, null, geometry);

        if (Stroke is not null)
            context.DrawGeometry(null, new Pen(Stroke, StrokeThickness), geometry);

        if (!ShowCornerMarks || Stroke is null)
            return;

        DrawDiamond(context, cut, cut * 0.45);
        DrawDiamond(context, w - cut, cut * 0.45);
        DrawDiamond(context, cut, h - cut * 0.45);
        DrawDiamond(context, w - cut, h - cut * 0.45);
    }

    public static StreamGeometry CreateGeometry(double width, double height, double cut, double inset = 0)
    {
        cut = EffectiveCut(width, height, cut);
        var left = inset;
        var top = inset;
        var right = width - inset;
        var bottom = height - inset;

        var geometry = new StreamGeometry();
        using var ctx = geometry.Open();
        ctx.BeginFigure(new Point(left + cut, top), true);
        ctx.LineTo(new Point(right - cut, top));
        ctx.LineTo(new Point(right, top + cut));
        ctx.LineTo(new Point(right, bottom - cut));
        ctx.LineTo(new Point(right - cut, bottom));
        ctx.LineTo(new Point(left + cut, bottom));
        ctx.LineTo(new Point(left, bottom - cut));
        ctx.LineTo(new Point(left, top + cut));
        ctx.EndFigure(true);
        return geometry;
    }

    private static double EffectiveCut(double width, double height, double cut) =>
        Math.Min(cut, Math.Min(width, height) / 3);

    private void DrawDiamond(DrawingContext context, double x, double y)
    {
        const double r = 3.2;
        var diamond = new StreamGeometry();
        using (var ctx = diamond.Open())
        {
            ctx.BeginFigure(new Point(x, y - r), true);
            ctx.LineTo(new Point(x + r, y));
            ctx.LineTo(new Point(x, y + r));
            ctx.LineTo(new Point(x - r, y));
            ctx.EndFigure(true);
        }

        context.DrawGeometry(Stroke, new Pen(Stroke, 0.8), diamond);
    }
}
