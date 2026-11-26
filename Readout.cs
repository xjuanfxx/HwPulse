using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace HwPulse;

/// <summary>
/// Lectura compacta: etiqueta pequeña, valor grande con la unidad en pequeño, y una línea secundaria
/// (normalmente el rango mínimo–máximo de la sesión).
/// </summary>
public sealed class Readout : StackPanel
{
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(Readout), new PropertyMetadata("", (d, e) =>
        {
            var r = (Readout)d;
            r._label.Text = (string)e.NewValue;
            r._label.Visibility = string.IsNullOrEmpty((string)e.NewValue) ? Visibility.Collapsed : Visibility.Visible;
        }));

    public static readonly DependencyProperty ValueSizeProperty = DependencyProperty.Register(
        nameof(ValueSize), typeof(double), typeof(Readout), new PropertyMetadata(18.0, (d, e) =>
        {
            var r = (Readout)d;
            r._value.FontSize = (double)e.NewValue;
            r._unit.FontSize = Math.Max(11, (double)e.NewValue * 0.6);
        }));

    public double ValueSize { get => (double)GetValue(ValueSizeProperty); set => SetValue(ValueSizeProperty, value); }

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(string), typeof(Readout), new PropertyMetadata("", (d, e) => ((Readout)d).SetValueText((string)e.NewValue)));

    public static readonly DependencyProperty SubProperty = DependencyProperty.Register(
        nameof(Sub), typeof(string), typeof(Readout), new PropertyMetadata(" ", (d, e) =>
        {
            var r = (Readout)d;
            r._sub.Text = (string)e.NewValue;
            r._sub.Visibility = string.IsNullOrEmpty((string)e.NewValue) ? Visibility.Collapsed : Visibility.Visible;
        }));

    public static readonly DependencyProperty ValueBrushProperty = DependencyProperty.Register(
        nameof(ValueBrush), typeof(Brush), typeof(Readout), new PropertyMetadata(null, (d, e) =>
        {
            var r = (Readout)d;
            if (e.NewValue is Brush b) r._num.Foreground = b;
            else r._num.ClearValue(TextElement.ForegroundProperty);
        }));

    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string Value { get => (string)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public string Sub { get => (string)GetValue(SubProperty); set => SetValue(SubProperty, value); }
    public Brush? ValueBrush { get => (Brush?)GetValue(ValueBrushProperty); set => SetValue(ValueBrushProperty, value); }

    private readonly TextBlock _label = new(), _value = new(), _sub = new() { Text = " " };
    private readonly Run _num = new(), _unit = new();

    public Readout()
    {
        _label.SetResourceReference(StyleProperty, "Caption");
        _label.TextTrimming = TextTrimming.CharacterEllipsis;
        _label.Visibility = Visibility.Collapsed;

        _value.SetResourceReference(TextBlock.FontFamilyProperty, "NumFont");
        _value.SetResourceReference(TextBlock.ForegroundProperty, "TextStrong");
        _value.FontSize = 18;
        _value.FontWeight = FontWeights.Bold;
        _value.Margin = new Thickness(0, 1, 0, 0);
        _value.TextTrimming = TextTrimming.None; // un número cortado ("2…") no sirve de nada
        Typography.SetNumeralAlignment(_value, FontNumeralAlignment.Tabular);

        _unit.FontSize = 11.5;
        _unit.FontWeight = FontWeights.SemiBold;
        _unit.SetResourceReference(TextElement.ForegroundProperty, "Muted");
        _value.Inlines.Add(_num);
        _value.Inlines.Add(_unit);

        _sub.SetResourceReference(StyleProperty, "Hint");
        _sub.TextTrimming = TextTrimming.CharacterEllipsis;
        Typography.SetNumeralAlignment(_sub, FontNumeralAlignment.Tabular);

        Children.Add(_label);
        Children.Add(_value);
        Children.Add(_sub);
    }

    /// <summary>"57 °C" → número grande "57" + unidad pequeña " °C".</summary>
    private void SetValueText(string text)
    {
        int i = text.LastIndexOf(' ');
        if (i > 0 && i < text.Length - 1)
        {
            _num.Text = text[..i];
            _unit.Text = " " + text[(i + 1)..];
        }
        else
        {
            _num.Text = text;
            _unit.Text = "";
        }
    }
}
