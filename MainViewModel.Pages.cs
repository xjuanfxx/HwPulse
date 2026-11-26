using System.IO;
using System.Windows;
using System.Windows.Media;

namespace HwPulse;

public sealed record BarRow(string Label, string ValueText, double Pct, Brush Brush);
/// <summary>Fila de proceso con dos valores (p. ej. GPU y VRAM, lectura y escritura, descarga y subida).</summary>
public sealed record ProcPair(string Name, Geometry? Icon, string A, string B);
public sealed record ProcView(string Name, Geometry? Icon, string CpuText, string MemText, double CpuPct);
public sealed record NetView(string Name, string DownText, string UpText, string TotalsText);
public sealed record DriveView(string Name, string Label, string Detail, string PctText, double Pct, Brush Brush, string FreeTotal = "");

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
    private List<DriveView> _drives = new();
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
    public string FpsText { get; private set; } = "–";
    public Brush FpsBrush { get; private set; } = Fmt.Faint;
    public string FrametimeText { get; private set; } = Fmt.None;
    public string FpsSource { get; private set; } = " ";
    public double[] FpsSeries { get; private set; } = Array.Empty<double>();

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
    public double[] CpuLoadSeries { get; private set; } = Array.Empty<double>();

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
    public List<BarRow> TempRows { get; private set; } = new();
    public Visibility NoTempsVisibility => TempRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public List<ProcView> TopCpu { get; private set; } = new();
    public List<ProcView> TopMem { get; private set; } = new();
    public List<ProcPair> TopCpuPage { get; private set; } = new();
    public List<ProcPair> TopGpu { get; private set; } = new();
    public List<ProcPair> TopDisk { get; private set; } = new();
    public List<ProcPair> TopNet { get; private set; } = new();
    private bool _netByProcess;
    public Visibility NoGpuProcsVisibility => TopGpu.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NoDiskProcsVisibility => TopDisk.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NoNetProcsVisibility => TopNet.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public string NoNetProcsText => Loc.T(_netByProcess ? "NoActivity" : "NetNeedsAdmin");
    public List<BarRow> FanRows { get; private set; } = new();
    public Visibility NoFansVisibility => FanRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public List<DriveView> Drives => _drives;

    // ---------------------------------------------------------------- Memoria
    public string VirtText { get; private set; } = Fmt.None;
    public double VirtPct { get; private set; }

    // ---------------------------------------------------------------- Red
    public string NetDownText { get; private set; } = Fmt.None;
    public string NetUpText { get; private set; } = Fmt.None;
    public double[] NetDownSeries { get; private set; } = Array.Empty<double>();
    public double[] NetUpSeries { get; private set; } = Array.Empty<double>();
    public List<NetView> NetRows { get; private set; } = new();
    public Visibility NoNetVisibility => NetRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    // ---------------------------------------------------------------- Estilo del overlay
    public bool OverlayPanel { get => _settings.OverlayStyle != "compact"; set { if (value) SetOverlayStyle("panel"); } }
    public bool OverlayCompact { get => _settings.OverlayStyle == "compact"; set { if (value) SetOverlayStyle("compact"); } }
    public Visibility OverlayPanelVisibility => OverlayPanel ? Visibility.Visible : Visibility.Collapsed;
    public Visibility OverlayCompactVisibility => OverlayCompact ? Visibility.Visible : Visibility.Collapsed;

    private void SetOverlayStyle(string style)
    {
        _settings.OverlayStyle = style;
        Raise(nameof(OverlayPanel)); Raise(nameof(OverlayCompact));
        Raise(nameof(OverlayPanelVisibility)); Raise(nameof(OverlayCompactVisibility));
        OverlayLayoutChanged?.Invoke();
    }

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
        if (_driveTick++ % 5 == 0) _drives = ReadDrives();
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
        FpsSeries = s.Fps is null ? Array.Empty<double>() : _fpsHist.ToArray();

        // Tarjetas
        CpuCardSub = JoinDot(s.CpuTempMax is double t ? Fmt.Temp(t) : null, s.CpuClockAvg is double c ? Fmt.Ghz(c) : null);
        GpuCardSub = JoinDot(s.GpuTemp is double gt ? Fmt.Temp(gt) : null, s.GpuCoreClock is double gc ? Fmt.Mhz(gc) : null);
        RamPctText = Fmt.Pct(s.RamLoad);
        RamCardSub = s.RamUsedGb is double ru && s.RamAvailGb is double ra ? $"{ru:F1} / {ru + ra:F1} GB" : " ";
        var sys = _drives.FirstOrDefault(d => d.Name.StartsWith(Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\') ?? "C:", StringComparison.OrdinalIgnoreCase))
                  ?? _drives.FirstOrDefault();
        StoragePctText = sys?.PctText ?? Fmt.None;
        StoragePct = sys?.Pct ?? 0;
        StorageCardSub = sys != null ? $"{sys.Name} · {sys.Detail}" : " ";
        StorageName = sys?.Name ?? "";
        StorageFreeTotal = sys?.FreeTotal ?? Fmt.None;
        GpuClockGhz = Fmt.Ghz(s.GpuCoreClock);
        CpuTempCard = Fmt.Temp(s.CpuTempMax);
        CpuLoadSeries = _cpuLoadHist.ToArray();

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
        TempRows = SelectedTempRows();

        // Ventiladores
        FanRows = s.Fans.Where(f => f.Rpm is > 0).Select(f => new BarRow(f.Name, $"{f.Rpm:F0} RPM", Math.Clamp((f.Rpm ?? 0) / 5000 * 100, 0, 100), BarCpu)).ToList();

        // Procesos
        ProcView View(ProcInfo p)
        {
            double cpu = _cpuSmooth.TryGetValue(p.Name, out var c) ? c : p.CpuPct;
            return new(p.Name, Application.Current.TryFindResource(ProcessMonitor.IconKey(p.Name)) as Geometry,
                $"{cpu:F1} %", FormatBytes(p.MemBytes), cpu);
        }
        // Orden por CPU suavizado (si no, los procesos saltan de puesto en cada lectura) o por memoria.
        TopCpu = (_sortByMem
                ? _procs.OrderByDescending(p => p.MemBytes)
                : _procs.OrderByDescending(p => _cpuSmooth.GetValueOrDefault(p.Name)).ThenByDescending(p => p.MemBytes))
            .Take(5).Select(View).ToList();
        TopMem = _procs.OrderByDescending(p => p.MemBytes).Take(8).Select(View).ToList();

        // Listas por página: solo procesos con actividad, ordenados de mayor a menor.
        Geometry? Ic(ProcInfo p) => Application.Current.TryFindResource(ProcessMonitor.IconKey(p.Name)) as Geometry;
        TopCpuPage = _procs.OrderByDescending(p => _cpuSmooth.GetValueOrDefault(p.Name)).Take(8)
            .Select(p => new ProcPair(p.Name, Ic(p), $"{_cpuSmooth.GetValueOrDefault(p.Name, p.CpuPct):F1} %", "")).ToList();
        TopGpu = _procs.Where(p => p.GpuPct >= 0.1).OrderByDescending(p => p.GpuPct).Take(8)
            .Select(p => new ProcPair(p.Name, Ic(p), $"{p.GpuPct:F1} %", p.GpuMem > 0 ? FormatBytes(p.GpuMem) : "—")).ToList();
        TopDisk = _procs.Where(p => p.ReadBps + p.WriteBps >= 1024).OrderByDescending(p => p.ReadBps + p.WriteBps).Take(8)
            .Select(p => new ProcPair(p.Name, Ic(p), Fmt.Rate(p.ReadBps), Fmt.Rate(p.WriteBps))).ToList();
        TopNet = _procs.Where(p => p.NetDownBps + p.NetUpBps >= 1).OrderByDescending(p => p.NetDownBps + p.NetUpBps).Take(8)
            .Select(p => new ProcPair(p.Name, Ic(p), Fmt.Rate(p.NetDownBps), Fmt.Rate(p.NetUpBps))).ToList();
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
        NetDownSeries = _netDownHist.ToArray();
        NetUpSeries = _netUpHist.ToArray();
        NetRows = s.Networks
            .OrderByDescending(n => (n.DownTotalGb ?? 0) + (n.UpTotalGb ?? 0))
            .Select(n => new NetView(n.Name, Fmt.Rate(n.DownBps), Fmt.Rate(n.UpBps),
                $"{Loc.T("Received")} {Fmt.Gb(n.DownTotalGb)} · {Loc.T("Sent")} {Fmt.Gb(n.UpTotalGb)}"))
            .ToList();
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

    private static List<DriveView> ReadDrives()
    {
        var list = new List<DriveView>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || d.DriveType is not (DriveType.Fixed or DriveType.Removable) || d.TotalSize <= 0) continue;
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
