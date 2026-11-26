using System.Windows;
using System.Windows.Media;

namespace HwPulse;

/// <summary>Gráfica de línea ligera: los datos nuevos entran por la derecha.</summary>
public sealed class SparkChart : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(double[]), typeof(SparkChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(SparkChart), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>NaN = automático según los datos.</summary>
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(SparkChart), new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(SparkChart), new FrameworkPropertyMetadata(Brushes.DeepSkyBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CapacityProperty = DependencyProperty.Register(
        nameof(Capacity), typeof(int), typeof(SparkChart), new FrameworkPropertyMetadata(600, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowFrameProperty = DependencyProperty.Register(
        nameof(ShowFrame), typeof(bool), typeof(SparkChart), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public bool ShowFrame { get => (bool)GetValue(ShowFrameProperty); set => SetValue(ShowFrameProperty, value); }

    public double[]? Values { get => (double[]?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public int Capacity { get => (int)GetValue(CapacityProperty); set => SetValue(CapacityProperty, value); }

    private static readonly Pen GridPen = CreateGridPen();

    private static Pen CreateGridPen()
    {
        var p = new Pen(new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)), 1);
        p.Freeze();
        return p;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 2 || h <= 2) return;

        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        if (ShowFrame)
            foreach (var f in new[] { 0.25, 0.5, 0.75 })
                dc.DrawLine(GridPen, new Point(0, Math.Round(h * f) + 0.5), new Point(w, Math.Round(h * f) + 0.5));

        var v = Values;
        if (v is { Length: > 0 } && v.Length > Capacity) v = v[^Capacity..];
        if (v is null || v.Length < 2)
        {
            if (ShowFrame) dc.DrawRectangle(null, GridPen, new Rect(0.5, 0.5, w - 1, h - 1));
            return;
        }

        double min = Minimum;
        double max = double.IsNaN(Maximum) ? Math.Max(v.Max() * 1.1, min + 1) : Maximum;
        double range = Math.Max(max - min, 1e-6);
        // Con poco historial, la línea se estira a todo el ancho en vez de quedarse pegada a la derecha.
        double step = w / Math.Max(Math.Min(Capacity, v.Length) - 1, 1);
        double x0 = w - (v.Length - 1) * step;

        Point P(int i) => new(x0 + i * step, h - Math.Clamp((v[i] - min) / range, 0, 1) * (h - 2) - 1);

        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(P(0), false, false);
            for (int i = 1; i < v.Length; i++) ctx.LineTo(P(i), true, false);
        }
        line.Freeze();

        var area = new StreamGeometry();
        using (var ctx = area.Open())
        {
            ctx.BeginFigure(new Point(x0, h), true, true);
            for (int i = 0; i < v.Length; i++) ctx.LineTo(P(i), true, false);
            ctx.LineTo(new Point(w, h), true, false);
        }
        area.Freeze();

        Brush fill = Brushes.Transparent;
        if (Stroke is SolidColorBrush sb)
        {
            var c = sb.Color;
            fill = new SolidColorBrush(Color.FromArgb(0x22, c.R, c.G, c.B));
            fill.Freeze();
        }

        if (ShowFrame) dc.DrawRectangle(null, GridPen, new Rect(0.5, 0.5, w - 1, h - 1));
        dc.DrawGeometry(fill, null, area);
        dc.DrawGeometry(null, new Pen(Stroke, 1.25) { LineJoin = PenLineJoin.Round }, line);

        // Valor bajo el mouse: punto sobre la línea y etiqueta con el valor y hace cuánto fue
        if (_hover is Point hp && hp.X >= 0 && hp.X <= w)
        {
            int ago = (int)Math.Round((w - hp.X) / step);
            int i = v.Length - 1 - ago;
            if (i >= 0 && i < v.Length)
            {
                var p = P(i);
                double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
                dc.DrawLine(CursorPen, new Point(Math.Round(p.X) + 0.5, 0), new Point(Math.Round(p.X) + 0.5, h));
                dc.DrawEllipse(Stroke, new Pen(Brushes.Black, 1.5), p, 3, 3);
                var ft = new FormattedText($"{string.Format(System.Globalization.CultureInfo.InvariantCulture, LabelFormat, v[i])}  ·  {(ago == 0 ? "0" : "-" + ago)} s",
                    System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, TipFace, 11, TipText, dip);
                double bw = ft.Width + 12, bh = ft.Height + 6;
                // A la izquierda y arriba del punto, para que el cursor no la tape
                double bx = p.X - 8 - bw >= 0 ? p.X - 8 - bw : Math.Min(p.X + 8, Math.Max(0, w - bw));
                double by = p.Y - bh - 6;
                dc.DrawRoundedRectangle(TipBg, TipBorder, new Rect(bx, by, bw, bh), 5, 5);
                dc.DrawText(ft, new Point(bx + 6, by + 3));
            }
        }
    }

    public static readonly DependencyProperty LabelFormatProperty = DependencyProperty.Register(
        nameof(LabelFormat), typeof(string), typeof(SparkChart), new FrameworkPropertyMetadata("{0:0}%", FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Formato del valor en la etiqueta al pasar el mouse.</summary>
    public string LabelFormat { get => (string)GetValue(LabelFormatProperty); set => SetValue(LabelFormatProperty, value); }

    private Point? _hover;

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _hover = e.GetPosition(this);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = null;
        InvalidateVisual();
    }

    private static readonly Brush TipBg = Fz(new SolidColorBrush(Color.FromArgb(0xF2, 0x1A, 0x1A, 0x1A)));
    private static readonly Pen TipBorder = Fz(new Pen(new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)), 1));
    private static readonly Pen CursorPen = Fz(new Pen(new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)), 1));
    private static readonly Brush TipText = Fz(new SolidColorBrush(Color.FromRgb(0xE5, 0xE5, 0xE5)));
    private static readonly Typeface TipFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static T Fz<T>(T f) where T : Freezable { f.Freeze(); return f; }
}
