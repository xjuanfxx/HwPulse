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

    /// <summary>Encoge la ventana al tamaño exacto del contenido (el cliente que WPF cree tener
    /// desde el principio) manteniéndola centrada donde estaba.</summary>
    private void FixFrame(object sender, RoutedEventArgs e)
    {
        var target = PresentationSource.FromVisual(this)?.CompositionTarget;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (target == null || !GetWindowRect(hwnd, out var r)) return;

        var m = target.TransformToDevice;
        int w = (int)Math.Ceiling(Root.ActualWidth * m.M11);
        int h = (int)Math.Ceiling(Root.ActualHeight * m.M22);

        int dx = (r.Right - r.Left - w) / 2, dy = (r.Bottom - r.Top - h) / 2;
        if (dx <= 0 && dy <= 0) return; // sin franja que recortar
        // Con SizeToContent activo, WPF re-impone su tamaño (con el marco fantasma) al procesar el WM_SIZE
        // del encogido: hay que apagarlo ANTES de tocar la ventana nativa. El contenido de este diálogo es
        // fijo tras cargar, así que la medición automática ya no hace falta.
        SizeToContent = SizeToContent.Manual;
        // SWP_NOZORDER: solo tamaño y posición, re-centrada sobre el dueño
        Native.SetWindowPos(hwnd, IntPtr.Zero, r.Left + Math.Max(dx, 0), r.Top + Math.Max(dy, 0), w, h, 0x0004);
    }

    private void Tray_Click(object sender, RoutedEventArgs e) { Choice = "tray"; Close(); }
    private void Exit_Click(object sender, RoutedEventArgs e) { Choice = "exit"; Close(); }
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close(); // Choice queda en null: cancelado

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }
}
