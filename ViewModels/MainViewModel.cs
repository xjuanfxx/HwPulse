using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace HwPulse;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    protected void RaiseAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}

public sealed class CoreRow : Observable
{
    public int Index { get; init; }
    public string Name { get; private set; } = "";
    public string ClockText { get; private set; } = Fmt.None;
    public string TempText { get; private set; } = Fmt.None;
    public Brush TempBrush { get; private set; } = Fmt.Neutral;
    public double Load { get; private set; }
    public string LoadText { get; private set; } = Fmt.None;

    public void Update(CoreReading r)
    {
        Name = $"{Loc.T("Core")} {Index}";
        ClockText = Fmt.Ghz(r.ClockMhz);
        TempText = Fmt.Temp(r.TempC);
        TempBrush = Fmt.TempBrush(r.TempC);
        Load = r.Load ?? 0;
        LoadText = Fmt.Pct(r.Load);
        RaiseAll();
    }
}

public sealed class DiskRow : Observable
{
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public string TempText { get; private set; } = Fmt.None;
    public Brush TempBrush { get; private set; } = Fmt.Neutral;
    public double Activity { get; private set; }
    public string ActivityText { get; private set; } = Fmt.None;
    public string Tip { get; private set; } = "";

    public void Update(DiskReading d)
    {
        TempText = Fmt.Temp(d.TempC);
        // Los SSD aguantan más que el CPU, pero sobre ~70 °C empiezan a bajar velocidad.
        TempBrush = d.TempC switch { null => Fmt.Neutral, >= 70 => Fmt.Hot, >= 60 => Fmt.Warn, _ => Fmt.Neutral };
        Activity = d.Activity ?? 0;
        ActivityText = Fmt.Pct(d.Activity);
        Tip = Loc.F("DiskTip", Fmt.Rate(d.ReadBps), Fmt.Rate(d.WriteBps))
              + (d.UsedPct is double u ? " · " + Loc.F("DiskUsed", $"{u:F0} %") : "");
        RaiseAll();
    }
}

public sealed class SensorRow : Observable
{
    public string Name { get; init; } = "";
    public Visibility Visibility { get; private set; } = Visibility.Visible;
    public string ValueText { get; private set; } = Fmt.None;
    public string MinText { get; private set; } = Fmt.None;
    public string MaxText { get; private set; } = Fmt.None;
    public string Type { get; private set; } = "";
    public string Hardware { get; init; } = "";

    public void Update(SensorReading r)
    {
        Type = Fmt.TypeName(r.Type);
        ValueText = Fmt.Sensor(r.Type, r.Value);
        MinText = Fmt.Sensor(r.Type, r.Min);
        MaxText = Fmt.Sensor(r.Type, r.Max);
        RaiseAll();
    }

    public bool ApplyFilter(string f)
    {
        bool match = f.Length == 0
                     || Name.Contains(f, StringComparison.OrdinalIgnoreCase)
                     || Type.Contains(f, StringComparison.OrdinalIgnoreCase)
                     || Hardware.Contains(f, StringComparison.OrdinalIgnoreCase);
        var v = match ? Visibility.Visible : Visibility.Collapsed;
        if (v != Visibility) { Visibility = v; Raise(nameof(Visibility)); }
        return match;
    }
}

public sealed class SensorGroup : Observable
{
    public string Name { get; init; } = "";
    public ObservableCollection<SensorRow> Rows { get; } = new();
    public Visibility Visibility { get; private set; } = Visibility.Visible;

    private bool _expanded = true;
    /// <summary>Colapsar un componente oculta sus filas (y deja de actualizarlas en pantalla).</summary>
    public bool IsExpanded
    {
        get => _expanded;
        set { if (_expanded == value) return; _expanded = value; Raise(); Raise(nameof(RowsVisibility)); }
    }
    public Visibility RowsVisibility => _expanded ? Visibility.Visible : Visibility.Collapsed;
    public string CountText => Loc.F("SensorCount", Rows.Count);

    public int ApplyFilter(string f)
    {
        int n = Rows.Count(r => r.ApplyFilter(f));
        var v = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (v != Visibility) { Visibility = v; Raise(nameof(Visibility)); }
        return n;
    }
}

public sealed partial class MainViewModel : Observable
{
    private const int HistorySeconds = 600;
    private readonly AppSettings _settings;

    private readonly MinMax _cpuTemp = new(), _cpuClock = new(), _cpuPower = new(), _gpuTemp = new(), _gpuLoad = new(), _gpuPower = new();
    private readonly Dictionary<string, SensorRow> _sensorRows = new();
    private readonly Dictionary<string, SensorGroup> _sensorGroups = new();
    private readonly Dictionary<string, DiskRow> _diskRows = new();
    private readonly RingBuffer _cpuTempHist = new(HistorySeconds), _cpuClockHist = new(HistorySeconds),
                                _gpuTempHist = new(HistorySeconds), _gpuLoadHist = new(HistorySeconds), _ramHist = new(HistorySeconds);
    private double _clockUnderLoadMax;
    private Snapshot? _last;
    private string _filter = "";

    /// <summary>Acciones que la App conecta (overlay, idioma, etc.).</summary>
    public Action<bool>? OverlayVisibilityChanged;
    public Action? OverlayLayoutChanged;
    public Action<string>? LanguageChanged;
    /// <summary>Algún ajuste cambió y hay que guardarlo (la App lo guarda con retraso, no en cada paso).</summary>
    public Action? SettingsDirty;

    public MainViewModel(AppSettings settings, bool isAdmin)
    {
        _settings = settings;
        IsAdmin = isAdmin;
    }

    public bool IsAdmin { get; }
    public string Version { get; } = "HwPulse " + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "");

    // ---------------------------------------------------------------- CPU
    public string CpuName { get; private set; } = "";
    public string CpuTempText { get; private set; } = Fmt.None;
    public string CpuTempShort { get; private set; } = Fmt.None;
    public Brush CpuTempBrush { get; private set; } = Fmt.Neutral;
    public string CpuTempMinMax { get; private set; } = " ";
    public string CpuClockText { get; private set; } = Fmt.None;
    public string CpuClockShort { get; private set; } = Fmt.None;
    public string CpuClockMinMax { get; private set; } = " ";
    public string CpuLoadText { get; private set; } = Fmt.None;
    public string CpuPowerText { get; private set; } = Fmt.None;
    public string CpuPowerSub { get; private set; } = " ";
    public string CpuDetails { get; private set; } = " ";
    public ObservableCollection<CoreRow> Cores { get; } = new();
    public double[] CpuTempSeries { get; private set; } = Array.Empty<double>();
    public double[] CpuClockSeries { get; private set; } = Array.Empty<double>();

    // ---------------------------------------------------------------- GPU
    public string GpuName { get; private set; } = "";
    public string GpuLoadText { get; private set; } = Fmt.None;
    public string GpuLoadMinMax { get; private set; } = " ";
    public string GpuTempText { get; private set; } = Fmt.None;
    public string GpuTempShort { get; private set; } = Fmt.None;
    public Brush GpuTempBrush { get; private set; } = Fmt.Neutral;
    public string GpuTempMinMax { get; private set; } = " ";
    public string GpuCoreClockText { get; private set; } = Fmt.None;
    public string GpuPowerText { get; private set; } = Fmt.None;
    public string GpuPowerMinMax { get; private set; } = " ";
    public string GpuDetails { get; private set; } = " ";
    public string VramText { get; private set; } = Fmt.None;
    public double VramPct { get; private set; }
    public double[] GpuTempSeries { get; private set; } = Array.Empty<double>();
    public double[] GpuLoadSeries { get; private set; } = Array.Empty<double>();

    // ---------------------------------------------------------------- RAM, discos, energía
    public string RamText { get; private set; } = Fmt.None;
    public string RamDetail { get; private set; } = " ";
    public double RamPct { get; private set; }
    public double[] RamSeries { get; private set; } = Array.Empty<double>();

    public ObservableCollection<DiskRow> Disks { get; } = new();
    public Visibility NoDisksVisibility => Disks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public string PowerSourceText { get; private set; } = Fmt.None;
    public Brush PowerSourceBrush { get; private set; } = Fmt.Neutral;
    public string BatteryText { get; private set; } = Fmt.None;
    /// <summary>En un PC de escritorio no hay batería: se ocultan "Fuente" y "Batería".</summary>
    public bool HasBattery { get; } = Native.HasBattery();
    public Visibility BatteryRowsVisibility => HasBattery ? Visibility.Visible : Visibility.Collapsed;
    public string TotalPowerText { get; private set; } = Fmt.None;

    // ---------------------------------------------------------------- Estado
    public string StatusText { get; private set; } = "";
    public Brush StatusBrush { get; private set; } = Fmt.Warn;
    public Visibility StatusVisibility => string.IsNullOrEmpty(StatusText) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ThrottleVisibility { get; private set; } = Visibility.Collapsed;
    public string ModeText => Loc.T(IsAdmin ? "Administrator" : "LimitedMode");
    public Brush ModeBrush => IsAdmin ? Fmt.Ok : Fmt.Warn;
    public string RefreshText => Loc.T("Refresh");
    public bool HotkeysFailed { get; set; }
    public string HotkeyText => Loc.T(HotkeysFailed ? "HotkeyFail" : "HotkeyHint");
    public Visibility HotkeyFailVisibility => HotkeysFailed ? Visibility.Visible : Visibility.Collapsed;
    public string TrayText { get; private set; } = "HwPulse";

    // ---------------------------------------------------------------- Sensores
    public ObservableCollection<SensorGroup> SensorGroups { get; } = new();
    public string SensorCountText { get; private set; } = "";

    public string SensorFilter
    {
        get => _filter;
        set { _filter = (value ?? "").Trim(); ApplyFilter(); Raise(); }
    }

    // ---------------------------------------------------------------- Ajustes
    public bool OverlayVisible
    {
        get => _settings.OverlayVisible;
        set { if (value == _settings.OverlayVisible) return; _settings.OverlayVisible = value; Raise(); OverlayVisibilityChanged?.Invoke(value); }
    }

    public bool LangEnglish { get => _settings.Language != "es"; set { if (value) SetLanguage("en"); } }
    public bool LangSpanish { get => _settings.Language == "es"; set { if (value) SetLanguage("es"); } }

    private void SetLanguage(string lang)
    {
        if (_settings.Language == lang) return;
        _settings.Language = lang;
        Loc.I.SetLanguage(lang);
        LanguageChanged?.Invoke(lang);
        Rerender();
    }

    public bool CornerTC { get => _settings.Corner == OverlayCorner.TopCenter; set { if (value) SetCorner(OverlayCorner.TopCenter); } }
    public bool CornerTL { get => _settings.Corner == OverlayCorner.TopLeft; set { if (value) SetCorner(OverlayCorner.TopLeft); } }
    public bool CornerTR { get => _settings.Corner == OverlayCorner.TopRight; set { if (value) SetCorner(OverlayCorner.TopRight); } }
    public bool CornerBL { get => _settings.Corner == OverlayCorner.BottomLeft; set { if (value) SetCorner(OverlayCorner.BottomLeft); } }
    public bool CornerBR { get => _settings.Corner == OverlayCorner.BottomRight; set { if (value) SetCorner(OverlayCorner.BottomRight); } }

    public void SetCorner(OverlayCorner c)
    {
        _settings.Corner = c;
        Raise(nameof(CornerTC)); Raise(nameof(CornerTL)); Raise(nameof(CornerTR)); Raise(nameof(CornerBL)); Raise(nameof(CornerBR));
        OverlayLayoutChanged?.Invoke();
    }

    public void CycleCorner() => SetCorner(_settings.Corner switch
    {
        OverlayCorner.TopCenter => OverlayCorner.TopRight,
        OverlayCorner.TopLeft => OverlayCorner.TopCenter,
        OverlayCorner.TopRight => OverlayCorner.BottomRight,
        OverlayCorner.BottomRight => OverlayCorner.BottomLeft,
        _ => OverlayCorner.TopLeft,
    });

    public double OverlayOpacity
    {
        get => _settings.OverlayOpacity;
        set
        {
            _settings.OverlayOpacity = Math.Clamp(value, 0, 1);
            Raise(); Raise(nameof(OverlayBackground)); Raise(nameof(OpacityText)); Raise(nameof(OpacityPercent)); SettingsDirty?.Invoke();
        }
    }

    public double OverlayScale
    {
        get => _settings.OverlayScale;
        set
        {
            _settings.OverlayScale = Math.Clamp(value, 0.7, 2);
            Raise(); Raise(nameof(ScaleText)); Raise(nameof(ScalePercent));
            OverlayLayoutChanged?.Invoke();
        }
    }

    /// <summary>Opacidad en % para la caja de texto (0–100); valores fuera de rango se ajustan.</summary>
    public int OpacityPercent
    {
        get => (int)Math.Round(OverlayOpacity * 100);
        set { OverlayOpacity = Math.Clamp(value, 0, 100) / 100.0; Raise(); }
    }

    /// <summary>Tamaño en % para la caja de texto (70–200).</summary>
    public int ScalePercent
    {
        get => (int)Math.Round(OverlayScale * 100);
        set { OverlayScale = Math.Clamp(value, 70, 200) / 100.0; Raise(); }
    }

    public string OpacityText => $"{OverlayOpacity * 100:F0} %";
    public string ScaleText => $"{OverlayScale * 100:F0} %";
    public Brush OverlayBackground
    {
        get
        {
            var c = OverlayColor("bg");
            return Fmt.Freeze(Color.FromArgb((byte)(OverlayOpacity * 255), c.R, c.G, c.B));
        }
    }

    // ---------------------------------------------------------------- Colores del overlay
    public Color OverlayColor(string key)
    {
        // Barras de CPU y GPU: color de la marca, salvo que el usuario haya elegido uno propio.
        if (!_settings.OverlayColors.ContainsKey(key))
        {
            if (key == "cpu") return CpuBrandColor;
            if (key == "gpu") return GpuBrandColor;
        }
        try { return (Color)ColorConverter.ConvertFromString(_settings.OverlayColor(key)); }
        catch { return (Color)ColorConverter.ConvertFromString(AppSettings.DefaultOverlayColors[key]); }
    }

    private Brush OvBrush(string key, byte alpha = 255)
    {
        var c = OverlayColor(key);
        return Fmt.Freeze(Color.FromArgb(alpha, c.R, c.G, c.B));
    }

    public Brush OvTextBrush => OvBrush("text");
    public Brush OvLabelBrush => OvBrush("labels");
    public Brush OvBorderBrush => OvBrush("border", 0x33);
    public Brush OvDividerBrush => OvBrush("border", 0x40);
    public Brush OvCpuBrush => OvBrush("cpu");
    public Brush OvGpuBrush => OvBrush("gpu");
    public Brush OvMemBrush => OvBrush("mem");

    public void SetOverlayColor(string key, Color c)
    {
        _settings.OverlayColors[key] = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        RaiseOverlayColors();
    }

    public void ResetOverlayColors()
    {
        _settings.OverlayColors.Clear();
        RaiseOverlayColors();
    }

    private void RaiseOverlayColors()
    {
        foreach (var n in new[] { nameof(OvTextBrush), nameof(OvLabelBrush), nameof(OvBorderBrush), nameof(OvDividerBrush),
                                  nameof(OvCpuBrush), nameof(OvGpuBrush), nameof(OvMemBrush), nameof(OverlayBackground) })
            Raise(n);
    }

    // ---------------------------------------------------------------- Lectura
    public void Apply(Snapshot s)
    {
        _last = s;
        _cpuTemp.Add(s.CpuTempMax); _cpuClock.Add(s.CpuClockAvg); _cpuPower.Add(s.CpuPower);
        _gpuTemp.Add(s.GpuTemp); _gpuLoad.Add(s.GpuLoad); _gpuPower.Add(s.GpuPower);
        _cpuTempHist.Add(s.CpuTempMax);
        _cpuClockHist.Add(s.CpuClockAvg is double c ? c / 1000 : null);
        _gpuTempHist.Add(s.GpuTemp); _gpuLoadHist.Add(s.GpuLoad); _ramHist.Add(s.RamLoad);
        if (s.CpuCoreLoadMax is >= 80 && s.CpuClockAvg is double clk) _clockUnderLoadMax = Math.Max(_clockUnderLoadMax, clk);
        RecordExtra(s);
        Render(s);
    }

    /// <summary>Vuelve a formatear la última lectura (al cambiar de idioma).</summary>
    public void Rerender()
    {
        Raise(nameof(LangEnglish)); Raise(nameof(LangSpanish));
        if (_last != null) Render(_last); else RaiseAll();
    }

    private void Render(Snapshot s)
    {
        // CPU
        CpuName = s.CpuName ?? "";
        CpuTempText = Fmt.Temp(s.CpuTempMax);
        CpuTempShort = Fmt.TempShort(s.CpuTempMax);
        CpuTempBrush = Fmt.TempBrush(s.CpuTempMax);
        CpuTempMinMax = Fmt.MinMax(_cpuTemp, Fmt.TempShort);
        CpuClockText = Fmt.Ghz(s.CpuClockAvg);
        CpuClockShort = s.CpuClockAvg is double ck ? (ck / 1000).ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + " GHz" : Fmt.None;
        CpuClockMinMax = Fmt.MinMax(_cpuClock, Fmt.Ghz);
        CpuLoadText = Fmt.Pct(s.CpuLoad);
        CpuPowerText = Fmt.Watts(s.CpuPower);
        CpuPowerSub = s.CpuVoltage is double v ? Loc.F("Vcore", Fmt.Volts(v)) : " ";
        CpuDetails = Join(
            s.CpuTempPkg is double p ? Loc.F("Package", Fmt.Temp(p)) : null,
            s.TjDistanceMin is double tj ? Loc.F("TjMargin", Fmt.Temp(tj)) : null);
        CpuTempSeries = _cpuTempHist.ToArray();
        CpuClockSeries = _cpuClockHist.ToArray();

        if (Cores.Count != s.Cores.Count)
        {
            Cores.Clear();
            foreach (var r in s.Cores) Cores.Add(new CoreRow { Index = r.Index });
        }
        for (int i = 0; i < s.Cores.Count; i++) Cores[i].Update(s.Cores[i]);

        // GPU
        GpuName = s.GpuName ?? "";
        GpuLoadText = Fmt.Pct(s.GpuLoad);
        GpuLoadMinMax = Fmt.MinMax(_gpuLoad, Fmt.Pct);
        GpuTempText = Fmt.Temp(s.GpuTemp);
        GpuTempShort = Fmt.TempShort(s.GpuTemp);
        GpuTempBrush = Fmt.TempBrush(s.GpuTemp);
        GpuTempMinMax = Fmt.MinMax(_gpuTemp, Fmt.TempShort);
        GpuCoreClockText = Fmt.Mhz(s.GpuCoreClock);
        GpuPowerText = Fmt.Watts(s.GpuPower);
        GpuPowerMinMax = Fmt.MinMax(_gpuPower, Fmt.Watts);
        GpuDetails = Join(
            s.GpuHotSpot is double hs ? Loc.F("HotSpot", Fmt.Temp(hs)) : null,
            s.GpuMemClock is double mc ? Loc.F("MemClock", Fmt.Mhz(mc)) : null,
            s.GpuFanRpm is double rpm ? Loc.F("Fan", $"{rpm:F0} RPM") : s.GpuFanPct is double fp ? Loc.F("Fan", $"{fp:F0} %") : null);
        if (s.VramUsedMb is double vu && s.VramTotalMb is double vt && vt > 0)
        {
            VramText = $"{Fmt.Gb(vu / 1024)} / {Fmt.Gb(vt / 1024)}";
            VramPct = vu / vt * 100;
        }
        else { VramText = s.VramUsedMb is double only ? Fmt.Gb(only / 1024) : Fmt.None; VramPct = 0; }
        GpuTempSeries = _gpuTempHist.ToArray();
        GpuLoadSeries = _gpuLoadHist.ToArray();

        // RAM
        if (s.RamUsedGb is double ru && s.RamAvailGb is double ra)
        {
            RamText = $"{Fmt.Gb(ru)} / {Fmt.Gb(ru + ra)}";
            RamDetail = Loc.F("Free", Fmt.Gb(ra));
        }
        else { RamText = Fmt.None; RamDetail = " "; }
        RamPct = s.RamLoad ?? 0;
        RamSeries = _ramHist.ToArray();

        // Discos
        var seen = new HashSet<string>();
        foreach (var d in s.Disks)
        {
            seen.Add(d.Key);
            if (!_diskRows.TryGetValue(d.Key, out var row))
            {
                row = new DiskRow { Key = d.Key, Name = d.Name };
                _diskRows[d.Key] = row;
                Disks.Add(row);
            }
            row.Update(d);
        }
        foreach (var gone in _diskRows.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            Disks.Remove(_diskRows[gone]);
            _diskRows.Remove(gone);
        }

        // Energía
        (PowerSourceText, PowerSourceBrush) = s.OnAc switch
        {
            true => (Loc.T("AcAdapter"), Fmt.Ok),
            false => (Loc.T("OnBatteryShort"), Fmt.Warn),
            _ => (Loc.T("Unknown"), Fmt.Neutral),
        };
        string? rate = s.BatteryRateW switch
        {
            double r when r > 0.05 => Loc.F("Charging", Fmt.Watts(r)),
            double r when r < -0.05 => Loc.F("Discharging", Fmt.Watts(-r)),
            double => Loc.T("Full"),
            _ => s.BatteryCharge is >= 99.5 ? Loc.T("Full") : null,
        };
        BatteryText = s.BatteryCharge is null ? Fmt.None : Join(Fmt.Pct(s.BatteryCharge), rate);
        TotalPowerText = s.CpuPower is null && s.GpuPower is null ? Fmt.None : Fmt.Watts((s.CpuPower ?? 0) + (s.GpuPower ?? 0));

        UpdateStatus(s);
        if (UiVisible) UpdateSensors(s); // ~150 filas de la página Sensores: solo si se pueden ver
        RenderExtra(s);

        TrayText = $"HwPulse · CPU {CpuTempShort} {CpuClockShort} · GPU {GpuLoadText} {GpuTempShort}";
        if (TrayText.Length > 63) TrayText = TrayText[..63];

        if (UiVisible) RaiseAll();
        else foreach (var n in OverlayProps) Raise(n); // en la bandeja solo se avisa al overlay
    }

    // ---------------------------------------------------------------- Página abierta
    private string _currentPage = "Overview";
    /// <summary>Página visible (la fija la ventana). Al abrir Sensores se refrescan sus filas al momento.</summary>
    public string CurrentPage
    {
        get => _currentPage;
        set { if (_currentPage == value) return; _currentPage = value; if (value == "Sensors") Rerender(); }
    }

    // ---------------------------------------------------------------- Ventana oculta (bandeja)
    private bool _uiVisible = true;

    /// <summary>
    /// false mientras la ventana principal está oculta o minimizada: se deja de calcular y notificar lo que
    /// solo se ve en ella. Al volver a mostrarla se recalcula todo con la última lectura.
    /// </summary>
    public bool UiVisible
    {
        get => _uiVisible;
        set
        {
            if (_uiVisible == value) return;
            _uiVisible = value;
            if (value) Rerender();
        }
    }

    private static readonly string[] OverlayProps =
    {
        nameof(CpuTempShort), nameof(CpuTempBrush), nameof(CpuClockShort), nameof(CpuLoadText), nameof(GpuLoadText),
        nameof(GpuTempShort), nameof(GpuTempBrush), nameof(FpsText), nameof(FpsBrush), nameof(FrametimeText), nameof(FpsSource),
        nameof(FpsSeries), nameof(CpuLoadPct), nameof(GpuLoadPct), nameof(VramPct), nameof(GpuVramShort), nameof(RamPct),
        nameof(RamShort), nameof(TotalPowerText), nameof(ThrottleVisibility), nameof(StatusText), nameof(TrayText),
    };

    private static string Join(params string?[] parts)
    {
        var s = string.Join("  ·  ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        return s.Length == 0 ? " " : s;
    }

    private void UpdateStatus(Snapshot s)
    {
        bool busy = s.CpuCoreLoadMax is >= 80;
        string text = "";
        Brush brush = Fmt.Warn;
        bool throttle = false;

        if (!s.IsAdmin && s.CpuTempMax is null)
        {
            text = Loc.T("StatusNoAdmin");
        }
        else if (s.TjDistanceMin is <= 5 || s.CpuTempMax is >= 95)
        {
            text = Loc.F("StatusThermal", Fmt.Temp(s.CpuTempMax));
            brush = Fmt.Hot;
            throttle = true;
        }
        else if (busy && s.CpuClockAvg is double c && _clockUnderLoadMax > 0 && c < _clockUnderLoadMax * 0.85)
        {
            text = Loc.F("StatusClockDrop", Fmt.Ghz(c), Fmt.Ghz(_clockUnderLoadMax));
            throttle = true;
        }
        else if (s.OnAc == false)
        {
            text = Loc.T("StatusBattery");
        }

        StatusText = text;
        StatusBrush = brush;
        ThrottleVisibility = throttle ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateSensors(Snapshot s)
    {
        bool added = false;
        foreach (var r in s.AllSensors)
        {
            if (!_sensorRows.TryGetValue(r.Key, out var row))
            {
                if (!_sensorGroups.TryGetValue(r.Hardware, out var g))
                {
                    g = new SensorGroup { Name = r.Hardware };
                    _sensorGroups[r.Hardware] = g;
                    SensorGroups.Add(g);
                }
                row = new SensorRow { Name = r.Name, Hardware = r.Hardware };
                _sensorRows[r.Key] = row;
                g.Rows.Add(row);
                added = true;
                row.Update(r);
                continue;
            }
            // Solo se refrescan las filas que se pueden ver: página Sensores abierta y componente expandido.
            if (CurrentPage == "Sensors" && _sensorGroups[r.Hardware].IsExpanded) row.Update(r);
        }
        if (added) ApplyFilter();
        else SensorCountText = Loc.F("SensorCount", _visibleSensors);
    }

    private int _visibleSensors;

    private void ApplyFilter()
    {
        _visibleSensors = SensorGroups.Sum(g => g.ApplyFilter(_filter));
        SensorCountText = Loc.F("SensorCount", _visibleSensors);
        Raise(nameof(SensorCountText));
    }
}
