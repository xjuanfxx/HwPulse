using System.Windows;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace HwPulse;

/// <summary>
/// Ventana transparente, siempre encima y que deja pasar los clics.
/// Funciona sobre juegos en ventana o "ventana sin bordes"; no en pantalla completa exclusiva
/// (para eso habría que inyectarse en DirectX, que es lo que los anti-cheat detectan).
/// </summary>
public partial class OverlayWindow : Window
{
    private const double MarginPx = 14;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _topmostTimer;
    private IntPtr _hwnd;

    public OverlayWindow(MainViewModel vm, AppSettings settings)
    {
        InitializeComponent();
        DataContext = vm;
        _settings = settings;
        SizeChanged += (_, _) => Reposition();

        // Los juegos a veces se ponen por encima: reafirmamos "siempre encima" cada 2 s.
        _topmostTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _topmostTimer.Tick += (_, _) =>
        {
            if (IsVisible && _hwnd != IntPtr.Zero)
                Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        };
        _topmostTimer.Start();

        // Supermuestreo: el contenido real (el XAML) se saca de la ventana y se dibuja fuera de pantalla
        // al doble de resolución; la ventana solo muestra esa imagen reducida con filtro de calidad.
        // En ventanas transparentes WPF no puede usar ClearType y su suavizado de texto queda dentado;
        // así cada píxel final promedia 4 y los bordes salen finos.
        _source = (FrameworkElement)Content;
        Content = _surface;
        _source.DataContext = vm;
        _source.SetValue(TextElement.FontFamilyProperty, FontFamily);
        _source.SetValue(TextElement.FontSizeProperty, FontSize);
        _source.SetValue(TextElement.FontWeightProperty, FontWeight);
        // Color de los valores: configurable desde "Personalizar colores"
        System.Windows.Data.BindingOperations.SetBinding(_source, TextElement.ForegroundProperty,
            new System.Windows.Data.Binding(nameof(MainViewModel.OvTextBrush)));
        TextOptions.SetTextFormattingMode(_source, TextFormattingMode.Ideal);
        RenderOptions.SetBitmapScalingMode(_surface, BitmapScalingMode.HighQuality);
        _surface.Stretch = System.Windows.Media.Stretch.Fill;

        vm.PropertyChanged += (_, _) => QueueRender();
        CaptureBaseSizes(_source);
        ApplyScale(settings.OverlayScale);
        Loaded += (_, _) => QueueRender();
        IsVisibleChanged += (_, _) => QueueRender();
        DpiChanged += (_, _) => QueueRender();
    }

    // ------------------------------------------------------------------ dibujo supermuestreado
    private const double Supersample = 2;
    private readonly FrameworkElement _source;
    private readonly System.Windows.Controls.Image _surface = new() { SnapsToDevicePixels = true };
    private bool _renderQueued;

    private RenderTargetBitmap? _bitmap;

    private void QueueRender()
    {
        if (_renderQueued) return;
        _renderQueued = true;
        // Prioridad "Render": se agrupan los cambios del mismo instante (varios bindings) en un solo dibujo,
        // pero sin esperar a que termine la interacción. Con prioridad Background el overlay se quedaba
        // congelado mientras arrastrabas un slider y saltaba al soltarlo.
        Dispatcher.BeginInvoke(DispatcherPriority.Render, Render);
    }

    private void Render()
    {
        _renderQueued = false;
        if (!IsVisible) return;
        // "Solo mientras juego": sin FPS detectados el overlay queda invisible (y no se dibuja: cero coste).
        // En modo mover se ve siempre, para poder colocarlo.
        bool hide = _settings.OverlayOnlyInGame && !_moveMode && DataContext is MainViewModel { FpsActive: false };
        Opacity = hide ? 0 : 1;
        if (hide) return;
        RenderCore();
    }

    // ------------------------------------------------------------------ mover con el ratón
    private bool _moveMode;
    private static readonly Brush MoveHighlight = CreateMoveHighlight();
    private static Brush CreateMoveHighlight() { var b = new SolidColorBrush(Color.FromArgb(0x40, 0x5B, 0x9B, 0xD5)); b.Freeze(); return b; }

    /// <summary>
    /// Activa/desactiva el modo de colocación: el overlay deja de ignorar los clics (quita WS_EX_TRANSPARENT),
    /// se resalta y se puede arrastrar. Al soltar se guarda la posición libre en fracción de la pantalla.
    /// </summary>
    public void SetMoveMode(bool on)
    {
        if (_moveMode == on || _hwnd == IntPtr.Zero) return;
        _moveMode = on;
        int ex = Native.GetWindowLong(_hwnd, Native.GWL_EXSTYLE);
        Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE, on ? ex & ~Native.WS_EX_TRANSPARENT : ex | Native.WS_EX_TRANSPARENT);
        // El editor se abre como ventana modal y WPF deshabilita el resto de ventanas de la app mientras tanto
        // (el overlay incluido): sin esto no recibiría el clic para arrastrarlo. Al cerrar el editor, WPF las
        // vuelve a habilitar; y el overlay deja pasar los clics igualmente (WS_EX_TRANSPARENT).
        if (on) Native.EnableWindow(_hwnd, true);
        Background = on ? MoveHighlight : Brushes.Transparent;
        Cursor = on ? System.Windows.Input.Cursors.SizeAll : null;
        QueueRender();
    }

    protected override void OnMouseLeftButtonDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!_moveMode) return;
        try { DragMove(); } catch (InvalidOperationException) { return; }
        double sw = SystemParameters.PrimaryScreenWidth, sh = SystemParameters.PrimaryScreenHeight;
        if (DataContext is MainViewModel vm && sw > 0 && sh > 0) vm.SetCustomPosition(Left / sw, Top / sh);
    }

    /// <summary>Solo para las capturas de desarrollo (--render): mide y dibuja el contenido desmontado
    /// aunque la ventana esté oculta. Si el overlay nunca se mostró, nadie midió ese contenido (mide 0)
    /// y capturarlo con RenderTargetBitmap revienta.</summary>
    public void RenderNow()
    {
        _renderQueued = false;
        RenderCore();
    }

    private void RenderCore()
    {
        _source.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _source.Arrange(new Rect(_source.DesiredSize));
        _source.UpdateLayout();
        // DesiredSize incluye el margen del contenido (ActualWidth/Height no): si usáramos Actual*, la imagen
        // quedaría más chica que lo dibujado y se cortaría el borde inferior.
        double w = Math.Ceiling(_source.DesiredSize.Width), h = Math.Ceiling(_source.DesiredSize.Height);
        if (w < 1 || h < 1) return;

        var dpi = VisualTreeHelper.GetDpi(this);
        double k = dpi.DpiScaleX * Supersample;
        int pw = (int)Math.Ceiling(w * k), ph = (int)Math.Ceiling(h * dpi.DpiScaleY * Supersample);

        // Se reutiliza la misma imagen mientras no cambie el tamaño. Crear una nueva cada segundo dejaba
        // memoria nativa pendiente de liberar (RenderTargetBitmap no presiona al recolector de basura).
        if (_bitmap == null || _bitmap.PixelWidth != pw || _bitmap.PixelHeight != ph)
        {
            _bitmap = new RenderTargetBitmap(pw, ph, 96 * k, 96 * dpi.DpiScaleY * Supersample, PixelFormats.Pbgra32);
            _surface.Source = _bitmap;
        }
        else _bitmap.Clear();
        _bitmap.Render(_source);
        _surface.Width = w;
        _surface.Height = h;
    }

    /// <summary>Para las capturas de desarrollo: el contenido real, no la imagen.</summary>
    public FrameworkElement SourceContent => _source;

    // ------------------------------------------------------------------ escala
    // Guardamos el tamaño original de cada fuente, margen y medida, y al cambiar el tamaño del overlay
    // los multiplicamos. El texto se rasteriza a su tamaño final (nítido), cosa que una ScaleTransform no hace.
    private readonly List<Action<double>> _scalers = new();
    private double _appliedScale = double.NaN;

    private static Thickness Mul(Thickness t, double s) => new(t.Left * s, t.Top * s, t.Right * s, t.Bottom * s);

    private void CaptureBaseSizes(DependencyObject node)
    {
        if (node is System.Windows.Controls.TextBlock tb)
        {
            double f = tb.FontSize;
            _scalers.Add(s => tb.FontSize = Math.Round(f * s * 2) / 2);
        }
        if (node is FrameworkElement fe)
        {
            double w = fe.Width, h = fe.Height, mw = fe.MinWidth;
            var m = fe.Margin;
            if (!double.IsNaN(w)) _scalers.Add(s => fe.Width = w * s);
            if (!double.IsNaN(h)) _scalers.Add(s => fe.Height = h * s);
            if (mw > 0) _scalers.Add(s => fe.MinWidth = mw * s);
            if (m != default) _scalers.Add(s => fe.Margin = Mul(m, s));
        }
        if (node is System.Windows.Controls.Border b)
        {
            var p = b.Padding;
            var cr = b.CornerRadius;
            _scalers.Add(s =>
            {
                b.Padding = Mul(p, s);
                b.CornerRadius = new CornerRadius(cr.TopLeft * s, cr.TopRight * s, cr.BottomRight * s, cr.BottomLeft * s);
            });
        }
        if (node is System.Windows.Controls.Grid g)
        {
            foreach (var rd in g.RowDefinitions)
                if (rd.Height.IsAbsolute) { double v = rd.Height.Value; _scalers.Add(s => rd.Height = new GridLength(v * s)); }
            foreach (var cd in g.ColumnDefinitions)
            {
                if (cd.Width.IsAbsolute) { double v = cd.Width.Value; _scalers.Add(s => cd.Width = new GridLength(v * s)); }
                if (cd.MinWidth > 0) { double v = cd.MinWidth; _scalers.Add(s => cd.MinWidth = v * s); }
            }
        }
        foreach (var child in LogicalTreeHelper.GetChildren(node))
            if (child is DependencyObject d) CaptureBaseSizes(d);
    }

    public void ApplyScale(double scale)
    {
        if (scale != _appliedScale)
        {
            _appliedScale = scale;
            foreach (var a in _scalers) a(scale);
        }
        RebuildCustom();
        if (_source != null) QueueRender();
    }

    /// <summary>Reconstruye el overlay personalizado (diseño o tamaño cambiado). Solo si ese estilo está activo.</summary>
    public void RebuildCustom()
    {
        if (_settings.OverlayStyle != "custom") { CustomHost.Content = null; return; }
        var layout = _settings.OverlayLayout ?? OverlayLayout.Preset("line");
        CustomHost.Content = OverlayCustomBuilder.Build(layout, double.IsNaN(_appliedScale) ? 1 : _appliedScale);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        int ex = Native.GetWindowLong(_hwnd, Native.GWL_EXSTYLE);
        Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE,
            ex | Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
        // El overlay se dibuja por CPU (es una imagen pequeña que cambia 1 vez por segundo): así no compite
        // con el juego por la GPU. Las ventanas transparentes de WPF ya copian a memoria igualmente.
        if (HwndSource.FromHwnd(_hwnd)?.CompositionTarget is HwndTarget target)
            target.RenderMode = RenderMode.SoftwareOnly;
        Reposition();
    }

    public void Reposition()
    {
        // Pantalla principal completa (no el área de trabajo): los juegos tapan la barra de tareas.
        double sw = SystemParameters.PrimaryScreenWidth, sh = SystemParameters.PrimaryScreenHeight;
        double w = ActualWidth, h = ActualHeight;
        (Left, Top) = _settings.Corner switch
        {
            OverlayCorner.TopCenter => (Math.Round((sw - w) / 2), MarginPx),
            OverlayCorner.TopLeft => (MarginPx, MarginPx),
            OverlayCorner.TopRight => (sw - w - MarginPx, MarginPx),
            OverlayCorner.BottomRight => (sw - w - MarginPx, sh - h - MarginPx),
            OverlayCorner.BottomCenter => (Math.Round((sw - w) / 2), sh - h - MarginPx),
            // Posición libre: guardada en fracción de pantalla y siempre dentro de ella (cambio de resolución)
            OverlayCorner.Custom => (Math.Clamp(Math.Round(_settings.OverlayX * sw), 0, Math.Max(0, sw - w)),
                                     Math.Clamp(Math.Round(_settings.OverlayY * sh), 0, Math.Max(0, sh - h))),
            _ => (MarginPx, sh - h - MarginPx),
        };
    }

    protected override void OnClosed(EventArgs e)
    {
        _topmostTimer.Stop();
        base.OnClosed(e);
    }
}
