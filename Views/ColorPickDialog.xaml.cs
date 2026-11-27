using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace HwPulse;

/// <summary>
/// Selector de color reutilizable del editor del overlay: colores rápidos, HEX y tono/saturación/luminosidad.
/// Cada cambio se avisa en vivo (<see cref="Changed"/>) para ver el resultado en el overlay al momento;
/// "Cancelar" vuelve al color con el que se abrió. Opcionalmente ofrece "Automático" (color según el valor).
/// </summary>
public partial class ColorPickDialog : Window
{
    private static readonly string[] Quick =
    {
        "#FFFFFF", "#FAFAFA", "#A3A3A3", "#555555", "#000000", "#5B9BD5", "#3B8EEA", "#6CC287", "#76B900",
        "#E5484D", "#F59E0B", "#FACC15", "#9C8BD0", "#EC4899", "#22D3EE", "#14B8A6",
    };

    private readonly string _original;
    private readonly Action<string>? _changed;
    private bool _updating;

    /// <summary>"#RRGGBB" o "auto". Válido tras cerrar con "Listo".</summary>
    public string Result { get; private set; }

    public ColorPickDialog(string title, string current, bool allowAuto, Action<string>? changed = null)
    {
        InitializeComponent();
        TitleText.Text = title;
        _original = Result = current;
        _changed = changed;
        AutoBtn.Visibility = allowAuto ? Visibility.Visible : Visibility.Collapsed;
        foreach (var hex in Quick)
        {
            var b = new Button
            {
                Width = 22, Height = 22, Margin = new Thickness(0, 0, 6, 6), Cursor = Cursors.Hand, ToolTip = hex,
                Template = SwatchTemplate(OverlayCustomBuilder.Brush(hex)),
            };
            b.Click += (_, _) => SetHex(hex, notify: true);
            Palette.Children.Add(b);
        }
        SetHex(current == "auto" ? "#FAFAFA" : current, notify: false);
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Cancel_Click(this, new RoutedEventArgs()); };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Native.StyleWindowFrame(new WindowInteropHelper(this).Handle, 0x00262626);
    }

    private static ControlTemplate SwatchTemplate(Brush fill)
    {
        var t = new ControlTemplate(typeof(Button));
        var f = new FrameworkElementFactory(typeof(Border));
        f.SetValue(Border.BackgroundProperty, fill);
        f.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        f.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        f.SetValue(Border.BorderBrushProperty, Application.Current.FindResource("LineStrong"));
        t.VisualTree = f;
        return t;
    }

    private void SetHex(string hex, bool notify)
    {
        Color c;
        try { c = (Color)ColorConverter.ConvertFromString(hex); } catch { return; }
        var (h, s, l) = ColorsWindow.RgbToHsl(c);
        Show(h, s, l, notify);
    }

    private void Show(double h, double s, double l, bool notify)
    {
        _updating = true;
        HueSlider.Value = h; SatSlider.Value = s; LightSlider.Value = l;
        var c = ColorsWindow.HslToRgb(h, s, l);
        string hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        HexBox.Text = hex;
        Swatch.Background = new SolidColorBrush(c);
        var hue = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        for (int i = 0; i <= 6; i++) hue.GradientStops.Add(new GradientStop(ColorsWindow.HslToRgb(i * 60, Math.Max(s, 40), Math.Clamp(l, 30, 70)), i / 6.0));
        HueSlider.Background = hue;
        SatSlider.Background = new LinearGradientBrush(ColorsWindow.HslToRgb(h, 0, l), ColorsWindow.HslToRgb(h, 100, l), 0);
        var light = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        light.GradientStops.Add(new GradientStop(Colors.Black, 0));
        light.GradientStops.Add(new GradientStop(ColorsWindow.HslToRgb(h, s, 50), 0.5));
        light.GradientStops.Add(new GradientStop(Colors.White, 1));
        LightSlider.Background = light;
        _updating = false;
        if (notify) { Result = hex; _changed?.Invoke(hex); }
    }

    private void Hsl_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_updating) Show(HueSlider.Value, SatSlider.Value, LightSlider.Value, notify: true);
    }

    private void Hex_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitHex(); HexBox.SelectAll(); e.Handled = true; }
    }

    private void Hex_LostFocus(object sender, RoutedEventArgs e) => CommitHex();

    private void CommitHex()
    {
        var t = HexBox.Text.Trim();
        if (!t.StartsWith('#')) t = "#" + t;
        if (System.Text.RegularExpressions.Regex.IsMatch(t, "^#[0-9A-Fa-f]{6}$")) SetHex(t.ToUpperInvariant(), notify: true);
        else HexBox.Text = Result == "auto" ? "#FAFAFA" : Result;
    }

    private void Auto_Click(object sender, RoutedEventArgs e)
    {
        Result = "auto";
        _changed?.Invoke("auto");
        DialogResult = true;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Result = _original;
        _changed?.Invoke(_original); // deshace la vista previa en vivo
        DialogResult = false;
    }
}
