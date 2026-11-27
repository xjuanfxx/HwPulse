using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;

namespace HwPulse;

/// <summary>Fila con barra (temperaturas, ventiladores). Se actualiza en sitio con <see cref="Update"/>:
/// antes se recreaba la lista entera cada segundo y WPF reconstruía los controles de cada ItemsControl.</summary>
public sealed class BarRow : Observable
{
    public BarRow(string label, string valueText, double pct, Brush brush) => Update(label, valueText, pct, brush);
    public string Label { get; private set; } = "";
    public string ValueText { get; private set; } = Fmt.None;
    public double Pct { get; private set; }
    public Brush Brush { get; private set; } = Fmt.Neutral;
    public void Update(string label, string valueText, double pct, Brush brush)
    {
        Label = label; ValueText = valueText; Pct = pct; Brush = brush;
        RaiseAll();
    }
}

/// <summary>Fila de proceso con dos valores (p. ej. GPU y VRAM, lectura y escritura, descarga y subida).</summary>
public sealed class ProcPair : Observable
{
    public ProcPair(string name, Geometry? icon, string a, string b) => Update(name, icon, a, b);
    public string Name { get; private set; } = "";
    public Geometry? Icon { get; private set; }
    public string A { get; private set; } = "";
    public string B { get; private set; } = "";
    public void Update(string name, Geometry? icon, string a, string b)
    {
        Name = name; Icon = icon; A = a; B = b;
        RaiseAll();
    }
}

public sealed class ProcView : Observable
{
    public ProcView(string name, Geometry? icon, string cpuText, string memText, double cpuPct) => Update(name, icon, cpuText, memText, cpuPct);
    public string Name { get; private set; } = "";
    public Geometry? Icon { get; private set; }
    public string CpuText { get; private set; } = "";
    public string MemText { get; private set; } = "";
    public double CpuPct { get; private set; }
    public void Update(string name, Geometry? icon, string cpuText, string memText, double cpuPct)
    {
        Name = name; Icon = icon; CpuText = cpuText; MemText = memText; CpuPct = cpuPct;
        RaiseAll();
    }
}

public sealed class NetView : Observable
{
    public NetView(string name, string downText, string upText, string totalsText) => Update(name, downText, upText, totalsText);
    public string Name { get; private set; } = "";
    public string DownText { get; private set; } = "";
    public string UpText { get; private set; } = "";
    public string TotalsText { get; private set; } = "";
    public void Update(string name, string downText, string upText, string totalsText)
    {
        Name = name; DownText = downText; UpText = upText; TotalsText = totalsText;
        RaiseAll();
    }
}

public sealed class DriveView : Observable
{
    public DriveView(string name, string label, string detail, string pctText, double pct, Brush brush, string freeTotal = "")
        => Update(name, label, detail, pctText, pct, brush, freeTotal);
    public string Name { get; private set; } = "";
    public string Label { get; private set; } = "";
    public string Detail { get; private set; } = "";
    public string PctText { get; private set; } = "";
    public double Pct { get; private set; }
    public Brush Brush { get; private set; } = Fmt.Neutral;
    public string FreeTotal { get; private set; } = "";
    public void Update(string name, string label, string detail, string pctText, double pct, Brush brush, string freeTotal)
    {
        Name = name; Label = label; Detail = detail; PctText = pctText; Pct = pct; Brush = brush; FreeTotal = freeTotal;
        RaiseAll();
    }
}

/// <summary>Datos de las páginas nuevas: tarjetas del resumen, gráfica de uso, temperaturas, procesos, red y unidades.</summary>
public sealed partial class MainViewModel
{
    private readonly RingBuffer _cpuLoadHist = new(HistorySeconds), _netDownHist = new(HistorySeconds), _netUpHist = new(HistorySeconds);
    private List<ProcInfo> _procs = new();
    private readonly Dictionary<string, double> _cpuSmooth = new(StringComparer.OrdinalIgnoreCase);
    private bool _sortByMem;

    public bool SortByCpu => !_sortByMem;
    public bool SortByMem => _sortByMem;
    public string CpuHeader => _sortByMem ? "CPU" : "CPU ↓";
    public string MemHeader => Loc.T("Memory") + (_sortByMem ? " ↓" : "");

    public void SetProcessSort(bool byMemory)
    {
        _sortByMem = byMemory;
        Rerender();
    }
    private int _driveTick;
    private readonly DateTime _started = DateTime.Now;

    // Cambian al color de la marca (Intel, AMD, NVIDIA) cuando se conoce el modelo; ver ApplyBrandColors.
    private static Brush BarCpu = Fmt.Freeze(BrandColors.DefaultCpu);
    private static Brush BarGpu = Fmt.Freeze(BrandColors.DefaultGpu);
    public Color CpuBrandColor { get; private set; } = BrandColors.DefaultCpu;
    public Color GpuBrandColor { get; private set; } = BrandColors.DefaultGpu;
    private string? _brandFor;

    /// <summary>Aplica el color de la marca del CPU y la GPU a toda la app (gráficas, tarjetas, barras y overlay).</summary>
    private void ApplyBrandColors(Snapshot s)
    {
        var id = $"{s.CpuName}|{s.GpuName}";
        if (id == _brandFor || (s.CpuName is null && s.GpuName is null)) return;
        _brandFor = id;
        CpuBrandColor = BrandColors.ForCpu(s.CpuName);
        GpuBrandColor = BrandColors.ForGpu(s.GpuName);
        BarCpu = Fmt.Freeze(CpuBrandColor);
        BarGpu = Fmt.Freeze(GpuBrandColor);
        var res = Application.Current.Resources;
        res["CpuColor"] = Fmt.Freeze(CpuBrandColor);
        res["GpuColor"] = Fmt.Freeze(GpuBrandColor);
        RaiseOverlayColors(); // el overlay usa la marca salvo que el usuario haya elegido otro color
    }
    private static readonly Brush BarMem = Fmt.Freeze(Color.FromRgb(0x9C, 0x8B, 0xD0));
    private static readonly Brush BarDisk = Fmt.Freeze(Color.FromRgb(0x8A, 0x93, 0x9E));

    // ---------------------------------------------------------------- Panel web
    public Action<bool>? WebSettingsChanged;
    public string WebUrl { get; private set; } = "";
    public string WebLanUrl { get; private set; } = "";
    public Visibility WebUrlVisibility => string.IsNullOrEmpty(WebUrl) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility WebLanUrlVisibility => string.IsNullOrEmpty(WebLanUrl) ? Visibility.Collapsed : Visibility.Visible;
    public string WebNote { get; private set; } = "";
    public Visibility WebNoteVisibility => string.IsNullOrEmpty(WebNote) ? Visibility.Collapsed : Visibility.Visible;

    public bool WebEnabled
    {
        get => _settings.WebEnabled;
        set { if (value == _settings.WebEnabled) return; _settings.WebEnabled = value; Raise(); WebSettingsChanged?.Invoke(_settings.WebLan); }
    }

    public bool WebLan
    {
        get => _settings.WebLan;
        set { if (value == _settings.WebLan) return; _settings.WebLan = value; Raise(); WebSettingsChanged?.Invoke(true); }
    }

    /// <summary>Código QR con la dirección de red, para abrir el panel con la cámara del teléfono.</summary>
    public ImageSource? WebQr { get; private set; }

    private static ImageSource? MakeQr(string text)
    {
        try
        {
            using var gen = new QRCoder.QRCodeGenerator();
            using var data = gen.CreateQrCode(text, QRCoder.QRCodeGenerator.ECCLevel.M);
            var png = new QRCoder.PngByteQRCode(data).GetGraphic(10);
            var img = new System.Windows.Media.Imaging.BitmapImage();
            img.BeginInit();
            img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            img.StreamSource = new MemoryStream(png);
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch { return null; }
    }

    /// <summary>Lo más corto de escribir: solo la IP si el puerto 80 está disponible; si no, la dirección completa.</summary>
    public string WebTypeUrl { get; private set; } = "";

    public void SetWebInfo(string? url, string? lanUrl, bool failed, string? shortUrl = null)
    {
        WebTypeUrl = shortUrl ?? (lanUrl ?? "").Replace("http://", "").TrimEnd('/');
        Raise(nameof(WebTypeUrl));
        WebUrl = url ?? "";
        WebLanUrl = lanUrl ?? "";
        WebQr = lanUrl != null ? MakeQr(lanUrl) : null;
        Raise(nameof(WebQr));
        WebNote = failed ? Loc.F("WebFailed", _settings.WebPort)
                : _settings.WebEnabled && _settings.WebLan && lanUrl == null ? Loc.T(IsAdmin ? "WebLanFailed" : "WebLanNeedsAdmin")
                : "";
        Raise(nameof(WebUrl)); Raise(nameof(WebLanUrl)); Raise(nameof(WebNote));
        Raise(nameof(WebUrlVisibility)); Raise(nameof(WebLanUrlVisibility)); Raise(nameof(WebNoteVisibility));
    }

    // ---------------------------------------------------------------- FPS (overlay)
    private readonly RingBuffer _fpsHist = new(120);
    private readonly ChartSeries _fpsChart = new(120);
    public string FpsText { get; private set; } = "–";
    public Brush FpsBrush { get; private set; } = Fmt.Faint;
    public string FrametimeText { get; private set; } = Fmt.None;
    public string FpsSource { get; private set; } = " ";
    public SeriesView? FpsSeries { get; private set; }

    // ---------------------------------------------------------------- Encabezado
    public string LiveText { get; private set; } = Loc.T("Starting");
    public string SessionText { get; private set; } = "";
    public string NowLabel => Loc.T("Now");

    // ---------------------------------------------------------------- Tarjetas
    public string CpuCardSub { get; private set; } = " ";
    public string GpuCardSub { get; private set; } = " ";
    public string RamPctText { get; private set; } = Fmt.None;
    public string RamCardSub { get; private set; } = " ";
    public string StoragePctText { get; private set; } = Fmt.None;
    public string StorageCardSub { get; private set; } = " ";
    public double StoragePct { get; private set; }
    public string StorageName { get; private set; } = "";
    public string StorageFreeTotal { get; private set; } = Fmt.None;
    public string GpuClockGhz { get; private set; } = Fmt.None;
    public string CpuTempCard { get; private set; } = Fmt.None;
    private readonly ChartSeries _cpuLoadChart = new(HistorySeconds);
    public SeriesView? CpuLoadSeries { get; private set; }

    // ---------------------------------------------------------------- Gráfica de uso
    public int ChartRange => _settings.ChartRange;
    public bool Range1 { get => ChartRange == 60; set { if (value) SetRange(60); } }
    public bool Range5 { get => ChartRange == 300; set { if (value) SetRange(300); } }
    public bool Range10 { get => ChartRange == 600; set { if (value) SetRange(600); } }

    private void SetRange(int seconds)
    {
        _settings.ChartRange = seconds;
        Raise(nameof(ChartRange)); Raise(nameof(Range1)); Raise(nameof(Range5)); Raise(nameof(Range10));
    }

    // ---------------------------------------------------------------- Listas
    // Filas reutilizadas (patrón de Cores/CoreRow): la colección es fija y las filas se refrescan con
    // Update; solo se añade o quita alguna cuando cambia cuántas hay. WPF deja de reconstruir cada
    // ItemsControl una vez por segundo.
    public ObservableCollection<BarRow> TempRows { get; } = new();
    public Visibility NoTempsVisibility => TempRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public ObservableCollection<ProcView> TopCpu { get; } = new();
    public ObservableCollection<ProcView> TopMem { get; } = new();
    public ObservableCollection<ProcPair> TopCpuPage { get; } = new();
    public ObservableCollection<ProcPair> TopGpu { get; } = new();
    public ObservableCollection<ProcPair> TopDisk { get; } = new();
    public ObservableCollection<ProcPair> TopNet { get; } = new();
    private bool _netByProcess;
    public Visibility NoGpuProcsVisibility => TopGpu.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NoDiskProcsVisibility => TopDisk.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NoNetProcsVisibility => TopNet.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public string NoNetProcsText => Loc.T(_netByProcess ? "NoActivity" : "NetNeedsAdmin");
    public ObservableCollection<BarRow> FanRows { get; } = new();
    public Visibility NoFansVisibility => FanRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public ObservableCollection<DriveView> Drives { get; } = new();

    // ---------------------------------------------------------------- Memoria
    public string VirtText { get; private set; } = Fmt.None;
    public double VirtPct { get; private set; }

    // ---------------------------------------------------------------- Red
    public string NetDownText { get; private set; } = Fmt.None;
    public string NetUpText { get; private set; } = Fmt.None;
    private readonly ChartSeries _netDownChart = new(HistorySeconds), _netUpChart = new(HistorySeconds);
    public SeriesView? NetDownSeries { get; private set; }
    public SeriesView? NetUpSeries { get; private set; }
    public ObservableCollection<NetView> NetRows { get; } = new();
    public Visibility NoNetVisibility => NetRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    // ---------------------------------------------------------------- Estilo del overlay
    public bool OverlayPanel { get => _settings.OverlayStyle == "panel"; set { if (value) SetOverlayStyle("panel"); } }
    public bool OverlayCompact { get => _settings.OverlayStyle == "compact"; set { if (value) SetOverlayStyle("compact"); } }
    public bool OverlayCustom { get => _settings.OverlayStyle == "custom"; set { if (value) SetOverlayStyle("custom"); } }
    public Visibility OverlayPanelVisibility => OverlayPanel ? Visibility.Visible : Visibility.Collapsed;
    public Visibility OverlayCompactVisibility => OverlayCompact ? Visibility.Visible : Visibility.Collapsed;
    public Visibility OverlayCustomVisibility => OverlayCustom ? Visibility.Visible : Visibility.Collapsed;
    /// <summary>En el estilo personalizado las casillas "Mostrar en el overlay" no aplican (lo decide el editor).</summary>
    public Visibility OverlayItemsVisibility => OverlayCustom ? Visibility.Collapsed : Visibility.Visible;

    private void SetOverlayStyle(string style)
    {
        _settings.OverlayStyle = style;
        if (style == "custom") _settings.OverlayLayout ??= HwPulse.OverlayLayout.Preset("line");
        Raise(nameof(OverlayPanel)); Raise(nameof(OverlayCompact)); Raise(nameof(OverlayCustom));
        Raise(nameof(OverlayPanelVisibility)); Raise(nameof(OverlayCompactVisibility)); Raise(nameof(OverlayCustomVisibility));
        Raise(nameof(OverlayItemsVisibility)); Raise(nameof(OverlayHasFps));
        OverlayLayoutChanged?.Invoke();
    }

    /// <summary>Diseño del overlay personalizado (se crea con el preset de una línea la primera vez).</summary>
    public OverlayLayout CustomLayout => _settings.OverlayLayout ??= HwPulse.OverlayLayout.Preset("line");

    /// <summary>El editor cambió el diseño: se reconstruye el overlay y se guarda.</summary>
    public void SetCustomLayout(OverlayLayout layout)
    {
        _settings.OverlayLayout = layout;
        Raise(nameof(OverlayHasFps));
        OverlayLayoutChanged?.Invoke();
    }

    /// <summary>Hay un juego dibujando ahora mismo (FPS detectados).</summary>
    public bool FpsActive { get; private set; }

    /// <summary>"Mostrar solo mientras juego": el overlay se oculta cuando no se detectan FPS.</summary>
    public bool OverlayOnlyInGame
    {
        get => _settings.OverlayOnlyInGame;
        set { if (value == _settings.OverlayOnlyInGame) return; _settings.OverlayOnlyInGame = value; Raise(); OverlayLayoutChanged?.Invoke(); }
    }

    /// <summary>Hora actual para el overlay ("time").</summary>
    public string ClockTimeText => DateTime.Now.ToString("HH:mm");

    // Valores numéricos para las barras del overlay
    public double CpuLoadPct { get; private set; }
    public double GpuLoadPct { get; private set; }
    public string GpuVramShort { get; private set; } = Fmt.None;
    public string RamShort { get; private set; } = Fmt.None;

    private void RecordExtra(Snapshot s)
    {
        ApplyBrandColors(s);
        _cpuLoadHist.Add(s.CpuLoad);
        _netDownHist.Add(s.NetDownBps / 1024 / 1024);
        _netUpHist.Add(s.NetUpBps / 1024 / 1024);
        if (s.Processes.Count > 0)
        {
            _procs = s.Processes;
            // Media móvil exponencial del CPU por proceso: el orden cambia solo si el consumo cambia de verdad.
            var seen = new HashSet<string>();
            foreach (var p in _procs)
            {
                seen.Add(p.Name);
                _cpuSmooth[p.Name] = _cpuSmooth.TryGetValue(p.Name, out var old) ? old * 0.6 + p.CpuPct * 0.4 : p.CpuPct;
            }
            foreach (var gone in _cpuSmooth.Keys.Where(k => !seen.Contains(k)).ToList()) _cpuSmooth.Remove(gone);
        }
        _fpsHist.Add(s.Fps ?? 0);
        if (_driveTick++ % 5 == 0) _ = RefreshDrivesAsync();
    }

    private void RenderExtra(Snapshot s)
    {
        LiveText = $"{Loc.T("Live")} · {DateTime.Now:HH:mm}";
        SessionText = Loc.F("Session", (DateTime.Now - _started).ToString(@"hh\:mm\:ss"));

        // Sin juego: un guion pequeño y gris en lugar de una barra blanca enorme.
        FpsText = s.Fps is double fps ? $"{fps:F0}" : "–";
        FpsBrush = s.Fps is null ? Fmt.Faint : Fmt.Neutral;
        FrametimeText = s.FrametimeMs is double ft ? $"{ft:F1} ms" : "–";
        FpsSource = s.FpsProcess ?? Loc.T(IsAdmin ? "NoGame" : "FpsNeedsAdmin");
        FpsSeries = s.Fps is null ? null : _fpsChart.Snapshot(_fpsHist);
        FpsActive = s.Fps is not null;

        // Tarjetas
        CpuCardSub = JoinDot(s.CpuTempMax is double t ? Fmt.Temp(t) : null, s.CpuClockAvg is double c ? Fmt.Ghz(c) : null);
        GpuCardSub = JoinDot(s.GpuTemp is double gt ? Fmt.Temp(gt) : null, s.GpuCoreClock is double gc ? Fmt.Mhz(gc) : null);
        RamPctText = Fmt.Pct(s.RamLoad);
        RamCardSub = s.RamUsedGb is double ru && s.RamAvailGb is double ra ? $"{ru:F1} / {ru + ra:F1} GB" : " ";
        var sys = Drives.FirstOrDefault(d => d.Name.StartsWith(Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\') ?? "C:", StringComparison.OrdinalIgnoreCase))
                  ?? Drives.FirstOrDefault();
        StoragePctText = sys?.PctText ?? Fmt.None;
        StoragePct = sys?.Pct ?? 0;
        StorageCardSub = sys != null ? $"{sys.Name} · {sys.Detail}" : " ";
        StorageName = sys?.Name ?? "";
        StorageFreeTotal = sys?.FreeTotal ?? Fmt.None;
        GpuClockGhz = Fmt.Ghz(s.GpuCoreClock);
        CpuTempCard = Fmt.Temp(s.CpuTempMax);
        CpuLoadSeries = _cpuLoadChart.Snapshot(_cpuLoadHist);

        CpuLoadPct = s.CpuLoad ?? 0;
        GpuLoadPct = s.GpuLoad ?? 0;
        GpuVramShort = s.VramUsedMb is double vu && s.VramTotalMb is double vt ? $"{vu / 1024:F1} / {vt / 1024:F0} GB" : Fmt.None;
        RamShort = s.RamUsedGb is double u2 && s.RamAvailGb is double a2 ? $"{u2:F1} / {u2 + a2:F0} GB" : Fmt.None;

        // Con la ventana oculta (en la bandeja) solo hace falta lo que usa el overlay: lo de abajo
        // (listas de temperaturas, procesos, red, gráficas) se recalcula al volver a mostrarla.
        if (!UiVisible) return;

        // Temperaturas: los sensores marcados en Ajustes → Temperaturas (barra sobre 0–105 °C).
        // Por defecto: CPU máx., GPU, GPU hotspot y la temperatura principal de cada disco.
        UpdateTempChoices(s);
        int ti = 0;
        foreach (var tc in TempChoices)
        {
            if (!tc.IsChecked || tc.Value is not double v) continue;
            double pct = Math.Clamp(v / 105 * 100, 0, 100);
            string label = tc.ShortLabel, text = Fmt.Temp(tc.Value);
            if (ti == TempRows.Count) TempRows.Add(new BarRow(label, text, pct, tc.Bar));
            else TempRows[ti].Update(label, text, pct, tc.Bar);
            ti++;
        }
        Trim(TempRows, ti);

        // Ventiladores
        int fi = 0;
        foreach (var f in s.Fans)
        {
            if (f.Rpm is not double rpm) continue;
            string name = f.Name, text = $"{rpm:F0} RPM";
            double pct = Math.Clamp(rpm / 5000 * 100, 0, 100);
            if (fi == FanRows.Count) FanRows.Add(new BarRow(name, text, pct, BarCpu));
            else FanRows[fi].Update(name, text, pct, BarCpu);
            fi++;
        }
        Trim(FanRows, fi);

        // Procesos: filas reutilizadas, solo se añade o quita alguna si cambia cuántas hay.
        Geometry? Ic(ProcInfo p) => Application.Current.TryFindResource(ProcessMonitor.IconKey(p.Name)) as Geometry;
        void FillView(ObservableCollection<ProcView> rows, IEnumerable<ProcInfo> procs)
        {
            int i = 0;
            foreach (var p in procs)
            {
                double cpu = _cpuSmooth.TryGetValue(p.Name, out var c) ? c : p.CpuPct;
                var icon = Ic(p);
                string cpuText = $"{cpu:F1} %", memText = FormatBytes(p.MemBytes);
                if (i == rows.Count) rows.Add(new ProcView(p.Name, icon, cpuText, memText, cpu));
                else rows[i].Update(p.Name, icon, cpuText, memText, cpu);
                i++;
            }
            Trim(rows, i);
        }
        void FillPair(ObservableCollection<ProcPair> rows, IEnumerable<ProcInfo> procs, Func<ProcInfo, (string a, string b)> fmt)
        {
            int i = 0;
            foreach (var p in procs)
            {
                var (a, b) = fmt(p);
                var icon = Ic(p);
                if (i == rows.Count) rows.Add(new ProcPair(p.Name, icon, a, b));
                else rows[i].Update(p.Name, icon, a, b);
                i++;
            }
            Trim(rows, i);
        }

        // Orden por CPU suavizado (si no, los procesos saltan de puesto en cada lectura) o por memoria.
        var byCpu = _sortByMem
            ? _procs.OrderByDescending(p => p.MemBytes)
            : _procs.OrderByDescending(p => _cpuSmooth.GetValueOrDefault(p.Name)).ThenByDescending(p => p.MemBytes);
        FillView(TopCpu, byCpu.Take(5));
        FillView(TopMem, _procs.OrderByDescending(p => p.MemBytes).Take(8));

        // Listas por página: solo procesos con actividad, ordenados de mayor a menor.
        FillPair(TopCpuPage, _procs.OrderByDescending(p => _cpuSmooth.GetValueOrDefault(p.Name)).Take(8),
            p => ($"{_cpuSmooth.GetValueOrDefault(p.Name, p.CpuPct):F1} %", ""));
        FillPair(TopGpu, _procs.Where(p => p.GpuPct >= 0.1).OrderByDescending(p => p.GpuPct).Take(8),
            p => ($"{p.GpuPct:F1} %", p.GpuMem > 0 ? FormatBytes(p.GpuMem) : "—"));
        FillPair(TopDisk, _procs.Where(p => p.ReadBps + p.WriteBps >= 1024).OrderByDescending(p => p.ReadBps + p.WriteBps).Take(8),
            p => (Fmt.Rate(p.ReadBps), Fmt.Rate(p.WriteBps)));
        FillPair(TopNet, _procs.Where(p => p.NetDownBps + p.NetUpBps >= 1).OrderByDescending(p => p.NetDownBps + p.NetUpBps).Take(8),
            p => (Fmt.Rate(p.NetDownBps), Fmt.Rate(p.NetUpBps)));
        if (s.Processes.Count > 0) _netByProcess = s.NetByProcessAvailable;

        // Memoria virtual
        if (s.VirtUsedGb is double vu2 && s.VirtAvailGb is double va2 && vu2 + va2 > 0)
        {
            VirtText = $"{Fmt.Gb(vu2)} / {Fmt.Gb(vu2 + va2)}";
            VirtPct = vu2 / (vu2 + va2) * 100;
        }

        // Red
        NetDownText = Fmt.Rate(s.NetDownBps);
        NetUpText = Fmt.Rate(s.NetUpBps);
        NetDownSeries = _netDownChart.Snapshot(_netDownHist);
        NetUpSeries = _netUpChart.Snapshot(_netUpHist);
        int ni = 0;
        foreach (var n in s.Networks.OrderByDescending(n => (n.DownTotalGb ?? 0) + (n.UpTotalGb ?? 0)))
        {
            string name = n.Name, down = Fmt.Rate(n.DownBps), up = Fmt.Rate(n.UpBps),
                   totals = $"{Loc.T("Received")} {Fmt.Gb(n.DownTotalGb)} · {Loc.T("Sent")} {Fmt.Gb(n.UpTotalGb)}";
            if (ni == NetRows.Count) NetRows.Add(new NetView(name, down, up, totals));
            else NetRows[ni].Update(name, down, up, totals);
            ni++;
        }
        Trim(NetRows, ni);
    }

    /// <summary>Quita las filas que sobran (las listas se rellenan por posición: solo cambia su número,
    /// no su contenido, cuando aparece o desaparece un proceso).</summary>
    private static void Trim<T>(ObservableCollection<T> rows, int count)
    {
        while (rows.Count > count) rows.RemoveAt(rows.Count - 1);
    }

    private static string JoinDot(params string?[] parts)
    {
        var s = string.Join("  ·  ", parts.Where(p => !string.IsNullOrEmpty(p)));
        return s.Length == 0 ? " " : s;
    }

    public static string FormatBytes(long b) => b switch
    {
        >= 1L << 30 => $"{b / (double)(1L << 30):F1} GB",
        >= 1L << 20 => $"{b / (double)(1L << 20):F0} MB",
        _ => $"{b / 1024.0:F0} KB",
    };

    private bool _drivesBusy;

    /// <summary>Lee las unidades fuera del hilo de la interfaz: con una unidad de red mapeada caída,
    /// IsReady puede bloquearse varios segundos y antes congelaba la ventana. Al volver, la colección
    /// se actualiza en sitio (mismas unidades → Update; alguna de más o de menos → añadir o quitar).</summary>
    private async Task RefreshDrivesAsync()
    {
        if (_drivesBusy) return; // la lectura anterior aún no ha vuelto: no se solapan ni llegan desordenadas
        _drivesBusy = true;
        try
        {
            var fresh = await Task.Run(ReadDrives);
            int i = 0;
            foreach (var d in fresh)
            {
                if (i == Drives.Count) Drives.Add(new DriveView(d.Name, d.Label, d.Detail, d.PctText, d.Pct, d.Brush, d.FreeTotal));
                else Drives[i].Update(d.Name, d.Label, d.Detail, d.PctText, d.Pct, d.Brush, d.FreeTotal);
                i++;
            }
            Trim(Drives, i);
        }
        catch (Exception ex) { ErrorLog.Write("Drives", ex); }
        finally { _drivesBusy = false; }
    }

    private static List<DriveView> ReadDrives()
    {
        var list = new List<DriveView>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                // DriveType no toca la unidad (sale del formato de la letra); IsReady sí. Con una unidad de
                // red caída, IsReady podía congelar la llamada: por eso el tipo se filtra primero.
                if (d.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                if (!d.IsReady || d.TotalSize <= 0) continue;
                double used = d.TotalSize - d.TotalFreeSpace;
                double pct = used / d.TotalSize * 100;
                var brush = pct >= 90 ? Fmt.Hot : pct >= 80 ? Fmt.Warn : BarDisk;
                list.Add(new DriveView(d.Name.TrimEnd('\\'), string.IsNullOrWhiteSpace(d.VolumeLabel) ? d.DriveFormat : d.VolumeLabel,
                    Loc.F("FreeOf", FormatBytes(d.TotalFreeSpace), FormatBytes(d.TotalSize)), $"{pct:F0} %", pct, brush,
                    $"{d.TotalFreeSpace / (double)(1L << 30):F0} / {d.TotalSize / (double)(1L << 30):F0} GB"));
            }
            catch { /* unidad que se desconectó */ }
        }
        return list;
    }
}
