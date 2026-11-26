using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace HwPulse;

/// <summary>
/// Editor de colores del overlay: lista de colores a la izquierda y, a la derecha, tono/saturación/luminosidad
/// con sliders de pista degradada y cajas de texto, más el valor HEX. Todo se aplica al overlay en vivo.
/// </summary>
public partial class ColorsWindow : Window
{
    private static readonly string[] Keys = { "bg", "border", "text", "labels", "cpu", "gpu", "mem" };

    private readonly MainViewModel _vm;
    private readonly Dictionary<string, Border> _swatches = new();
    private string _key = "text";
    private bool _updating;

    public ColorsWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        foreach (var k in Keys)
        {
            var sw = new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 10, 0),
                                  BorderBrush = (Brush)FindResource("LineStrong"), BorderThickness = new Thickness(1) };
            _swatches[k] = sw;
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(sw);
            row.Children.Add(new TextBlock { Text = Loc.T("Col." + k), VerticalAlignment = VerticalAlignment.Center });
            var rb = new RadioButton { Style = (Style)FindResource("ColorItem"), Content = row, Tag = k, IsChecked = k == _key };
            rb.Checked += (_, _) => Select(k);
            List.Children.Add(rb);
        }
        RefreshSwatches();
        Select(_key);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Native.StyleWindowFrame(new WindowInteropHelper(this).Handle, 0x00262626);
    }

    private void RefreshSwatches()
    {
        foreach (var (k, sw) in _swatches) sw.Background = new SolidColorBrush(_vm.OverlayColor(k));
    }

    private void Select(string key)
    {
        _key = key;
        EditingName.Text = Loc.T("Col." + key);
        var (h, s, l) = RgbToHsl(_vm.OverlayColor(key));
        ShowHsl(h, s, l);
    }

    /// <summary>Pone sliders, cajas, muestra y degradados sin volver a disparar los eventos.</summary>
    private void ShowHsl(double h, double s, double l)
    {
        _updating = true;
        HueSlider.Value = h; SatSlider.Value = s; LightSlider.Value = l;
        HueBox.Text = $"{h:F0}"; SatBox.Text = $"{s:F0}"; LightBox.Text = $"{l:F0}";
        var c = HslToRgb(h, s, l);
        HexBox.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        Swatch.Background = new SolidColorBrush(c);

        // Pistas: tono (arcoíris con la S/L actuales), saturación (gris → color), luminosidad (negro → color → blanco)
        var hue = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        for (int i = 0; i <= 6; i++) hue.GradientStops.Add(new GradientStop(HslToRgb(i * 60, Math.Max(s, 40), Math.Clamp(l, 30, 70)), i / 6.0));
        HueSlider.Background = hue;
        SatSlider.Background = new LinearGradientBrush(HslToRgb(h, 0, l), HslToRgb(h, 100, l), 0);
        var light = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        light.GradientStops.Add(new GradientStop(Colors.Black, 0));
        light.GradientStops.Add(new GradientStop(HslToRgb(h, s, 50), 0.5));
        light.GradientStops.Add(new GradientStop(Colors.White, 1));
        LightSlider.Background = light;
        _updating = false;
    }

    private void Apply(double h, double s, double l)
    {
        ShowHsl(h, s, l);
        _vm.SetOverlayColor(_key, HslToRgb(h, s, l));
        RefreshSwatches();
    }

    private void Hsl_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating) return;
        Apply(HueSlider.Value, SatSlider.Value, LightSlider.Value);
    }

    // ---------------------------------------------------------------- cajas de texto
    private void HslBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitBox((TextBox)sender); ((TextBox)sender).SelectAll(); e.Handled = true; }
    }

    private void HslBox_LostFocus(object sender, RoutedEventArgs e) => CommitBox((TextBox)sender);

    private void CommitBox(TextBox tb)
    {
        double h = HueSlider.Value, s = SatSlider.Value, l = LightSlider.Value;
        if (double.TryParse(tb.Text.Trim().TrimEnd('%', '°'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
        {
            switch (tb.Tag as string)
            {
                case "h": h = Math.Clamp(v, 0, 360); break;
                case "s": s = Math.Clamp(v, 0, 100); break;
                case "l": l = Math.Clamp(v, 0, 100); break;
            }
            Apply(h, s, l);
        }
        else ShowHsl(h, s, l); // texto inválido: vuelve el valor actual
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
        try
        {
            var c = (Color)ColorConverter.ConvertFromString(t);
            var (h, s, l) = RgbToHsl(c);
            Apply(h, s, l);
        }
        catch { Select(_key); }
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _vm.ResetOverlayColors();
        RefreshSwatches();
        Select(_key);
    }

    private void Done_Click(object sender, RoutedEventArgs e) => Close();

    // ---------------------------------------------------------------- conversión HSL ↔ RGB
    public static Color HslToRgb(double h, double s, double l)
    {
        s /= 100; l /= 100;
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double hp = (h % 360) / 60;
        double x = c * (1 - Math.Abs(hp % 2 - 1));
        (double r, double g, double b) = hp switch
        {
            < 1 => (c, x, 0d),
            < 2 => (x, c, 0d),
            < 3 => (0d, c, x),
            < 4 => (0d, x, c),
            < 5 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        double m = l - c / 2;
        byte B(double v) => (byte)Math.Round(Math.Clamp(v + m, 0, 1) * 255);
        return Color.FromRgb(B(r), B(g), B(b));
    }

    public static (double h, double s, double l) RgbToHsl(Color col)
    {
        double r = col.R / 255.0, g = col.G / 255.0, b = col.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2, d = max - min, h = 0, s = 0;
        if (d > 1e-9)
        {
            s = d / (1 - Math.Abs(2 * l - 1));
            h = max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
            if (h < 0) h += 360;
        }
        return (Math.Round(h), Math.Round(s * 100), Math.Round(l * 100));
    }
}
