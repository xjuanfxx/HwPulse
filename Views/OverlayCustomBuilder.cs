using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;

namespace HwPulse;

/// <summary>
/// Construye el overlay personalizado a partir de un <see cref="OverlayLayout"/>: los textos se enlazan a las
/// mismas propiedades del ViewModel que el overlay de siempre, así que se actualizan solos cada segundo.
/// Se usa en el overlay real y en la vista previa del editor. El tamaño se aplica multiplicando las medidas
/// (no con una transformación) para que el texto se dibuje nítido a su tamaño final.
/// </summary>
public static class OverlayCustomBuilder
{
    public static FrameworkElement Build(OverlayLayout layout, double scale)
    {
        double S(double v) => v * scale;
        var font = layout.Font == "Inter" && Application.Current.TryFindResource("NumFont") is FontFamily inter
            ? inter : new FontFamily(layout.Font);

        var visible = layout.Elements.Where(e => e.Visible).ToList();
        Panel panel;
        switch (layout.Arrangement)
        {
            case "vertical":
                panel = new StackPanel();
                Grid.SetIsSharedSizeScope(panel, true); // etiquetas alineadas en columna
                foreach (var e in visible) panel.Children.Add(VerticalRow(e, layout, S, font, visible.IndexOf(e) > 0));
                break;
            case "grid":
                panel = new UniformGrid { Columns = Math.Max(1, Math.Min(layout.Columns, Math.Max(1, visible.Count))) };
                foreach (var e in visible)
                {
                    var cell = Inline(e, S, font);
                    cell.Margin = new Thickness(0, 0, S(layout.Spacing), S(layout.Spacing / 2));
                    panel.Children.Add(cell);
                }
                break;
            default:
                panel = new StackPanel { Orientation = Orientation.Horizontal };
                for (int i = 0; i < visible.Count; i++)
                {
                    if (i > 0)
                    {
                        if (layout.Separator.Length > 0)
                            panel.Children.Add(new TextBlock
                            {
                                Text = layout.Separator, FontFamily = font, FontSize = S(12), VerticalAlignment = VerticalAlignment.Center,
                                Foreground = Brush(layout.SeparatorColor), Margin = new Thickness(S(layout.Spacing / 2), 0, S(layout.Spacing / 2), 0),
                            });
                        else panel.Children.Add(new Border { Width = S(layout.Spacing) });
                    }
                    panel.Children.Add(Inline(visible[i], S, font));
                }
                break;
        }

        return new Border
        {
            Child = panel,
            Background = Brush(layout.Background, layout.BackgroundOpacity),
            BorderBrush = Brush(layout.Border, layout.BorderOpacity),
            BorderThickness = new Thickness(S(layout.BorderWidth)),
            CornerRadius = new CornerRadius(S(layout.CornerRadius)),
            Padding = new Thickness(S(layout.PaddingX), S(layout.PaddingY), S(layout.PaddingX), S(layout.PaddingY)),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
    }

    /// <summary>Etiqueta + valor (+ barra) en línea: modos horizontal y rejilla.</summary>
    private static FrameworkElement Inline(OverlayElement e, Func<double, double> S, FontFamily font)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (e.Label.Length > 0) sp.Children.Add(Label(e, S, font, new Thickness(0, 0, S(5), 0)));
        sp.Children.Add(Value(e, S, font));
        if (e.Bar) sp.Children.Add(Bar(e, S, new Thickness(S(6), 0, 0, 0), S(40)));
        if (e.Graph) sp.Children.Add(Graph(e, S, new Thickness(S(6), 0, 0, 0), S(56)));
        return sp;
    }

    /// <summary>Fila del modo vertical: etiqueta (columna compartida) · barra · valor a la derecha.</summary>
    private static FrameworkElement VerticalRow(OverlayElement e, OverlayLayout layout, Func<double, double> S, FontFamily font, bool notFirst)
    {
        var g = new Grid { Margin = new Thickness(0, notFirst ? S(layout.Spacing) : 0, 0, 0) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "ovLabel" });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = e.Graph ? S(70) : e.Bar ? S(60) : 0 });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "ovValue" });
        if (e.Label.Length > 0) g.Children.Add(Label(e, S, font, new Thickness(0, 0, S(10), 0)));
        // En columna, la mini gráfica ocupa el espacio central (más informativa que la barra si hay ambas).
        if (e.Graph)
        {
            var chart = Graph(e, S, new Thickness(0, 0, S(10), 0), double.NaN);
            Grid.SetColumn(chart, 1);
            g.Children.Add(chart);
        }
        else if (e.Bar)
        {
            var bar = Bar(e, S, new Thickness(0, 0, S(10), 0), double.NaN);
            Grid.SetColumn(bar, 1);
            g.Children.Add(bar);
        }
        var v = Value(e, S, font);
        v.HorizontalAlignment = HorizontalAlignment.Right;
        v.TextAlignment = TextAlignment.Right;
        Grid.SetColumn(v, 2);
        g.Children.Add(v);
        return g;
    }

    private static TextBlock Label(OverlayElement e, Func<double, double> S, FontFamily font, Thickness margin) => new()
    {
        Text = e.Label, FontFamily = font, FontSize = Math.Round(S(Math.Max(8, e.Size * 0.75)) * 2) / 2, FontWeight = FontWeights.Bold,
        Foreground = Brush(e.LabelColor), VerticalAlignment = VerticalAlignment.Center, Margin = margin,
    };

    private static TextBlock Value(OverlayElement e, Func<double, double> S, FontFamily font)
    {
        var (textProp, autoBrush, _, _) = OverlayLayout.Catalog[e.Key];
        var tb = new TextBlock
        {
            FontFamily = font, FontSize = Math.Round(S(e.Size) * 2) / 2, FontWeight = e.Bold ? FontWeights.Bold : FontWeights.Medium,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Typography.SetNumeralAlignment(tb, FontNumeralAlignment.Tabular);
        tb.SetBinding(TextBlock.TextProperty, new Binding(textProp));
        if (e.ValueColor == "auto")
            tb.SetBinding(TextBlock.ForegroundProperty, new Binding(autoBrush ?? nameof(MainViewModel.OvTextBrush)));
        else tb.Foreground = Brush(e.ValueColor);
        return tb;
    }

    private static ProgressBar Bar(OverlayElement e, Func<double, double> S, Thickness margin, double width)
    {
        var pct = OverlayLayout.Catalog[e.Key].pct!;
        var bar = new ProgressBar
        {
            Maximum = 100, Height = Math.Max(2, S(3)), Margin = margin, VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)),
            Template = BarTemplate,
        };
        if (!double.IsNaN(width)) bar.Width = width;
        bar.SetBinding(RangeBase.ValueProperty, new Binding(pct) { Mode = BindingMode.OneWay });
        if (e.ValueColor != "auto") bar.Foreground = Brush(e.ValueColor);
        else bar.SetBinding(Control.ForegroundProperty, new Binding(e.Key.StartsWith("cpu") ? nameof(MainViewModel.OvCpuBrush)
            : e.Key.StartsWith("gpu") ? nameof(MainViewModel.OvGpuBrush) : nameof(MainViewModel.OvMemBrush)));
        return bar;
    }

    /// <summary>Mini gráfica del historial (los últimos 60 s), del color del dato.</summary>
    private static FrameworkElement Graph(OverlayElement e, Func<double, double> S, Thickness margin, double width)
    {
        var (series, max) = OverlayLayout.Series[e.Key];
        var chart = new SparkChart
        {
            Height = Math.Max(10, S(e.Size * 1.1)), Margin = margin, VerticalAlignment = VerticalAlignment.Center,
            Minimum = 0, Maximum = max, Capacity = 60, ShowFrame = false,
        };
        if (!double.IsNaN(width)) chart.Width = width;
        chart.SetBinding(SparkChart.ValuesProperty, new Binding(series));
        if (e.ValueColor != "auto") chart.Stroke = Brush(e.ValueColor);
        else chart.SetBinding(SparkChart.StrokeProperty, new Binding(e.Key.StartsWith("cpu") ? nameof(MainViewModel.OvCpuBrush)
            : e.Key == "ram" ? nameof(MainViewModel.OvMemBrush) : nameof(MainViewModel.OvGpuBrush)));
        return chart;
    }

    // Plantilla mínima de la barra: pista + indicador redondeado (como la del overlay de siempre).
    private static readonly ControlTemplate BarTemplate = CreateBarTemplate();
    private static ControlTemplate CreateBarTemplate()
    {
        var t = new ControlTemplate(typeof(ProgressBar));
        var grid = new FrameworkElementFactory(typeof(Grid));
        var track = new FrameworkElementFactory(typeof(Border), "PART_Track");
        track.SetValue(Border.CornerRadiusProperty, new CornerRadius(1.5));
        track.SetBinding(Border.BackgroundProperty, new Binding(nameof(Control.Background)) { RelativeSource = RelativeSource.TemplatedParent });
        var ind = new FrameworkElementFactory(typeof(Border), "PART_Indicator");
        ind.SetValue(Border.CornerRadiusProperty, new CornerRadius(1.5));
        ind.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        ind.SetBinding(Border.BackgroundProperty, new Binding(nameof(Control.Foreground)) { RelativeSource = RelativeSource.TemplatedParent });
        grid.AppendChild(track);
        grid.AppendChild(ind);
        t.VisualTree = grid;
        t.Seal();
        return t;
    }

    public static SolidColorBrush Brush(string hex, double opacity = 1)
    {
        var c = (Color)ColorConverter.ConvertFromString(hex);
        c.A = (byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255);
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
