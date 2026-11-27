using System.Collections.ObjectModel;
using System.Windows;

namespace HwPulse;

/// <summary>Casilla "Mostrar en el overlay" de Ajustes.</summary>
public sealed class OverlayItemChoice : Observable
{
    private readonly Action _changed;
    private bool _checked;
    public OverlayItemChoice(string key, bool on, Action changed) { Key = key; _checked = on; _changed = changed; }
    public string Key { get; }
    public string Label => Loc.T("Ov." + Key);
    public bool IsChecked
    {
        get => _checked;
        set { if (_checked == value) return; _checked = value; Raise(); _changed(); }
    }
    public void RefreshLabel() => Raise(nameof(Label));
}

/// <summary>Qué valores muestra el overlay. Cada grupo (FPS, CPU, GPU…) se oculta entero si no queda nada visible.</summary>
public sealed partial class MainViewModel
{
    private ObservableCollection<OverlayItemChoice>? _ovChoices;

    public ObservableCollection<OverlayItemChoice> OverlayItemChoices =>
        _ovChoices ??= new(AppSettings.OverlayItemKeys.Select(k => new OverlayItemChoice(k, _settings.OverlayItems.Contains(k), OnOverlayItemsChanged)));

    private void OnOverlayItemsChanged()
    {
        _settings.OverlayItems = OverlayItemChoices.Where(c => c.IsChecked).Select(c => c.Key).ToList();
        foreach (var n in OverlayVisibilityProps) Raise(n);
        OverlayLayoutChanged?.Invoke();
    }

    private bool On(string k) => _settings.OverlayItems.Contains(k);
    private static Visibility V(bool b) => b ? Visibility.Visible : Visibility.Collapsed;

    // Valores sueltos
    public Visibility OvFps => V(On("fps"));
    public Visibility OvFrametime => V(On("frametime"));
    public Visibility OvCpuLoad => V(On("cpuLoad"));
    public Visibility OvCpuTemp => V(On("cpuTemp"));
    public Visibility OvCpuClock => V(On("cpuClock"));
    public Visibility OvGpuLoad => V(On("gpuLoad"));
    public Visibility OvGpuTemp => V(On("gpuTemp"));

    // Grupos
    private bool FpsG => On("fps") || On("frametime");
    /// <summary>El overlay tiene marcado FPS o frametime: puede hacer falta la sesión ETW de FPS.</summary>
    public bool OverlayHasFps => _settings.OverlayStyle == "custom"
        ? (_settings.OverlayLayout?.Elements.Any(e => e.Visible && e.Key is "fps" or "frametime") ?? false)
        : FpsG;
    private bool CpuG => On("cpuLoad") || On("cpuTemp") || On("cpuClock");
    private bool GpuG => On("gpuLoad") || On("gpuTemp");
    private bool VramG => On("vram");
    private bool RamG => On("ram");
    private bool PowerG => On("power");
    public Visibility OvFpsGroup => V(FpsG);
    public Visibility OvCpuGroup => V(CpuG);
    public Visibility OvGpuGroup => V(GpuG);
    public Visibility OvVramGroup => V(VramG);
    public Visibility OvRamGroup => V(RamG);
    public Visibility OvPowerGroup => V(PowerG);
    public Visibility OvRowsDivider => V(FpsG && (CpuG || GpuG || VramG || RamG || PowerG));

    // Separadores de la versión de una línea: solo entre grupos visibles
    public Visibility OvSepCpu => V(CpuG && FpsG);
    public Visibility OvSepGpu => V(GpuG && (FpsG || CpuG));
    public Visibility OvSepVram => V(VramG && (FpsG || CpuG || GpuG));
    public Visibility OvSepRam => V(RamG && (FpsG || CpuG || GpuG || VramG));
    public Visibility OvSepPower => V(PowerG && (FpsG || CpuG || GpuG || VramG || RamG));

    private static readonly string[] OverlayVisibilityProps =
    {
        nameof(OvFps), nameof(OvFrametime), nameof(OvCpuLoad), nameof(OvCpuTemp), nameof(OvCpuClock), nameof(OvGpuLoad), nameof(OvGpuTemp),
        nameof(OvFpsGroup), nameof(OvCpuGroup), nameof(OvGpuGroup), nameof(OvVramGroup), nameof(OvRamGroup), nameof(OvPowerGroup),
        nameof(OvRowsDivider), nameof(OvSepCpu), nameof(OvSepGpu), nameof(OvSepVram), nameof(OvSepRam), nameof(OvSepPower),
    };
}
