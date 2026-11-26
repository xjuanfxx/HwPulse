using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace HwPulse;

/// <summary>Pregunta qué hacer con la X: mandar a la bandeja o cerrar del todo (con opción de recordarlo).</summary>
public partial class CloseDialog : Window
{
    /// <summary>"tray", "exit" o null si se canceló (Esc o la X de la cabecera).</summary>
    public string? Choice { get; private set; }
    public bool RememberChoice => Remember.IsChecked == true;

    public CloseDialog()
    {
        InitializeComponent();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        // SizeToContent mide la ventana como si el marco nativo siguiera existiendo, pero el
        // NCCALCSIZE de WindowChrome lo elimina: sobra una franja abajo/derecha (a 125 % de DPI,
        // ~45 y ~16 px) que sin glass se pintaba negra (invisible) y con la hoja DWM extendida
        // muestra el relleno del marco (#2B2B2B). La encogemos al tamaño real del contenido.
        Loaded += FixFrame;
        // El foco dado en Loaded se pierde cuando Windows activa la ventana; en ContentRendered
        // ya está activa y el anillo de foco del botón recomendado (bandeja) se ve al abrir.
        ContentRendered += (_, _) => TrayBtn.Focus();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Native.StyleWindowFrame(new WindowInteropHelper(this).Handle, 0x00262626);
    }

    /// <summary>
    /// Encoge la ventana al tamaño exacto del contenido y la centra sobre la ventana principal.
    /// No se puede fiar de CenterOwner: WPF calcula la posición con el tamaño que incluye el marco
    /// fantasma (y en coordenadas que no cuadran con el DPI real), así que el diálogo quedaba
    /// desplazado a la izquierda o incluso medio fuera de la ventana. Todo se hace en píxeles físicos.
    /// </summary>
    private void FixFrame(object sender, RoutedEventArgs e)
    {
        var target = PresentationSource.FromVisual(this)?.CompositionTarget;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (target == null || hwnd == IntPtr.Zero) return;

        var m = target.TransformToDevice;
        int w = (int)Math.Ceiling(Root.ActualWidth * m.M11);
        int h = (int)Math.Ceiling(Root.ActualHeight * m.M22);
        if (w <= 0 || h <= 0) return;

        // Rectángulo de referencia: la ventana dueña si está visible; si no, el monitor del diálogo.
        var owner = Owner is { IsVisible: true, WindowState: not WindowState.Minimized }
            ? new WindowInteropHelper(Owner).Handle : IntPtr.Zero;
        var area = WorkArea(owner != IntPtr.Zero ? owner : hwnd);
        RECT refRect = owner != IntPtr.Zero && GetWindowRect(owner, out var o) ? o : area;

        int x = refRect.Left + (refRect.Right - refRect.Left - w) / 2;
        int y = refRect.Top + (refRect.Bottom - refRect.Top - h) / 2;
        // Que nunca quede fuera de la pantalla (ventana principal pegada a un borde o medio fuera).
        x = Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - w));
        y = Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - h));

        // Con SizeToContent activo, WPF re-impone su tamaño (con el marco fantasma) al procesar el WM_SIZE
        // del encogido: hay que apagarlo ANTES de tocar la ventana nativa. El contenido de este diálogo es
        // fijo tras cargar, así que la medición automática ya no hace falta.
        SizeToContent = SizeToContent.Manual;
        Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h, 0x0004); // SWP_NOZORDER
    }

    /// <summary>Área de trabajo (sin barra de tareas) del monitor donde está la ventana, en píxeles físicos.</summary>
    private static RECT WorkArea(IntPtr hwnd)
    {
        var mon = MonitorFromWindow(hwnd, 2); // MONITOR_DEFAULTTONEAREST
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (mon != IntPtr.Zero && GetMonitorInfo(mon, ref info)) return info.rcWork;
        return new RECT { Left = 0, Top = 0, Right = (int)SystemParameters.PrimaryScreenWidth, Bottom = (int)SystemParameters.PrimaryScreenHeight };
    }

    private void Tray_Click(object sender, RoutedEventArgs e) { Choice = "tray"; Close(); }
    private void Exit_Click(object sender, RoutedEventArgs e) { Choice = "exit"; Close(); }
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close(); // Choice queda en null: cancelado

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public int dwFlags; }
}
