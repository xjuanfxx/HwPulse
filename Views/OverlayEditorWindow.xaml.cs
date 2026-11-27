using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace HwPulse;

/// <summary>
/// Editor visual del overlay personalizado. Todo cambio se aplica en vivo: a la vista previa de la izquierda
/// y al overlay real (que se muestra mientras se edita). Se guarda solo, como el resto de los ajustes.
/// </summary>
public partial class OverlayEditorWindow : Window
{
    private readonly MainViewModel _vm;
    private OverlayLayout _layout;
    private bool _building, _moveMode;

    // Columnas de la lista de elementos: arrastrar · subir/bajar · visible · nombre · etiqueta · color valor · color etiqueta · tamaño · negrita · barra · gráfica
    private static readonly double[] Cols = { 18, 50, 46, 0, 84, 34, 34, 52, 48, 34, 34 };

    public OverlayEditorWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        _layout = vm.CustomLayout.Clone();
        _layout.EnsureAllElements();
        PreviewHost.DataContext = vm;
        BuildHeader();
        BuildStyle();
        BuildElements();
        RefreshPreview();
        _current = _initial = StateKey();
        _initialOnlyInGame = vm.OverlayOnlyInGame;
        vm.PositionChanged += OnPositionChanged;
        UpdateUndoButtons();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
            // Ctrl+Z / Ctrl+Y (y Ctrl+Mayús+Z), salvo mientras se escribe en una caja de texto (allí deshace el texto)
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && Keyboard.FocusedElement is not TextBox)
            {
                if (e.Key == Key.Z && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) { Undo_Click(this, e); e.Handled = true; }
                else if (e.Key == Key.Y || (e.Key == Key.Z && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))) { Redo_Click(this, e); e.Handled = true; }
            }
        };
        // Al cerrar el editor, el overlay vuelve a dejar pasar los clics
        Closed += (_, _) =>
        {
            _vm.PositionChanged -= OnPositionChanged;
            ((App)Application.Current).SetOverlayMoveMode(false);
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Native.StyleWindowFrame(new WindowInteropHelper(this).Handle, 0x00262626);
    }

    /// <summary>Aplica el diseño: vista previa + overlay real (y se guarda con el guardado diferido).</summary>
    private void Changed()
    {
        if (_building) return;
        RecordHistory();
        RefreshPreview();
        _vm.SetCustomLayout(_layout.Clone());
    }

    // ------------------------------------------------------------------ deshacer / rehacer
    // Historial de estados completos: el diseño (JSON) + la posición del overlay. Los cambios seguidos de un
    // mismo gesto (arrastrar un slider, escribir una etiqueta) se agrupan en un solo paso si llegan con menos
    // de 600 ms de diferencia. "Cancelar" vuelve al estado con el que se abrió el editor.
    private readonly List<string> _undo = new(), _redo = new();
    private string _current = "", _initial = "";
    private bool _initialOnlyInGame, _restoring;
    private DateTime _lastRecord = DateTime.MinValue;
    private const string PosMark = "\n#POS:";

    private string StateKey() =>
        _layout.ToJson() + PosMark + string.Join(";", (int)_vm.Corner,
            _vm.OverlayX.ToString("R", CultureInfo.InvariantCulture), _vm.OverlayY.ToString("R", CultureInfo.InvariantCulture));

    /// <summary>El overlay se movió (arrastre, botón de posición por defecto…): un paso más en el historial.</summary>
    private void OnPositionChanged()
    {
        if (_restoring) return;
        _lastRecord = DateTime.MinValue; // cada movimiento es su propio paso
        RecordHistory();
    }

    private void RecordHistory()
    {
        string json = StateKey();
        if (json == _current) return;
        if (_current.Length > 0 && (DateTime.UtcNow - _lastRecord).TotalMilliseconds > 600)
        {
            _undo.Add(_current);
            if (_undo.Count > 100) _undo.RemoveAt(0);
        }
        _redo.Clear();
        _current = json;
        _lastRecord = DateTime.UtcNow;
        UpdateUndoButtons();
    }

    private void Restore(string state)
    {
        int cut = state.IndexOf(PosMark, StringComparison.Ordinal);
        string json = cut >= 0 ? state[..cut] : state;
        if (OverlayLayout.FromJson(json) is not { } l) return;
        _restoring = true;
        try
        {
            _layout = l;
            _current = state;
            _lastRecord = DateTime.MinValue; // el siguiente cambio abre un paso nuevo
            BuildStyle();
            BuildElements();
            RefreshPreview();
            _vm.SetCustomLayout(_layout.Clone());
            // Posición guardada en ese paso
            if (cut >= 0 && state[(cut + PosMark.Length)..].Split(';') is [var c, var x, var y]
                && int.TryParse(c, out int corner) && Enum.IsDefined((OverlayCorner)corner))
            {
                if ((OverlayCorner)corner == OverlayCorner.Custom
                    && double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out var px)
                    && double.TryParse(y, NumberStyles.Float, CultureInfo.InvariantCulture, out var py))
                    _vm.SetCustomPosition(px, py);
                else _vm.SetCorner((OverlayCorner)corner);
            }
            _current = StateKey();
        }
        finally { _restoring = false; }
        UpdateUndoButtons();
    }

    /// <summary>Cancelar: deshace todo lo hecho desde que se abrió el editor (diseño, posición y "solo en juegos").</summary>
    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Restore(_initial);
        _vm.OverlayOnlyInGame = _initialOnlyInGame;
        Close();
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_undo.Count == 0) return;
        _redo.Add(_current);
        var json = _undo[^1]; _undo.RemoveAt(_undo.Count - 1);
        Restore(json);
    }

    private void Redo_Click(object sender, RoutedEventArgs e)
    {
        if (_redo.Count == 0) return;
        _undo.Add(_current);
        var json = _redo[^1]; _redo.RemoveAt(_redo.Count - 1);
        Restore(json);
    }

    private void UpdateUndoButtons()
    {
        UndoBtn.IsEnabled = _undo.Count > 0; UndoBtn.Opacity = _undo.Count > 0 ? 1 : 0.4;
        RedoBtn.IsEnabled = _redo.Count > 0; RedoBtn.Opacity = _redo.Count > 0 ? 1 : 0.4;
    }

    private void RefreshPreview() => PreviewHost.Content = OverlayCustomBuilder.Build(_layout, 1);

    // ================================================================== estilo general
    private void BuildStyle()
    {
        _building = true;
        StylePanel.Children.Clear();
        var L = _layout;

        Section("OvBehavior");
        var moveBtn = new ToggleButton { Style = (Style)FindResource("MoveToggle"), Content = Loc.T("OvMoveMouse"), IsChecked = _moveMode,
                                         HorizontalAlignment = HorizontalAlignment.Left, ToolTip = Loc.T("OvMoveMouseTip") };
        moveBtn.Checked += (_, _) => { _moveMode = true; ((App)Application.Current).SetOverlayMoveMode(true); };
        moveBtn.Unchecked += (_, _) => { _moveMode = false; ((App)Application.Current).SetOverlayMoveMode(false); };
        var resetPos = new Button { Style = (Style)FindResource("SmallButton"), Content = Loc.T("OvResetPosition"), Margin = new Thickness(8, 0, 0, 0),
                                    ToolTip = Loc.T("OvResetPositionTip"), VerticalAlignment = VerticalAlignment.Center };
        resetPos.Click += (_, _) => _vm.SetCorner(OverlayCorner.TopCenter);
        var posRow = new StackPanel { Orientation = Orientation.Horizontal };
        posRow.Children.Add(moveBtn);
        posRow.Children.Add(resetPos);
        StylePanel.Children.Add(posRow);
        StylePanel.Children.Add(new TextBlock { Text = Loc.T("OvMoveMouseHint"), Style = (Style)FindResource("Hint"),
                                                Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap });
        var onlyGame = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = _vm.OverlayOnlyInGame, VerticalAlignment = VerticalAlignment.Center };
        onlyGame.Checked += (_, _) => _vm.OverlayOnlyInGame = true;
        onlyGame.Unchecked += (_, _) => _vm.OverlayOnlyInGame = false;
        var gameRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        gameRow.Children.Add(onlyGame);
        var gameText = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        gameText.Children.Add(new TextBlock { Text = Loc.T("OvOnlyInGame"), Style = (Style)FindResource("Body") });
        gameText.Children.Add(new TextBlock { Text = Loc.T("OvOnlyInGameHint"), Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap, MaxWidth = 300 });
        gameRow.Children.Add(gameText);
        StylePanel.Children.Add(gameRow);

        Section("OvLayout");
        var arr = Segments(new[] { ("horizontal", "OvHorizontal"), ("vertical", "OvVertical"), ("grid", "OvGrid") }, L.Arrangement,
            v => { L.Arrangement = v; BuildStyle(); Changed(); });
        var cols = NumBox(L.Columns, 1, 6, v => { L.Columns = (int)v; Changed(); });
        cols.Visibility = L.Arrangement == "grid" ? Visibility.Visible : Visibility.Collapsed;
        var arrRow = new StackPanel { Orientation = Orientation.Horizontal };
        arrRow.Children.Add(arr);
        if (L.Arrangement == "grid")
        {
            arrRow.Children.Add(new TextBlock { Text = Loc.T("OvColumns"), Style = (Style)FindResource("Hint"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 6, 0) });
            arrRow.Children.Add(cols);
        }
        StylePanel.Children.Add(arrRow);

        Section("OvFont");
        var fontBtn = new Button { Style = (Style)FindResource("SmallButton"), Content = L.Font + "  ▾", HorizontalAlignment = HorizontalAlignment.Left };
        fontBtn.Click += (_, _) => ShowMenu(fontBtn, OverlayLayout.Fonts.Select(f => (f, f)), f => { L.Font = f; BuildStyle(); Changed(); });
        StylePanel.Children.Add(fontBtn);

        Section("OvBackground");
        StylePanel.Children.Add(ColorAndSlider(L.Background, c => { L.Background = c; Changed(); },
            "OvOpacity", L.BackgroundOpacity * 100, 0, 100, v => { L.BackgroundOpacity = v / 100; Changed(); }, "%"));

        Section("OvBorder");
        StylePanel.Children.Add(ColorAndSlider(L.Border, c => { L.Border = c; Changed(); },
            "OvOpacity", L.BorderOpacity * 100, 0, 100, v => { L.BorderOpacity = v / 100; Changed(); }, "%"));
        StylePanel.Children.Add(SliderRow("OvBorderWidth", L.BorderWidth, 0, 4, v => { L.BorderWidth = v; Changed(); }, "px", 0.5));

        Section("OvShape");
        StylePanel.Children.Add(SliderRow("OvCorners", L.CornerRadius, 0, 24, v => { L.CornerRadius = v; Changed(); }, "px"));
        StylePanel.Children.Add(SliderRow("OvPaddingX", L.PaddingX, 0, 40, v => { L.PaddingX = v; Changed(); }, "px"));
        StylePanel.Children.Add(SliderRow("OvPaddingY", L.PaddingY, 0, 40, v => { L.PaddingY = v; Changed(); }, "px"));
        StylePanel.Children.Add(SliderRow("OvSpacing", L.Spacing, 0, 40, v => { L.Spacing = v; Changed(); }, "px"));

        if (L.Arrangement == "horizontal")
        {
            Section("OvSeparator");
            var sepRow = new StackPanel { Orientation = Orientation.Horizontal };
            var sep = new TextBox { Style = (Style)FindResource("SearchBox"), Width = 60, Text = L.Separator, MaxLength = 3 };
            sep.TextChanged += (_, _) => { L.Separator = sep.Text; Changed(); };
            sepRow.Children.Add(sep);
            sepRow.Children.Add(Swatch(L.SeparatorColor, Loc.T("OvSeparator"), false, c => { L.SeparatorColor = c; Changed(); }, new Thickness(8, 0, 0, 0)));
            sepRow.Children.Add(new TextBlock { Text = Loc.T("OvSeparatorHint"), Style = (Style)FindResource("Hint"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) });
            StylePanel.Children.Add(sepRow);
        }
        _building = false;
    }

    private void Section(string key) => StylePanel.Children.Add(new TextBlock
    {
        Text = Loc.T(key), Style = (Style)FindResource("Caption"), Margin = new Thickness(0, StylePanel.Children.Count == 0 ? 0 : 14, 0, 6),
    });

    private Border Segments((string value, string key)[] options, string current, Action<string> set)
    {
        var host = new Border { Style = (Style)FindResource("SegmentHost"), HorizontalAlignment = HorizontalAlignment.Left };
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        string group = "seg" + Guid.NewGuid().ToString("N");
        foreach (var (value, key) in options)
        {
            var rb = new RadioButton { Style = (Style)FindResource("Segment"), GroupName = group, Content = Loc.T(key), IsChecked = value == current };
            rb.Checked += (_, _) => { if (!_building) set(value); };
            sp.Children.Add(rb);
        }
        host.Child = sp;
        return host;
    }

    private const double SliderLabelWidth = 140; // mismo inicio de slider con o sin muestra de color delante

    private Grid SliderRow(string labelKey, double value, double min, double max, Action<double> set, string unit, double step = 1, double labelWidth = SliderLabelWidth)
    {
        var g = new Grid { Height = 30 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelWidth) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        g.Children.Add(new TextBlock { Text = Loc.T(labelKey), Style = (Style)FindResource("Body"), VerticalAlignment = VerticalAlignment.Center });
        var s = new Slider { Minimum = min, Maximum = max, Value = value, VerticalAlignment = VerticalAlignment.Center, IsSnapToTickEnabled = true, TickFrequency = step };
        Grid.SetColumn(s, 1);
        var t = new TextBlock { Style = (Style)FindResource("Hint"), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right,
                                Text = Fmt(value, unit) };
        Grid.SetColumn(t, 2);
        s.ValueChanged += (_, e) => { t.Text = Fmt(e.NewValue, unit); if (!_building) set(e.NewValue); };
        g.Children.Add(s);
        g.Children.Add(t);
        return g;
    }

    private static string Fmt(double v, string unit) => v.ToString(v % 1 == 0 ? "0" : "0.0", CultureInfo.InvariantCulture) + " " + unit;

    private FrameworkElement ColorAndSlider(string color, Action<string> setColor, string sliderKey, double value, double min, double max, Action<double> set, string unit)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.Children.Add(Swatch(color, Loc.T(sliderKey), false, setColor, new Thickness(0, 0, 12, 0)));
        var row = SliderRow(sliderKey, value, min, max, set, unit, labelWidth: SliderLabelWidth - 38); // 26 de muestra + 12 de margen
        Grid.SetColumn(row, 1);
        g.Children.Add(row);
        return g;
    }

    private TextBox NumBox(double value, double min, double max, Action<double> set)
    {
        var tb = new TextBox { Style = (Style)FindResource("NumBox"), Width = 44, Text = value.ToString("0", CultureInfo.InvariantCulture) };
        void Commit()
        {
            if (double.TryParse(tb.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            {
                v = Math.Clamp(v, min, max);
                tb.Text = v.ToString("0.#", CultureInfo.InvariantCulture);
                set(v);
            }
            else tb.Text = value.ToString("0.#", CultureInfo.InvariantCulture);
            value = double.TryParse(tb.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var nv) ? nv : value;
        }
        tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); tb.SelectAll(); e.Handled = true; } };
        tb.LostFocus += (_, _) => Commit();
        return tb;
    }

    /// <summary>Muestra de color que abre el selector; los cambios se ven en vivo mientras se elige.</summary>
    private Button Swatch(string color, string title, bool allowAuto, Action<string> set, Thickness margin)
    {
        var b = new Button { Width = 26, Height = 26, Margin = margin, Cursor = Cursors.Hand, ToolTip = title, VerticalAlignment = VerticalAlignment.Center };
        void Paint(string c) => b.Template = SwatchTemplate(c);
        Paint(color);
        b.Click += (_, _) =>
        {
            var dlg = new ColorPickDialog(title, color, allowAuto, c => { set(c); Paint(c); }) { Owner = this };
            if (dlg.ShowDialog() == true) color = dlg.Result;
            set(color);
            Paint(color);
        };
        return b;
    }

    private static ControlTemplate SwatchTemplate(string color)
    {
        var t = new ControlTemplate(typeof(Button));
        var f = new FrameworkElementFactory(typeof(Border));
        f.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        f.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        f.SetValue(Border.BorderBrushProperty, Application.Current.FindResource("LineStrong"));
        if (color == "auto")
        {
            // "Automático": degradado verde → ámbar → rojo, como los colores por temperatura
            var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            g.GradientStops.Add(new GradientStop(Color.FromRgb(0x6C, 0xC2, 0x87), 0));
            g.GradientStops.Add(new GradientStop(Color.FromRgb(0xF5, 0x9E, 0x0B), 0.5));
            g.GradientStops.Add(new GradientStop(Color.FromRgb(0xE5, 0x48, 0x4D), 1));
            g.Freeze();
            f.SetValue(Border.BackgroundProperty, g);
            var a = new FrameworkElementFactory(typeof(TextBlock));
            a.SetValue(TextBlock.TextProperty, "A");
            a.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
            a.SetValue(TextBlock.FontSizeProperty, 11.0);
            a.SetValue(TextBlock.ForegroundProperty, Brushes.White);
            a.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            a.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            f.AppendChild(a);
        }
        else f.SetValue(Border.BackgroundProperty, OverlayCustomBuilder.Brush(color));
        t.VisualTree = f;
        return t;
    }

    private void ShowMenu(FrameworkElement anchor, IEnumerable<(string value, string label)> items, Action<string> pick)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        foreach (var (value, label) in items)
        {
            var mi = new MenuItem { Header = label };
            mi.Click += (_, _) => pick(value);
            menu.Items.Add(mi);
        }
        menu.IsOpen = true;
    }

    // ================================================================== lista de elementos
    /// <summary>El encabezado no está dentro del ScrollViewer: cuando aparece la barra de desplazamiento las filas
    /// pierden ese ancho por la derecha, así que el encabezado se ajusta igual para que las columnas coincidan.</summary>
    private void ElementsScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        double bar = Math.Max(0, ElementsScroll.ActualWidth - ElementsScroll.ViewportWidth);
        HeaderRow.Margin = new Thickness(6, 10, 6 + 6 + bar, 4); // relleno de fila + margen del panel + barra
    }

    private void BuildHeader()
    {
        HeaderRow.Children.Clear();
        HeaderRow.ColumnDefinitions.Clear();
        string[] keys = { "", "", "OvShow", "OvData", "OvLabel", "OvColors", "", "OvSize", "OvBold", "OvExtras", "" };
        for (int i = 0; i < Cols.Length; i++)
        {
            HeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = Cols[i] == 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(Cols[i]) });
            if (keys[i].Length == 0) continue;
            var t = new TextBlock { Text = Loc.T(keys[i]), Style = (Style)FindResource("Hint"), TextTrimming = TextTrimming.CharacterEllipsis,
                                    ToolTip = Loc.T(keys[i]), HorizontalAlignment = i >= 5 ? HorizontalAlignment.Center : HorizontalAlignment.Left };
            Grid.SetColumn(t, i);
            if (keys[i] == "OvColors") { Grid.SetColumnSpan(t, 2); t.HorizontalAlignment = HorizontalAlignment.Center; t.ToolTip = Loc.T("OvColorsTip"); }
            if (keys[i] == "OvExtras") { Grid.SetColumnSpan(t, 2); t.HorizontalAlignment = HorizontalAlignment.Center; t.ToolTip = Loc.T("OvExtrasTip"); }
            HeaderRow.Children.Add(t);
        }
    }

    private void BuildElements()
    {
        ElementsPanel.Children.Clear();
        for (int i = 0; i < _layout.Elements.Count; i++) ElementsPanel.Children.Add(ElementRow(_layout.Elements[i], i));
    }

    private Border ElementRow(OverlayElement el, int index)
    {
        var g = new Grid { Height = 36 };
        for (int i = 0; i < Cols.Length; i++)
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = Cols[i] == 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(Cols[i]) });
        void Put(UIElement c, int col) { Grid.SetColumn(c, col); g.Children.Add(c); }

        // Arrastrar para ordenar
        var handle = new TextBlock { Text = "≡", FontSize = 16, Foreground = (Brush)FindResource("Muted"), Cursor = Cursors.SizeAll,
                                     VerticalAlignment = VerticalAlignment.Center, ToolTip = Loc.T("OvDragHint") };
        Put(handle, 0);

        // Subir / bajar
        var updown = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        updown.Children.Add(MoveBtn("▲", index, -1));
        updown.Children.Add(MoveBtn("▼", index, +1));
        Put(updown, 1);

        var show = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = el.Visible, VerticalAlignment = VerticalAlignment.Center };
        show.Checked += (_, _) => { el.Visible = true; Changed(); };
        show.Unchecked += (_, _) => { el.Visible = false; Changed(); };
        Put(show, 2);

        Put(new TextBlock { Text = Loc.T("OvKey." + el.Key), Style = (Style)FindResource("Body"), VerticalAlignment = VerticalAlignment.Center,
                            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 8, 0) }, 3);

        var label = new TextBox { Style = (Style)FindResource("SearchBox"), Text = el.Label, MaxLength = 12, Margin = new Thickness(0, 0, 8, 0),
                                  VerticalAlignment = VerticalAlignment.Center };
        label.TextChanged += (_, _) => { el.Label = label.Text; Changed(); };
        Put(label, 4);

        var vc = Swatch(el.ValueColor, Loc.T("OvValueColor") + " · " + Loc.T("OvKey." + el.Key), true, c => { el.ValueColor = c; Changed(); }, default);
        vc.HorizontalAlignment = HorizontalAlignment.Center;
        Put(vc, 5);
        var lc = Swatch(el.LabelColor, Loc.T("OvLabelColor") + " · " + Loc.T("OvKey." + el.Key), false, c => { el.LabelColor = c; Changed(); }, default);
        lc.HorizontalAlignment = HorizontalAlignment.Center;
        Put(lc, 6);

        var size = NumBox(el.Size, 8, 48, v => { el.Size = v; Changed(); });
        size.HorizontalAlignment = HorizontalAlignment.Center;
        Put(size, 7);

        var bold = new ToggleButton { Style = (Style)FindResource("Toggle"), Content = new TextBlock { Text = "B", FontWeight = FontWeights.Bold }, IsChecked = el.Bold,
                                      ToolTip = Loc.T("OvBold") };
        bold.Checked += (_, _) => { el.Bold = true; Changed(); };
        bold.Unchecked += (_, _) => { el.Bold = false; Changed(); };
        Put(bold, 8);

        bool canBar = OverlayLayout.Catalog[el.Key].pct != null;
        var bar = new ToggleButton { Style = (Style)FindResource("Toggle"), Content = new TextBlock { Text = "▬" }, IsChecked = el.Bar && canBar,
                                     IsEnabled = canBar, ToolTip = Loc.T(canBar ? "OvBar" : "OvBarNa") };
        bar.Checked += (_, _) => { el.Bar = true; Changed(); };
        bar.Unchecked += (_, _) => { el.Bar = false; Changed(); };
        Put(bar, 9);

        bool canGraph = OverlayLayout.Series.ContainsKey(el.Key);
        var graph = new ToggleButton { Style = (Style)FindResource("Toggle"), Content = new TextBlock { Text = "∿", FontSize = 14 }, IsChecked = el.Graph && canGraph,
                                       IsEnabled = canGraph, ToolTip = Loc.T(canGraph ? "OvGraph" : "OvGraphNa") };
        graph.Checked += (_, _) => { el.Graph = true; Changed(); };
        graph.Unchecked += (_, _) => { el.Graph = false; Changed(); };
        Put(graph, 10);

        var row = new Border { Child = g, CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 0, 6, 0), Background = Brushes.Transparent,
                               Tag = el, Opacity = el.Visible ? 1 : 0.6 };
        show.Checked += (_, _) => row.Opacity = 1;
        show.Unchecked += (_, _) => row.Opacity = 0.6;
        row.MouseEnter += (_, _) => { if (_dragRow == null) row.Background = (Brush)FindResource("Hover"); };
        row.MouseLeave += (_, _) => { if (!ReferenceEquals(_dragRow, row)) row.Background = Brushes.Transparent; };

        // Arrastre en vivo: la fila se levanta y sigue al ratón; las demás se apartan con animación.
        row.RenderTransform = new TranslateTransform();
        handle.MouseLeftButtonDown += (_, e) => BeginDrag(row, handle, index, e);
        handle.MouseMove += (_, e) => DragMove(e);
        handle.MouseLeftButtonUp += (_, _) => EndDrag();
        handle.LostMouseCapture += (_, _) => EndDrag();
        return row;
    }

    // ------------------------------------------------------------------ arrastre en vivo
    private const double RowHeight = 36;
    private Border? _dragRow;
    private int _dragFrom, _dragTo;
    private double _dragStartY;

    private void BeginDrag(Border row, UIElement handle, int index, MouseButtonEventArgs e)
    {
        _dragRow = row;
        _dragFrom = _dragTo = index;
        _dragStartY = e.GetPosition(ElementsPanel).Y;
        Panel.SetZIndex(row, 10);
        row.Background = (Brush)FindResource("SurfaceAlt");
        row.Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 3, Opacity = 0.55, Color = Colors.Black };
        row.Opacity = 1;
        handle.CaptureMouse();
        e.Handled = true;
    }

    private void DragMove(MouseEventArgs e)
    {
        if (_dragRow == null) return;
        double dy = e.GetPosition(ElementsPanel).Y - _dragStartY;
        int count = ElementsPanel.Children.Count;
        // La fila arrastrada no se sale de la lista
        dy = Math.Clamp(dy, -_dragFrom * RowHeight, (count - 1 - _dragFrom) * RowHeight);
        ((TranslateTransform)_dragRow.RenderTransform).Y = dy;

        int to = Math.Clamp(_dragFrom + (int)Math.Round(dy / RowHeight), 0, count - 1);
        if (to == _dragTo) return;
        _dragTo = to;
        // Las filas entre el origen y el destino se desplazan una posición para dejar el hueco
        for (int i = 0; i < count; i++)
        {
            if (i == _dragFrom || ElementsPanel.Children[i] is not Border other) continue;
            double shift = _dragFrom < to && i > _dragFrom && i <= to ? -RowHeight
                         : _dragFrom > to && i >= to && i < _dragFrom ? RowHeight : 0;
            var tt = (TranslateTransform)other.RenderTransform;
            tt.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(shift, TimeSpan.FromMilliseconds(140)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
    }

    private void EndDrag()
    {
        if (_dragRow == null) return;
        var row = _dragRow;
        _dragRow = null;
        Mouse.Capture(null);
        int from = _dragFrom, to = _dragTo;
        if (from != to)
        {
            var el = _layout.Elements[from];
            _layout.Elements.RemoveAt(from);
            _layout.Elements.Insert(to, el);
            BuildElements(); // reconstruye en el nuevo orden (sin transformaciones)
            Changed();
        }
        else
        {
            // Soltó en su sitio: vuelve a su posición con una pequeña animación
            row.Effect = null;
            row.Background = Brushes.Transparent;
            row.Opacity = row.Tag is OverlayElement oe && !oe.Visible ? 0.6 : 1;
            Panel.SetZIndex(row, 0);
            ((TranslateTransform)row.RenderTransform).BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(0, TimeSpan.FromMilliseconds(120)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
    }

    private Button MoveBtn(string glyph, int index, int delta)
    {
        var b = new Button { Content = glyph, Width = 22, Height = 22, Margin = new Thickness(0, 0, 2, 0), Cursor = Cursors.Hand, FontSize = 9,
                             Foreground = (Brush)FindResource("Muted"), Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                             IsEnabled = index + delta >= 0 && index + delta < _layout.Elements.Count };
        b.Template = FlatTemplate();
        b.Click += (_, _) =>
        {
            int to = index + delta;
            if (to < 0 || to >= _layout.Elements.Count) return;
            (_layout.Elements[index], _layout.Elements[to]) = (_layout.Elements[to], _layout.Elements[index]);
            BuildElements();
            Changed();
        };
        return b;
    }

    private static ControlTemplate FlatTemplate()
    {
        var t = new ControlTemplate(typeof(Button));
        var f = new FrameworkElementFactory(typeof(Border));
        f.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        f.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        f.AppendChild(cp);
        t.VisualTree = f;
        var disabled = new Trigger { Property = IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(OpacityProperty, 0.25));
        t.Triggers.Add(disabled);
        return t;
    }

    // ================================================================== cabecera
    private void Presets_Click(object sender, RoutedEventArgs e) =>
        ShowMenu(PresetsBtn, OverlayLayout.PresetKeys.Select(k => (k, Loc.T("OvPreset." + k))), k =>
        {
            _layout = OverlayLayout.Preset(k);
            BuildStyle();
            BuildElements();
            Changed();
        });

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "HwPulse overlay (*.json)|*.json", FileName = "hwpulse-overlay.json" };
        if (dlg.ShowDialog(this) != true) return;
        try { File.WriteAllText(dlg.FileName, _layout.ToJson()); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "HwPulse"); }
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "HwPulse overlay (*.json)|*.json" };
        if (dlg.ShowDialog(this) != true) return;
        OverlayLayout? imported = null;
        try
        {
            // Archivo compartido por otra persona: tamaño acotado y validación completa (Normalize) al leerlo.
            var fi = new FileInfo(dlg.FileName);
            if (fi.Length <= 64 * 1024) imported = OverlayLayout.FromJson(File.ReadAllText(dlg.FileName));
        }
        catch { }
        if (imported == null) { MessageBox.Show(this, Loc.T("OvImportFailed"), "HwPulse"); return; }
        _layout = imported;
        BuildStyle();
        BuildElements();
        Changed();
    }

    private void Done_Click(object sender, RoutedEventArgs e) => Close();

#if DEBUG
    // ------------------------------------------------------------------ prueba automática (--editor-selftest)
    /// <summary>Recorre mover → deshacer → rehacer → posición por defecto → cambiar diseño → cancelar, sin ratón,
    /// y devuelve un informe con el resultado de cada paso.</summary>
    internal string SelfTest()
    {
        var log = new System.Text.StringBuilder();
        void Check(string step, bool ok, string detail) => log.AppendLine($"{(ok ? "PASS" : "FAIL")}  {step,-34} {detail}");
        string Pos() => _vm.Corner == OverlayCorner.Custom ? $"Custom({_vm.OverlayX:0.00},{_vm.OverlayY:0.00})" : _vm.Corner.ToString();
        var startCorner = _vm.Corner; double startSpacing = _layout.Spacing;

        _vm.SetCustomPosition(0.5, 0.4);
        Check("move overlay (like a drag)", _vm.Corner == OverlayCorner.Custom && UndoBtn.IsEnabled, Pos());
        Undo_Click(this, new RoutedEventArgs());
        Check("undo restores position", _vm.Corner == startCorner, Pos());
        Redo_Click(this, new RoutedEventArgs());
        Check("redo moves it again", _vm.Corner == OverlayCorner.Custom && Math.Abs(_vm.OverlayX - 0.5) < 1e-9, Pos());
        _vm.SetCorner(OverlayCorner.TopCenter); // botón "Posición por defecto"
        Check("default position button", _vm.Corner == OverlayCorner.TopCenter, Pos());
        Undo_Click(this, new RoutedEventArgs());
        Check("undo the reset", _vm.Corner == OverlayCorner.Custom, Pos());
        _lastRecord = DateTime.MinValue;
        _layout.Spacing = startSpacing + 7; Changed();
        Check("design change applied", Math.Abs(_vm.CustomLayout.Spacing - (startSpacing + 7)) < 1e-9, $"spacing {_vm.CustomLayout.Spacing}");
        Restore(_initial);
        Check("cancel restores design", Math.Abs(_vm.CustomLayout.Spacing - startSpacing) < 1e-9, $"spacing {_vm.CustomLayout.Spacing}");
        Check("cancel restores position", _vm.Corner == startCorner, Pos());
        return log.ToString();
    }
#endif
}
