using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace HwPulse;

/// <summary>Pregunta qué hacer con la X: mandar a la bandeja o cerrar del todo (con opción de recordarlo).</summary>
public partial class CloseDialog : Window
{
    /// <summary>"tray", "exit" o null si se canceló (Esc / cerrar el diálogo).</summary>
    public string? Choice { get; private set; }
    public bool RememberChoice => Remember.IsChecked == true;

    public CloseDialog()
    {
        InitializeComponent();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Loaded += (_, _) => TrayBtn.Focus();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Native.StyleWindowFrame(new WindowInteropHelper(this).Handle, 0x00262626);
    }

    private void Tray_Click(object sender, RoutedEventArgs e) { Choice = "tray"; Close(); }
    private void Exit_Click(object sender, RoutedEventArgs e) { Choice = "exit"; Close(); }
}
