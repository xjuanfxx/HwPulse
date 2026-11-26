using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace HwPulse;

/// <summary>
/// Gráfica de hasta tres series con ejes. Muestra los últimos <see cref="Visible"/> puntos (1 por segundo).
/// </summary>
public sealed class MultiChart : FrameworkElement
{
    private static DependencyProperty Reg<T>(string name, T def) => DependencyProperty.Register(
        name, typeof(T), typeof(MultiChart), new FrameworkPropertyMetadata(def, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty Values1Property = Reg<double[]?>(nameof(Values1), null);
    public static readonly DependencyProperty Values2Property = Reg<double[]?>(nameof(Values2), null);
    public static readonly DependencyProperty Values3Property = Reg<double[]?>(nameof(Values3), null);
    public static readonly DependencyProperty Stroke1Property = Reg<Brush>(nameof(Stroke1), Brushes.SteelBlue);
    public static readonly DependencyProperty Stroke2Property = Reg<Brush>(nameof(Stroke2), Brushes.SeaGreen);
    public static readonly DependencyProperty Stroke3Property = Reg<Brush>(nameof(Stroke3), Brushes.MediumPurple);
    public static readonly DependencyProperty VisibleProperty = Reg(nameof(Visible), 600);
    public static readonly DependencyProperty MinimumProperty = Reg(nameof(Minimum), 0.0);
    public static readonly DependencyProperty MaximumProperty = Reg(nameof(Maximum), 100.0);
    public static readonly DependencyProperty LabelFormatProperty = Reg(nameof(LabelFormat), "{0:0}%");
    public static readonly DependencyProperty NowLabelProperty = Reg(nameof(NowLabel), "now");
    public static readonly DependencyProperty ShowAxesProperty = Reg(nameof(ShowAxes), true);

    public double[]? Values1 { get => (double[]?)GetValue(Values1Property); set => SetValue(Values1Property, value); }
    public double[]? Values2 { get => (double[]?)GetValue(Values2Property); set => SetValue(Values2Property, value); }
    public double[]? Values3 { get => (double[]?)GetValue(Values3Property); set => SetValue(Values3Property, value); }
    public Brush Stroke1 { get => (Brush)GetValue(Stroke1Property); set => SetValue(Stroke1Property, value); }
    public Brush Stroke2 { get => (Brush)GetValue(Stroke2Property); set => SetValue(Stroke2Property, value); }
    public Brush Stroke3 { get => (Brush)GetValue(Stroke3Property); set => SetValue(Stroke3Property, value); }
    public int Visible { get => (int)GetValue(VisibleProperty); set => SetValue(VisibleProperty, value); }
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    /// <summary>NaN = automático (redondeado a un número "limpio").</summary>
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public string LabelFormat { get => (string)GetValue(LabelFormatProperty); set => SetValue(LabelFormatProperty, value); }
    public string NowLabel { get => (string)GetValue(NowLabelProperty); set => SetValue(NowLabelProperty, value); }
    public bool ShowAxes { get => (bool)GetValue(ShowAxesProperty); set => SetValue(ShowAxesProperty, value); }

    private static readonly Pen GridPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF)), 1));
    private static readonly Brush LabelBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x5A, 0x60, 0x68)));
    private static readonly Typeface LabelFace = new("Segoe UI");

    private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 40 || h < 30) return;
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        int visible = Math.Max(Visible, 2);

        var series = new[] { (Values1, Stroke1), (Values2, Stroke2), (Values3, Stroke3) }
            .Where(s => s.Item1 is { Length: > 0 })
            .Select(s => (vals: s.Item1!.Skip(Math.Max(0, s.Item1!.Length - visible)).ToArray(), brush: s.Item2))
            .ToList();

        // Mientras aún no hay historial completo, el eje X abarca solo el tiempo que sí tenemos:
        // así la línea ocupa todo el ancho en vez de quedar aplastada a la derecha.
        int pts = Math.Clamp(series.Select(s => s.vals.Length).DefaultIfEmpty(0).Max(), 2, visible);

        double min = Minimum;
        double max = Maximum;
        if (double.IsNaN(max))
        {
            double peak = series.SelectMany(s => s.vals).DefaultIfEmpty(0).Max();
            max = NiceCeiling(Math.Max(peak * 1.15, min + 1e-3));
        }
        double range = Math.Max(max - min, 1e-9);

        // El margen izquierdo se ajusta a la etiqueta más ancha del eje Y.
        var yLabels = Enumerable.Range(0, 5)
            .Select(i => Text(string.Format(CultureInfo.InvariantCulture, LabelFormat, min + range * i / 4.0), dip))
            .ToArray();
        double left = ShowAxes ? Math.Ceiling(yLabels.Max(l => l.Width)) + 10 : 0;
        double bottom = ShowAxes ? 16 : 0, top = ShowAxes ? 6 : 0;
        double pw = w - left, ph = h - bottom - top;

        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

        // Rejilla horizontal y etiquetas del eje Y
        for (int i = 0; i <= 4; i++)
        {
            double y = Math.Round(top + ph - ph * i / 4.0) + 0.5;
            dc.DrawLine(GridPen, new Point(left, y), new Point(w, y));
            if (ShowAxes)
            {
                var ft = yLabels[i];
                dc.DrawText(ft, new Point(left - 6 - ft.Width, y - ft.Height / 2));
            }
        }

        // Etiquetas del eje X: inicio, mitad y "ahora"
        if (ShowAxes)
        {
            string Ago(int s) => s >= 120 ? $"-{s / 60} min" : $"-{s} s";
            int span = pts - 1;
            var labels = new[] { (0.0, Ago(span)), (0.5, Ago(span / 2)), (1.0, NowLabel) };
            foreach (var (f, txt) in labels)
            {
                var ft = Text(txt, dip);
                double x = left + pw * f - ft.Width * f;
                dc.DrawText(ft, new Point(x, h - ft.Height));
            }
        }

        double step = pw / (pts - 1);
        foreach (var (vals, brush) in series)
        {
            if (vals.Length < 2) continue;
            double x0 = left + pw - (vals.Length - 1) * step;
            Point P(int i) => new(x0 + i * step, top + ph - Math.Clamp((vals[i] - min) / range, 0, 1) * ph);

            var area = new StreamGeometry();
            using (var ctx = area.Open())
            {
                ctx.BeginFigure(new Point(x0, top + ph), true, true);
                for (int i = 0; i < vals.Length; i++) ctx.LineTo(P(i), true, false);
                ctx.LineTo(new Point(left + pw, top + ph), true, false);
            }
            area.Freeze();

            var line = new StreamGeometry();
            using (var ctx = line.Open())
            {
                ctx.BeginFigure(P(0), false, false);
                for (int i = 1; i < vals.Length; i++) ctx.LineTo(P(i), true, false);
            }
            line.Freeze();

            if (brush is SolidColorBrush sb)
            {
                var c = sb.Color;
                var fill = new LinearGradientBrush(Color.FromArgb(0x30, c.R, c.G, c.B), Color.FromArgb(0x00, c.R, c.G, c.B), 90);
                fill.Freeze();
                dc.DrawGeometry(fill, null, area);
            }
            dc.DrawGeometry(null, new Pen(brush, 1.4) { LineJoin = PenLineJoin.Round }, line);
        }

        DrawHover(dc, series, left, top, pw, ph, step, min, range, dip);
    }

    // ------------------------------------------------------------------ valor bajo el mouse
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

    private static readonly Brush TipBg = Frozen(new SolidColorBrush(Color.FromArgb(0xF2, 0x1A, 0x1A, 0x1A)));
    private static readonly Pen TipBorder = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)), 1));
    private static readonly Pen CursorPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)), 1) { DashStyle = DashStyles.Dash });
    private static readonly Brush TipText = Frozen(new SolidColorBrush(Color.FromRgb(0xE5, 0xE5, 0xE5)));
    private static readonly Brush TipMuted = Frozen(new SolidColorBrush(Color.FromRgb(0x8C, 0x8C, 0x8C)));
    private static readonly Typeface TipFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    /// <summary>Línea vertical en el punto más cercano al cursor, un punto sobre cada serie y una etiqueta con los valores.</summary>
    private void DrawHover(DrawingContext dc, List<(double[] vals, Brush brush)> series, double left, double top,
                           double pw, double ph, double step, double min, double range, double dip)
    {
        if (_hover is not Point hp || series.Count == 0 || hp.X < left || hp.X > left + pw) return;

        int ago = (int)Math.Round((left + pw - hp.X) / step); // segundos hacia atrás desde "ahora"
        double x = Math.Round(left + pw - ago * step) + 0.5;
        var rows = new List<(Brush brush, string text)>();
        foreach (var (vals, brush) in series)
        {
            int i = vals.Length - 1 - ago;
            if (i < 0 || i >= vals.Length) continue;
            double v = vals[i];
            double y = top + ph - Math.Clamp((v - min) / range, 0, 1) * ph;
            dc.DrawEllipse(brush, new Pen(Brushes.Black, 1.5), new Point(x, y), 3.5, 3.5);
            rows.Add((brush, string.Format(CultureInfo.InvariantCulture, LabelFormat, v)));
        }
        if (rows.Count == 0) return;
        dc.DrawLine(CursorPen, new Point(x, top), new Point(x, top + ph));

        // Etiqueta: tiempo arriba, una fila por serie con su color
        var head = new FormattedText(ago == 0 ? NowLabel : $"-{ago} s", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, TipFace, 10.5, TipMuted, dip);
        var lines = rows.Select(r => (r.brush, ft: new FormattedText(r.text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, TipFace, 12, TipText, dip))).ToList();
        double bw = Math.Max(head.Width, lines.Max(l => l.ft.Width + 14)) + 16;
        double lh = 17;
        double bh = 8 + head.Height + 2 + lines.Count * lh + 4;
        // A la izquierda y arriba del cursor (los cursores grandes o personalizados tapan abajo a la derecha);
        // solo si no cabe, se pasa al otro lado.
        double bx = x - 12 - bw >= 0 ? x - 12 - bw : x + 12;
        double by = hp.Y - 14 - bh >= 0 ? hp.Y - 14 - bh : Math.Min(hp.Y + 14, Math.Max(0, ActualHeight - bh));
        dc.DrawRoundedRectangle(TipBg, TipBorder, new Rect(bx, by, bw, bh), 6, 6);
        dc.DrawText(head, new Point(bx + 8, by + 6));
        double ly = by + 8 + head.Height + 2;
        foreach (var (brush, ft) in lines)
        {
            dc.DrawEllipse(brush, null, new Point(bx + 11, ly + lh / 2 - 1), 3.5, 3.5);
            dc.DrawText(ft, new Point(bx + 20, ly + (lh - ft.Height) / 2 - 1));
            ly += lh;
        }
    }

    private static FormattedText Text(string s, double dip) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LabelFace, 10, LabelBrush, dip);

    private static double NiceCeiling(double v)
    {
        double exp = Math.Pow(10, Math.Floor(Math.Log10(v)));
        foreach (var m in new[] { 1, 2, 2.5, 5, 10 })
            if (m * exp >= v) return m * exp;
        return 10 * exp;
    }
}
