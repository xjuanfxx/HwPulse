using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace HwPulse;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly (RadioButton nav, FrameworkElement page, string title, string sub)[] _pages;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;

        _pages = new (RadioButton, FrameworkElement, string, string)[]
        {
            (NavOverview, PageOverview, "Overview", "OverviewSub"),
            (NavCpu, PageCpu, "CPU", "CpuSub"),
            (NavGpu, PageGpu, "GPU", "GpuSub"),
            (NavMemory, PageMemory, "Memory", "MemorySub"),
            (NavStorage, PageStorage, "Storage", "StorageSub"),
            (NavNetwork, PageNetwork, "Network", "NetworkSub"),
            (NavSensors, PageSensors, "Sensors", "SensorsSub"),
            (NavSettings, PageSettings, "Settings", "SettingsSub"),
        };

        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.OverlayVisible) or "" or null) UpdateOverlayDot();
        };
        Loc.I.PropertyChanged += (_, _) => UpdatePages();
        UpdateOverlayDot();
        UpdatePages();
        StateChanged += (_, _) =>
        {
            Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
            if (WindowState == WindowState.Minimized)
            {
                // Minimizar la manda a la bandeja (sin botón en la barra de tareas).
                Hide();
                CurrentApp.NotifyHiddenToTray();
            }
            else RestoreState = WindowState;
            UpdateUiVisible();
        };
        // Oculta en la bandeja o minimizada: el modelo deja de calcular lo que solo se ve aquí.
        IsVisibleChanged += (_, _) => UpdateUiVisible();
    }

    /// <summary>Estado al que vuelve desde la bandeja (normal o maximizada).</summary>
    public WindowState RestoreState { get; private set; } = WindowState.Normal;

    private static App CurrentApp => (App)Application.Current;

    private void UpdateUiVisible() => _vm.UiVisible = IsVisible && WindowState != WindowState.Minimized;

    public FrameworkElement PageFor(RadioButton nav) => _pages.First(p => p.nav == nav).page;

    /// <summary>Páginas en orden, para las capturas de desarrollo.</summary>
    public IEnumerable<(RadioButton nav, string key)> Pages => _pages.Select(p => (p.nav, p.title));

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Borde #24282D en formato COLORREF (0x00BBGGRR).
        Native.StyleWindowFrame(new WindowInteropHelper(this).Handle, 0x00262626);
    }

    private void UpdateOverlayDot() =>
        OverlayDot.Fill = (Brush)FindResource(_vm.OverlayVisible ? "Accent" : "Faint");

    private void Nav_Checked(object sender, RoutedEventArgs e) => UpdatePages();

    private void UpdatePages()
    {
        if (_pages == null) return; // durante InitializeComponent
        foreach (var (nav, page, title, sub) in _pages)
        {
            bool on = nav.IsChecked == true;
            page.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (on)
            {
                PageTitle.Text = Loc.T(title);
                PageSub.Text = Loc.T(sub);
                _vm.CurrentPage = title;
            }
        }
    }

    private void ProcSort_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        _vm.SetProcessSort((sender as FrameworkElement)?.Tag as string == "mem");

    /// <summary>Enter aplica el valor escrito; texto no numérico se descarta y vuelve el valor actual.</summary>
    private void NumBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter || sender is not TextBox tb) return;
        var binding = tb.GetBindingExpression(TextBox.TextProperty);
        if (int.TryParse(tb.Text.Trim().TrimEnd('%'), out _)) binding?.UpdateSource();
        binding?.UpdateTarget();
        tb.SelectAll();
        e.Handled = true;
    }

    private void ResetTemps_Click(object sender, RoutedEventArgs e) => _vm.ResetTempChoices();

    private void Colors_Click(object sender, RoutedEventArgs e) => CurrentApp.OpenColors();

    private void OpenWeb_Click(object sender, RoutedEventArgs e) => CurrentApp.OpenWeb();

    private void Update_Click(object sender, RoutedEventArgs e) => CurrentApp.UpdateClicked();
    private void UpdateNotes_Click(object sender, RoutedEventArgs e) => CurrentApp.OpenUpdateNotes();
    private void UpdateDismiss_Click(object sender, RoutedEventArgs e) => CurrentApp.DismissUpdateBanner();

    private void OverlayQuick_Click(object sender, RoutedEventArgs e) => _vm.OverlayVisible = !_vm.OverlayVisible;
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>La X pregunta (o hace lo recordado): bandeja o salir del todo.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!CurrentApp.IsExiting)
        {
            e.Cancel = true;
            string action = CurrentApp.CloseAction;
            if (action == "ask")
            {
                var dlg = new CloseDialog { Owner = this };
                dlg.ShowDialog();
                if (dlg.Choice == null) return; // cancelado: la ventana sigue abierta
                action = dlg.Choice;
                if (dlg.RememberChoice) CurrentApp.CloseAction = action;
            }
            // Diferido: ExitApp cierra esta ventana, y no se puede llamar a Close() dentro de OnClosing.
            if (action == "exit") { Dispatcher.BeginInvoke(CurrentApp.ExitApp); return; }
            Hide();
            CurrentApp.NotifyHiddenToTray();
        }
        base.OnClosing(e);
    }
}
