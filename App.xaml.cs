using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;

namespace HwPulse;

public partial class App : Application
{
    private const int HotkeyToggle = 1, HotkeyCorner = 2;

    private AppSettings _settings = null!;
    private MainViewModel _vm = null!;
    private SensorService? _sensors;
    private MainWindow? _main;
    private OverlayWindow? _overlay;
    private WinForms.NotifyIcon? _tray;
    private HwndSource? _hotkeys;
    private Mutex? _mutex;
    private readonly CancellationTokenSource _cts = new();
    private readonly ProcessMonitor _procMon = new();
    private readonly GpuProcessMonitor _gpuMon = new();
    private volatile FpsMonitor? _fps; // escrito por la tarea de EnsureFpsMonitor, leído por UI y ProcessExit
    private bool _extrasStarted;
    private Task<SensorService>? _sensorsTask;
    private int _procTick;

    /// <summary>Recursos que se están creando en segundo plano (sensores, sesión ETW de FPS) antes de
    /// asignarse a su campo. Si la app se cierra antes de que la tarea termine, ExitApp/ProcessExit los
    /// liberan igualmente: sin esto quedaría un Computer de LibreHardwareMonitor abierto (con los módulos
    /// de PawnIO cargados) o una sesión ETW activa tras la salida.</summary>
    private readonly List<IDisposable> _pendingResources = new();

    public bool IsExiting { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ErrorLog.Install(this);
        // Si Windows cierra la app (apagado, fin de sesión, kill), liberar todo lo que quede vivo: la sesión
        // ETW de FPS, el Computer de LibreHardwareMonitor (descarga los módulos de PawnIO), el panel web y
        // cualquier recurso que siga abriéndose en segundo plano.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { _fps?.Dispose(); } catch { }
            try { _sensors?.Dispose(); } catch { }
            try { _web?.Dispose(); } catch { }
            try
            {
                lock (_pendingResources)
                {
                    foreach (var d in _pendingResources) { try { d.Dispose(); } catch { } }
                    _pendingResources.Clear();
                }
            }
            catch { }
        };
        var args = e.Args;
        bool admin = Native.IsAdmin();

        // Sin admin no hay temperaturas ni relojes del CPU: nos relanzamos elevados (UAC).
        if (!admin && !args.Contains("--no-elevate") && TryRelaunchElevated(args))
        {
            Shutdown();
            return;
        }

        int benchIdx = Array.IndexOf(args, "--bench");
        if (benchIdx >= 0 && benchIdx + 1 < args.Length)
        {
            File.WriteAllText(args[benchIdx + 1], SensorService.Bench());
            Shutdown();
            return;
        }

        int dumpIdx = Array.IndexOf(args, "--dump");
        if (dumpIdx >= 0 && dumpIdx + 1 < args.Length)
        {
            RunDump(args[dumpIdx + 1], admin);
            Shutdown();
            return;
        }

        _settings = AppSettings.Load();
        Loc.I.SetLanguage(_settings.Language);

        // --font "Nombre": prueba otra tipografía (desarrollo).
        int fontIdx = Array.IndexOf(args, "--font");
        if (fontIdx >= 0 && fontIdx + 1 < args.Length)
        {
            var ff = new System.Windows.Media.FontFamily(args[fontIdx + 1]);
            Resources["UiFont"] = ff;
            Resources["NumFont"] = ff;
        }

        _mutex = new Mutex(true, "HwPulse.SingleInstance", out bool created);
        // Recién actualizada: la instancia anterior aún está cerrándose, se espera a que suelte el mutex.
        if (!created && args.Contains("--updated"))
        {
            try { created = _mutex.WaitOne(TimeSpan.FromSeconds(15)); }
            catch (AbandonedMutexException) { created = true; }
        }
        if (!created)
        {
            MessageBox.Show(Loc.T("AlreadyRunning"), "HwPulse");
            Shutdown();
            return;
        }

        // Los sensores empiezan a abrirse YA, en paralelo con la creación de las ventanas. El recurso se
        // registra al crearse: si la app se cierra antes de que StartSensorsAsync lo asigne a _sensors,
        // ExitApp/ProcessExit lo liberan igualmente (Computer.Close descarga los módulos de PawnIO).
        _sensorsTask = Task.Run(() =>
        {
            var s = new SensorService(admin);
            lock (_pendingResources) _pendingResources.Add(s);
            return s;
        });

        _vm = new MainViewModel(_settings, admin);
        _vm.OverlayVisibilityChanged = OnOverlayVisibilityChanged;
        _vm.OverlayLayoutChanged = OnOverlayLayoutChanged;
        _vm.LanguageChanged = _ => { BuildTrayMenu(); SaveSoon(); };

        // La ventana se muestra YA, con valores vacíos; los sensores se abren después en segundo plano.
        _main = new MainWindow(_vm);
        _overlay = new OverlayWindow(_vm, _settings);
        if (!args.Contains("--minimized")) _main.Show();
        if (_settings.OverlayVisible) _overlay.Show();
        _vm.UiVisible = _main.IsVisible; // arrancando minimizado en la bandeja: modo ahorro desde el inicio
        Mark("window shown");

        _vm.WebSettingsChanged = OnWebSettingsChanged;
        _vm.TempSelectionChanged = SaveSoon;
        _vm.SettingsDirty = SaveSoon;
        StartWeb();
        SetupTray();
        SetupHotkeys();

        int renderIdx = Array.IndexOf(args, "--render");
        if (renderIdx >= 0 && renderIdx + 1 < args.Length) _renderDir = args[renderIdx + 1];
        _timingFile = args.Contains("--timing") ? Path.Combine(Path.GetTempPath(), "hwpulse-startup.txt") : null;
        _ = StartSensorsAsync();
        Updater.CleanupOld();
        if (_renderDir == null) StartUpdateChecks();

        // --fake-pawnio installing|done|reboot|failed: muestra ese estado del aviso de PawnIO (desarrollo).
        int fakePawnio = Array.IndexOf(args, "--fake-pawnio");
        if (fakePawnio >= 0 && fakePawnio + 1 < args.Length) _vm.SetPawnioState(args[fakePawnio + 1]);

        // --fake-update 9.9.9: simula una versión nueva para ver el aviso (desarrollo; no descarga nada).
        int fakeIdx = Array.IndexOf(args, "--fake-update");
        if (fakeIdx >= 0 && fakeIdx + 1 < args.Length)
        {
            _vm.SetUpdateState("UpdateAvailable", args[fakeIdx + 1]);
            _vm.ShowUpdateBanner(args[fakeIdx + 1]);
        }
    }

    // ------------------------------------------------------------------ actualizaciones
    private Updater.Release? _update;
    private DispatcherTimer? _updateTimer = null; // solo se crea en Release

    /// <summary>Comprueba 20 s después de arrancar y luego cada 6 h (una petición pequeña a la API de GitHub).</summary>
    private void StartUpdateChecks()
    {
#if !DEBUG
        _updateTimer = new DispatcherTimer(TimeSpan.FromSeconds(20), DispatcherPriority.Background, async (_, _) =>
        {
            _updateTimer!.Interval = TimeSpan.FromHours(6);
            await CheckUpdatesAsync(manual: false);
        }, Dispatcher);
#endif
    }

    private async Task CheckUpdatesAsync(bool manual)
    {
        if (manual) _vm.SetUpdateState("UpdateChecking", busy: true);
        try { _update = await Updater.CheckAsync(_cts.Token); }
        catch (Exception ex)
        {
            if (manual) { ErrorLog.Write("UpdateCheck", ex); _vm.SetUpdateState("UpdateCheckFailed"); }
            return;
        }
        if (_update == null) { if (manual) _vm.SetUpdateState("UpdateLatest"); return; }

        string v = _update.Version.ToString(3);
        _vm.SetUpdateState("UpdateAvailable", v);
        if (_settings.UpdateDismissed != v) _vm.ShowUpdateBanner(v);
        if (!manual && _settings.UpdateNotified != v && _tray != null)
        {
            _settings.UpdateNotified = v;
            SaveSoon();
            _tray.ShowBalloonTip(5000, Loc.T("UpdateNoticeTitle"), Loc.F("UpdateNoticeText", v), WinForms.ToolTipIcon.Info);
        }
    }

    /// <summary>La × del aviso: no vuelve a salir hasta que haya una versión más nueva que esta.</summary>
    public void DismissUpdateBanner()
    {
        if (_update != null) { _settings.UpdateDismissed = _update.Version.ToString(3); SaveSoon(); }
        _vm.ShowUpdateBanner(null);
    }

    public void OpenUpdateNotes()
    {
        string url = _update?.PageUrl is { Length: > 0 } u ? u : $"https://github.com/{Updater.Repo}/releases/latest";
        OpenUrl(url);
    }

    /// <summary>Clic en el enlace de actualización: comprobar, o descargar e instalar si ya hay una nueva.</summary>
    public async void UpdateClicked()
    {
        if (_update == null) { await CheckUpdatesAsync(manual: true); return; }
        // La versión en carpeta (zip) no se puede reemplazar con un solo archivo: se abre la página.
        if (!Updater.IsSingleFile) { OpenUrl(_update.PageUrl); return; }
        _vm.SetUpdateState("UpdateDownloading", busy: true);
        try
        {
            await Updater.InstallAsync(_update, _cts.Token);
            bool minimized = _main == null || !_main.IsVisible;
            Updater.Relaunch(minimized);
            ExitApp();
        }
        catch (Exception ex)
        {
            ErrorLog.Write("UpdateInstall", ex);
            _vm.SetUpdateState("UpdateFailed");
        }
    }

    // ------------------------------------------------------------------ panel web
    private WebServer? _web;
    private bool _firewallChecked;
    private Dictionary<string, (byte[], string)>? _webFiles;

    private void StartWeb()
    {
        _web?.Dispose();
        _web = null;
        if (_settings.WebEnabled)
        {
            _webFiles ??= LoadWebFiles();
            _web = WebServer.TryStart(_settings.WebPort, _settings.WebLan && _vm.IsAdmin, _webFiles);
        }
        _vm.SetWebInfo(_web?.Url, _web?.LanUrl, _settings.WebEnabled && _web == null, _web?.ShortUrl);
        // La regla de firewall ahora incluye el puerto 80: se actualiza si el acceso por red ya estaba activo.
        if (_web?.LanActive == true && _vm.IsAdmin && !_firewallChecked)
        {
            _firewallChecked = true;
            int port = _settings.WebPort;
            _ = Task.Run(() => WebServer.SetFirewallRule(true, port));
        }
    }

    private Dictionary<string, (byte[], string)> LoadWebFiles()
    {
        var asm = typeof(App).Assembly;
        byte[] Embedded(string name)
        {
            using var s = asm.GetManifestResourceStream(name)!;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
        byte[] Wpf(string path)
        {
            using var s = GetResourceStream(new Uri("pack://application:,,,/" + path))!.Stream;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
        var files = new Dictionary<string, (byte[], string)>
        {
            ["/index.html"] = (Embedded("HwPulse.Web.index.html"), "text/html; charset=utf-8"),
            ["/icon.png"] = (Embedded("HwPulse.Web.icon.png"), "image/png"),
        };
        foreach (var w in new[] { "Medium", "SemiBold", "Bold" })
            files[$"/fonts/Inter-{w}.ttf"] = (Wpf($"Fonts/Inter-{w}.ttf"), "font/ttf");
        return files;
    }

    private void OnWebSettingsChanged(bool lanChanged)
    {
        _settings.Save();
        // Abrir/cerrar el puerto en el firewall solo cuando el usuario cambia la opción (y somos admin).
        if (lanChanged && _vm.IsAdmin)
        {
            bool allow = _settings.WebLan && _settings.WebEnabled;
            int port = _settings.WebPort;
            _ = Task.Run(() => WebServer.SetFirewallRule(allow, port));
        }
        StartWeb();
        BuildTrayMenu();
    }

    /// <summary>Abre la URL con el navegador del usuario sin heredar los permisos de administrador.</summary>
    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", url) { UseShellExecute = true }); } catch { }
    }

    /// <summary>Editor de colores del overlay (ventana modal); al cerrarlo se guardan los colores.</summary>
    public void OpenColors()
    {
        // Mostramos el overlay mientras se edita, para ver los cambios en vivo.
        if (!_vm.OverlayVisible) _vm.OverlayVisible = true;
        new ColorsWindow(_vm) { Owner = _main }.ShowDialog();
        _settings.Save();
    }

    public void OpenWeb() { if (_web?.Url is string u) OpenUrl(u); }

    private string BuildWebJson(Snapshot s)
    {
        double? total = s.CpuPower is null && s.GpuPower is null ? null : (s.CpuPower ?? 0) + (s.GpuPower ?? 0);
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            lang = Loc.I.Lang,
            admin = _vm.IsAdmin,
            colors = new { cpu = BrandColors.Hex(_vm.CpuBrandColor), gpu = BrandColors.Hex(_vm.GpuBrandColor) },
            status = _vm.StatusText,
            throttle = _vm.ThrottleVisibility == Visibility.Visible,
            cpu = new
            {
                name = s.CpuName, load = s.CpuLoad, temp = s.CpuTempMax, pkg = s.CpuTempPkg, clock = s.CpuClockAvg, power = s.CpuPower,
                cores = s.Cores.Select(c => new { i = c.Index, clock = c.ClockMhz, temp = c.TempC, load = c.Load }),
            },
            gpu = new
            {
                name = s.GpuName, load = s.GpuLoad, temp = s.GpuTemp, hotspot = s.GpuHotSpot, clock = s.GpuCoreClock,
                power = s.GpuPower, vramUsed = s.VramUsedMb, vramTotal = s.VramTotalMb,
            },
            ram = new { used = s.RamUsedGb, total = s.RamUsedGb + s.RamAvailGb, load = s.RamLoad },
            fps = new { value = s.Fps, frametime = s.FrametimeMs, process = s.FpsProcess },
            net = new { down = s.NetDownBps, up = s.NetUpBps },
            disks = s.Disks.Select(d => new { name = string.IsNullOrEmpty(d.Short) ? d.Name : d.Short, temp = d.TempC, activity = d.Activity }),
            drives = _vm.Drives.Select(d => new { name = d.Name, pct = d.Pct }),
            power = new { ac = s.OnAc, battery = s.BatteryCharge, total },
        });
    }

    // ------------------------------------------------------------------ arranque en segundo plano
    private readonly List<string> _marks = new();
    private string? _timingFile;

    /// <summary>Milisegundos desde que arrancó el proceso (no desde que cargó la App).</summary>
    private void Mark(string what)
    {
        var ms = (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;
        lock (_marks)
        {
            _marks.Add($"{ms,6:F0} ms  {what}");
            if (_timingFile != null && _ticks > 0) File.WriteAllLines(_timingFile, _marks);
        }
    }

    private async Task StartSensorsAsync()
    {
        try
        {
            // Abrir LibreHardwareMonitor (driver, discos, red) tarda varios segundos: fuera del hilo de la interfaz.
            _sensors = await _sensorsTask!;
            lock (_pendingResources) _pendingResources.Remove(_sensors); // ya vive en su campo: lo libera ExitApp
            Mark("sensors opened");
        }
        catch (Exception ex)
        {
            MessageBox.Show(Loc.T("SensorInitFail") + "\n" + ex.Message, "HwPulse");
            ExitApp();
            return;
        }

        // La sesión ETW de FPS ya no se crea aquí: la crea EnsureFpsMonitor (llamado desde el bucle de
        // polling) solo cuando alguien la necesita. Sin eso, TraceEvent y la captura de eventos corrían
        // siempre, con admin, aunque el usuario no usara FPS ni la página de Red.
        // ExitApp pudo correr mientras los sensores se abrían. La asignación de arriba va ANTES de esta
        // comprobación a propósito: el recurso ya vive en su campo, así que ExitApp/ProcessExit lo
        // disponen aunque la tarea terminara después de que ExitApp pasara por la lista de pendientes.
        if (IsExiting) return;
        if (_renderDir == null) _ = EnsurePawnIOAsync(auto: true);
        await PollLoop(_cts.Token);
    }

    // ------------------------------------------------------------------ driver PawnIO
    private bool _pawnioBusy;

    /// <summary>
    /// Sin PawnIO, LibreHardwareMonitor no puede leer temperatura/reloj/potencia del CPU y no avisa de nada.
    /// Al iniciar (con admin) se instala solo, con un aviso arriba de la ventana. Si sale bien se reinicia la
    /// app para abrir los sensores con el driver; si Windows pide reiniciar, se avisa. Si falla, no se
    /// reintenta solo en cada inicio: el aviso ofrece reintentar o instalarlo a mano.
    /// </summary>
    private async Task EnsurePawnIOAsync(bool auto)
    {
        if (_pawnioBusy || !_vm.IsAdmin || PawnIOInstaller.IsInstalled()) return;
        if (auto && (_settings.PawnIODismissed)) return;
        if (auto && _settings.PawnIOAutoFailed) { _vm.SetPawnioState("failed"); return; }

        _pawnioBusy = true;
        _vm.SetPawnioState("installing");
        try
        {
            var result = await PawnIOInstaller.InstallAsync(_cts.Token);
            if (result == PawnIOInstaller.Result.Failed) throw new InvalidOperationException("PawnIO service not found after install.");
            _settings.PawnIOAutoFailed = false;
            SaveSoon();
            if (result == PawnIOInstaller.Result.RebootRequired) { _vm.SetPawnioState("reboot"); return; }

            // Instalado: los sensores se abrieron sin el driver, así que se reinicia la app para releerlos.
            _vm.SetPawnioState("done");
            await Task.Delay(1500);
            if (IsExiting) return;
            _settings.Save();
            Updater.Relaunch(minimized: _main == null || !_main.IsVisible);
            ExitApp();
        }
        catch (OperationCanceledException) { /* la app se está cerrando */ }
        catch (Exception ex)
        {
            ErrorLog.Write("PawnIO", ex);
            _settings.PawnIOAutoFailed = true;
            SaveSoon();
            _vm.SetPawnioState("failed");
        }
        finally { _pawnioBusy = false; }
    }

    public void PawnioRetry() => _ = EnsurePawnIOAsync(auto: false);
    public void PawnioManual() => OpenUrl(PawnIOInstaller.ManualUrl);
    public void PawnioDismiss()
    {
        _settings.PawnIODismissed = true;
        SaveSoon();
        _vm.SetPawnioState(null);
    }

    // ------------------------------------------------------------------ sesión ETW de FPS (bajo demanda)
    private bool _fpsTried; // un solo intento, como antes: si ETW falla, el overlay muestra "—"

    /// <summary>Crea la sesión ETW de FPS solo si alguien la usa ahora: el overlay visible con FPS o
    /// frametime marcados, la página de Red abierta (la red por proceso se lee de los eventos
    /// Kernel-Network de esa misma sesión) o un cliente del panel web conectado (el dashboard también
    /// muestra los FPS). El resto del tiempo no hay sesión: ni RAM de TraceEvent ni eventos de present
    /// de todo el sistema.</summary>
    private void EnsureFpsMonitor()
    {
        if (_fps != null || _fpsTried || !_vm.IsAdmin) return;
        bool overlayFps = _vm.OverlayVisible && _vm.OverlayHasFps;
        bool netPage = _vm.UiVisible && _vm.CurrentPage == "Network";
        bool webFps = _web?.HasClients == true;
        if (!overlayFps && !netPage && !webFps) return;
        _fpsTried = true;
        _ = Task.Run(() =>
        {
            var f = FpsMonitor.TryStart();
            if (f == null) return;
            lock (_pendingResources) _pendingResources.Add(f);
            _fps = f;
            lock (_pendingResources) _pendingResources.Remove(f);
        });
    }

    // ------------------------------------------------------------------ capturas (desarrollo)
    private string? _renderDir;
    private int _ticks;

    /// <summary>--render &lt;carpeta&gt;: guarda PNG de cada pestaña y del overlay, y sale.</summary>
    private void RenderScreenshots()
    {
        Directory.CreateDirectory(_renderDir!);
        void Save(FrameworkElement el, string name)
        {
            el.UpdateLayout();
            // Elementos desmontados o de ventanas nunca mostradas (overlay oculto por ajuste): nadie los
            // midió y su tamaño real es 0, con lo que RenderTargetBitmap no acepta la medida. Se miden
            // aquí, igual que hace OverlayWindow.Render, antes de capturar.
            if (el.ActualWidth < 1 || el.ActualHeight < 1)
            {
                el.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                el.Arrange(new Rect(el.DesiredSize));
                el.UpdateLayout();
                if (el.ActualWidth < 1 || el.ActualHeight < 1) return; // no hay nada que capturar
            }
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(el);
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
                (int)(el.ActualWidth * dpi.DpiScaleX), (int)(el.ActualHeight * dpi.DpiScaleY),
                dpi.PixelsPerInchX, dpi.PixelsPerInchY, System.Windows.Media.PixelFormats.Pbgra32);
            bmp.Render(el);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using var fs = File.Create(Path.Combine(_renderDir!, name));
            enc.Save(fs);
        }

        // Solo para la captura: simulamos el acceso por red para poder ver la sección del QR.
        _vm.SetWebInfo(_web?.Url, "http://192.168.1.50:8086/", false);
        var root = (FrameworkElement)_main!.Content;
        var pages = _main.Pages.ToList();
        var fit = new List<string>();
        foreach (var (nav, key) in pages)
        {
            nav.IsChecked = true;
            Save(root, $"{Loc.I.Lang}-{key.ToLowerInvariant()}.png");
            // ¿Cabe la página sin desplazar? (alto del contenido frente al alto visible)
            var sv = FindScroll(_main.PageFor(nav));
            if (sv != null) fit.Add($"{key,-10} content {sv.ExtentHeight,5:F0}  visible {sv.ViewportHeight,5:F0}  {(sv.ExtentHeight <= sv.ViewportHeight + 1 ? "OK" : "SCROLL")}");
        }
        File.WriteAllLines(Path.Combine(_renderDir!, $"{Loc.I.Lang}-fit.txt"), fit);
        // Ajustes completos (sin el recorte del scroll)
        pages.Last().nav.IsChecked = true;
        Save((FrameworkElement)_main.PageSettings.Content, $"{Loc.I.Lang}-settings-full.png");
        pages[0].nav.IsChecked = true;
        // El overlay puede estar oculto por ajuste del usuario: su contenido desmontado nunca se midió
        // y las capturas de abajo reventarían (mide 0). Se dibuja en frío antes de capturarlo.
        _overlay!.RenderNow();
        var prevStyle = _vm.OverlayPanel;
        _vm.OverlayPanel = true;
        Save(_overlay!.SourceContent, $"{Loc.I.Lang}-overlay-panel.png");
        _overlay.ApplyScale(2);
        Save(_overlay.SourceContent, $"{Loc.I.Lang}-overlay-panel-200.png");
        _overlay.ApplyScale(_settings.OverlayScale);
        _vm.OverlayCompact = true;
        Save(_overlay.SourceContent, $"{Loc.I.Lang}-overlay-compact.png");
        // La imagen supermuestreada tal como la muestra la ventana. El bitmap lo siembra RenderCore con el
        // estilo activo en ese momento; sin este refresco mostraría el último dibujado (modo render: no
        // hay paso de dispatcher que lo actualice entre medias).
        _overlay.RenderNow();
        _overlay.UpdateLayout();
        Save((FrameworkElement)_overlay.Content, $"{Loc.I.Lang}-overlay-window.png");
        if (prevStyle) _vm.OverlayPanel = true;
        _overlay.RenderNow();
        _overlay.UpdateLayout();
        Save((FrameworkElement)_overlay.Content, $"{Loc.I.Lang}-overlay-window-panel.png");
        var cw = new ColorsWindow(_vm) { Owner = _main };
        cw.Show();
        cw.UpdateLayout();
        Save((FrameworkElement)cw.Content, $"{Loc.I.Lang}-colors.png");
        cw.Close();
        if (prevStyle) _vm.OverlayPanel = true;
    }

    private static System.Windows.Controls.ScrollViewer? FindScroll(DependencyObject? d)
    {
        if (d is System.Windows.Controls.ScrollViewer sv) return sv;
        if (d == null) return null;
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(d); i++)
            if (FindScroll(System.Windows.Media.VisualTreeHelper.GetChild(d, i)) is { } r) return r;
        return null;
    }

    private static bool TryRelaunchElevated(string[] args)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)),
            });
            return true;
        }
        catch (Win32Exception)
        {
            // El usuario dijo "No" en el UAC: seguimos sin admin y la ventana lo avisa.
            return false;
        }
    }

    private static void RunDump(string path, bool admin)
    {
        try
        {
            using var s = new SensorService(admin);
            s.Read();               // la primera lectura llena los valores iniciales
            Thread.Sleep(1200);
            File.WriteAllText(path, s.Dump());
        }
        catch (Exception ex)
        {
            File.WriteAllText(path, "ERROR: " + ex);
        }
    }

    private async Task PollLoop(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            do
            {
                try
                {
                    // ETW de FPS: la sesión se crea solo si el overlay la usa o la página de Red está abierta;
                    // la captura de red por proceso (Kernel-Network) se enciende y apaga según esa página.
                    EnsureFpsMonitor();
                    _fps?.SetNetworkEnabled(_vm.UiVisible && _vm.CurrentPage == "Network");
                    // Procesos cada 2 s (es la consulta más cara); la primera, ya en el primer ciclo.
                    // Procesos desde el segundo ciclo: el primero solo trae sensores, para pintar cuanto antes.
                    bool sampleProcs = _vm.UiVisible && _procTick++ % 2 == 1; // en la bandeja no se listan procesos
                    // La propia HwPulse también dibuja con DirectX: no contamos sus FPS como si fuera un juego.
                    int fgPid = Native.ForegroundProcessId();
                    if (fgPid == Environment.ProcessId) fgPid = 0;
                    var snap = await Task.Run(() =>
                    {
                        var s = _sensors!.Read(50);
                        if (s == null) return null; // discos/red abriéndose: conservamos la lectura anterior
                        if (sampleProcs)
                        {
                            try
                            {
                                // GPU (contadores de Windows) y red (eventos ETW, solo con admin) por proceso
                                var gpu = _gpuMon.Sample();
                                var net = _fps?.SampleNetwork();
                                s.Processes = _procMon.Sample(gpu, net);
                                s.NetByProcessAvailable = net != null;
                            }
                            catch (Exception ex) { ErrorLog.Write("Processes", ex); }
                        }
                        // En la bandeja no se muestra la red por proceso: se descarta lo acumulado para que
                        // el diccionario no crezca sin límite (una entrada por PID que haya tenido tráfico).
                        else if (!_vm.UiVisible) _fps?.ResetNetwork();
                        if (_fps != null) (s.Fps, s.FrametimeMs, s.FpsProcess) = _fps.Sample(fgPid);
                        return s;
                    }, ct);
                    if (snap == null) continue;
                    _vm.Apply(snap);   // continuamos en el hilo de la interfaz
                    if (_tray != null) _tray.Text = _vm.TrayText;
                    // El JSON del panel web solo si hay alguien conectado (SSE o polling reciente).
                    if (_web != null && _web.NeedsJson) _web.SetJson(BuildWebJson(snap));
                    // Con los primeros datos ya en pantalla, abrimos discos y red en segundo plano.
                    if (!_sensors!.ExtrasEnabled && !_extrasStarted)
                    {
                        _extrasStarted = true;
                        var sensors = _sensors;
                        _ = Task.Run(() => { sensors.EnableExtras(); Mark("disks + network opened"); });
                    }
                    if (_timingFile != null && _ticks == 0)
                    {
                        Mark("first data on screen");
                        File.WriteAllLines(_timingFile, _marks);
                    }
                    if (_renderDir == null) _ticks++;
                    else if (++_ticks == 4)
                    {
                        RenderScreenshots();
                        Loc.I.SetLanguage(Loc.I.IsSpanish ? "en" : "es");
                        _vm.Rerender();
                        RenderScreenshots();
                        Loc.I.SetLanguage(_settings.Language);
                        ExitApp();
                        return;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { ErrorLog.Write("Read", ex); } // una lectura fallida no detiene el ciclo
            }
            while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) { /* saliendo */ }
    }

    // ------------------------------------------------------------------ bandeja
    private void SetupTray()
    {
        // Ícono propio, en el tamaño exacto que usa la bandeja (16/20/24 px según el escalado) para que se vea nítido.
        System.Drawing.Icon icon;
        try
        {
            using var s = GetResourceStream(new Uri("pack://application:,,,/Assets/HwPulse.ico"))!.Stream;
            icon = new System.Drawing.Icon(s, WinForms.SystemInformation.SmallIconSize);
        }
        catch { icon = System.Drawing.SystemIcons.Application; }

        _tray = new WinForms.NotifyIcon { Icon = icon, Text = "HwPulse", Visible = true };
        _tray.DoubleClick += (_, _) => ShowMain();
        BuildTrayMenu();
    }

    private void BuildTrayMenu()
    {
        if (_tray == null) return;
        var old = _tray.ContextMenuStrip;
        var menu = DarkMenu.Create();
        menu.Items.Add(DarkMenu.Item(Loc.T("TrayOpen"), null, (_, _) => ShowMain()));
        menu.Items.Add(DarkMenu.Item(Loc.T("TrayToggle"), "Ctrl+Shift+F10", (_, _) => ToggleOverlay()));
        menu.Items.Add(DarkMenu.Item(Loc.T("TrayCorner"), "Ctrl+Shift+F11", (_, _) => _vm.CycleCorner()));
        if (_web?.Url is string url)
            menu.Items.Add(DarkMenu.Item(Loc.T("TrayWeb"), null, (_, _) => OpenUrl(url)));
        menu.Items.Add(DarkMenu.Separator());
        menu.Items.Add(DarkMenu.Item(Loc.T("TrayExit"), null, (_, _) => ExitApp()));
        _tray.ContextMenuStrip = menu;
        old?.Dispose();
    }

    public void ShowMain()
    {
        if (_main == null) return;
        _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = _main.RestoreState;
        _main.Activate();
    }

    /// <summary>Qué hace la X de la ventana: "ask", "tray" o "exit".</summary>
    public string CloseAction
    {
        get => _settings.CloseAction;
        set => _vm.SetCloseAction(value); // también actualiza la opción en Ajustes y guarda

    }

    public void NotifyHiddenToTray()
    {
        if (_settings.TrayNoticeShown || _tray == null) return;
        _settings.TrayNoticeShown = true;
        _settings.Save();
        _tray.ShowBalloonTip(4000, Loc.T("TrayNoticeTitle"), Loc.T("TrayNoticeText"), WinForms.ToolTipIcon.Info);
    }

    // ------------------------------------------------------------------ atajos globales
    private void SetupHotkeys()
    {
        var p = new HwndSourceParameters("HwPulseHotkeys") { Width = 0, Height = 0, WindowStyle = 0, ParentWindow = new IntPtr(-3) };
        _hotkeys = new HwndSource(p);
        _hotkeys.AddHook(HotkeyHook);
        uint mods = Native.MOD_CONTROL | Native.MOD_SHIFT | Native.MOD_NOREPEAT;
        bool ok1 = Native.RegisterHotKey(_hotkeys.Handle, HotkeyToggle, mods, Native.VK_F10);
        bool ok2 = Native.RegisterHotKey(_hotkeys.Handle, HotkeyCorner, mods, Native.VK_F11);
        _vm.HotkeysFailed = !ok1 || !ok2;
    }

    private IntPtr HotkeyHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY)
        {
            switch (wParam.ToInt32())
            {
                case HotkeyToggle: ToggleOverlay(); handled = true; break;
                case HotkeyCorner: _vm.CycleCorner(); handled = true; break;
            }
        }
        return IntPtr.Zero;
    }

    // ------------------------------------------------------------------ overlay
    public void ToggleOverlay() => _vm.OverlayVisible = !_vm.OverlayVisible;

    private void OnOverlayVisibilityChanged(bool visible)
    {
        if (_overlay == null) return;
        if (visible) _overlay.Show(); else _overlay.Hide();
        SaveSoon();
    }

    private void OnOverlayLayoutChanged()
    {
        // Aplicamos el tamaño y esperamos a que el overlay recalcule su medida antes de colocarlo.
        _overlay?.ApplyScale(_settings.OverlayScale);
        _overlay?.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => _overlay.Reposition());
        SaveSoon();
    }

    // ------------------------------------------------------------------ guardado diferido
    // Antes cada paso de un slider escribía settings.json en disco (decenas de veces por segundo al arrastrar).
    // Ahora se guarda una sola vez, medio segundo después del último cambio.
    private DispatcherTimer? _saveTimer;

    public void SaveSoon()
    {
        _saveTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Background,
            (_, _) => { _saveTimer!.Stop(); _settings.Save(); }, Dispatcher);
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    // ------------------------------------------------------------------ salida
    public void ExitApp()
    {
        IsExiting = true;
        _cts.Cancel();
        if (_hotkeys != null)
        {
            Native.UnregisterHotKey(_hotkeys.Handle, HotkeyToggle);
            Native.UnregisterHotKey(_hotkeys.Handle, HotkeyCorner);
            _hotkeys.Dispose();
        }
        if (_tray != null)
        {
            _tray.Visible = false;
            var trayIcon = _tray.Icon;
            _tray.Dispose();
            // El icono propio es un HICON nuestro; el de reserva (SystemIcons) es compartido y no se libera.
            if (trayIcon != null && !ReferenceEquals(trayIcon, System.Drawing.SystemIcons.Application)) trayIcon.Dispose();
        }
        _saveTimer?.Stop();
        _updateTimer?.Stop();
        _settings.Save();
        _overlay?.Close();
        _main?.Close();
        _web?.Dispose();
        _fps?.Dispose();
        _sensors?.Dispose();
        // Sensores o sesión ETW que aún se estén abriendo en segundo plano (la carrera de arranque que
        // cubre _pendingResources): sin esto quedaría un Computer abierto con módulos de PawnIO cargados.
        lock (_pendingResources)
        {
            foreach (var d in _pendingResources) { try { d.Dispose(); } catch { } }
            _pendingResources.Clear();
        }
        _mutex?.ReleaseMutex();
        Shutdown();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        _settings?.Save();
        base.OnSessionEnding(e);
    }
}
