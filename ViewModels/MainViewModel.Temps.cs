using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows.Media;
using LibreHardwareMonitor.Hardware;

namespace HwPulse;

/// <summary>Una casilla de la lista "Temperaturas" en Ajustes.</summary>
public sealed class TempChoice : Observable
{
    private readonly Action<TempChoice> _changed;
    private bool _checked;

    public TempChoice(string key, Action<TempChoice> changed) { Key = key; _changed = changed; }

    public string Key { get; }
    public string Label { get; set; } = "";
    /// <summary>Nombre corto para el panel del Resumen (la columna es estrecha).</summary>
    public string ShortLabel { get; set; } = "";
    public string GroupLabel { get; set; } = "";
    public int GroupOrder { get; set; }
    public int Order { get; set; }
    public bool DefaultOn { get; set; }
    public Brush Bar { get; set; } = Fmt.Neutral;
    public double? Value { get; set; }
    public string ValueText => Fmt.Temp(Value);

    public bool IsChecked
    {
        get => _checked;
        set { if (_checked == value) return; _checked = value; Raise(); _changed(this); }
    }

    /// <summary>Marca sin avisar (al cargar o restaurar).</summary>
    public void SetChecked(bool v) { _checked = v; Raise(nameof(IsChecked)); }
    public void Refresh() => RaiseAll();
}

/// <summary>Qué sensores de temperatura se muestran en el Resumen, elegidos por el usuario.</summary>
public sealed partial class MainViewModel
{
    private static readonly Regex CoreN = new(@"^CPU Core #(\d+)$", RegexOptions.Compiled);
    private readonly Dictionary<string, TempChoice> _tempChoices = new();

    public ObservableCollection<TempChoice> TempChoices { get; } = new();
    public System.Windows.Visibility TempChoicesEmptyVisibility =>
        TempChoices.Count == 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    public Action? TempSelectionChanged;

    private void UpdateTempChoices(Snapshot s)
    {
        foreach (var r in s.AllSensors)
        {
            if (r.Type != SensorType.Temperature || r.Name.Contains("Distance", StringComparison.OrdinalIgnoreCase)) continue;
            if (!_tempChoices.TryGetValue(r.Key, out var c))
            {
                c = new TempChoice(r.Key, OnTempChoiceChanged);
                Describe(c, r, s);
                c.SetChecked(_settings.TempSensors?.Contains(r.Key) ?? c.DefaultOn);
                _tempChoices[r.Key] = c;
                // Insertar ordenado: grupo (CPU, GPU, discos, otros) y dentro, lo más útil primero.
                int i = 0;
                while (i < TempChoices.Count && Compare(TempChoices[i], c) <= 0) i++;
                TempChoices.Insert(i, c);
            }
            else Describe(c, r, s); // vuelve a calcular el nombre (por si cambió el idioma)
            c.Value = r.Value;
            c.Refresh();
        }
    }

    private static int Compare(TempChoice a, TempChoice b) =>
        a.GroupOrder != b.GroupOrder ? a.GroupOrder.CompareTo(b.GroupOrder)
        : a.Order != b.Order ? a.Order.CompareTo(b.Order)
        : string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase);

    /// <summary>Nombre claro, grupo, orden y si viene marcado por defecto.</summary>
    private static void Describe(TempChoice c, SensorReading r, Snapshot s)
    {
        int cut = r.Key.IndexOf("/temperature", StringComparison.Ordinal);
        string hwKey = cut > 0 ? r.Key[..cut] : r.Key;
        var disk = s.Disks.FirstOrDefault(d => d.Key == hwKey);

        if (r.Hardware == s.CpuName)
        {
            c.GroupLabel = "CPU"; c.GroupOrder = 0; c.Bar = BarCpu;
            (c.Label, c.ShortLabel, c.Order, c.DefaultOn) = r.Name switch
            {
                "Core Max" => (Loc.T("TempCpuMax"), "CPU", 0, true),
                "CPU Package" => (Loc.T("TempCpuPkg"), Loc.T("ShortCpuPkg"), 1, false),
                "Core Average" => (Loc.T("TempCpuAvg"), Loc.T("ShortCpuAvg"), 2, false),
                _ when CoreN.Match(r.Name) is { Success: true } m => (Loc.F("TempCpuCore", m.Groups[1].Value), Loc.F("ShortCore", m.Groups[1].Value),
                                                                      10 + int.Parse(m.Groups[1].Value), false),
                _ => ($"CPU {r.Name}", $"CPU {r.Name}", 50, false),
            };
        }
        else if (r.Hardware == s.GpuName)
        {
            c.GroupLabel = "GPU"; c.GroupOrder = 1; c.Bar = BarGpu;
            (c.Label, c.ShortLabel, c.Order, c.DefaultOn) = r.Name switch
            {
                "GPU Core" => ("GPU", "GPU", 0, true),
                "GPU Hot Spot" => (Loc.T("GpuHotSpot"), Loc.T("ShortGpuHot"), 1, true),
                "GPU Memory Junction" => (Loc.T("TempGpuMem"), Loc.T("ShortGpuMem"), 2, false),
                _ => ($"GPU {r.Name}", $"GPU {r.Name}", 10, false),
            };
        }
        else if (disk != null)
        {
            c.GroupLabel = Loc.T("Storage"); c.GroupOrder = 2; c.Bar = BarDisk;
            string name = string.IsNullOrEmpty(disk.Short) ? disk.Name : disk.Short;
            // La principal es el primer sensor de temperatura del disco ("Temperature" en SATA,
            // "Composite Temperature" en NVMe); las demás llevan un sufijo corto (T2, T3…).
            var first = s.AllSensors.FirstOrDefault(x => x.Type == SensorType.Temperature && x.Key.StartsWith(hwKey + "/", StringComparison.Ordinal));
            bool main = first?.Key == r.Key;
            string suffix = r.Name.Replace("Composite", "").Replace("Temperature", "T").Replace(" ", "");
            c.Label = main ? $"{name} ({disk.Name})" : $"{name} · {suffix}";
            c.ShortLabel = main ? name : $"{name} {suffix}";
            c.Order = main ? 0 : 1;
            c.DefaultOn = main;
        }
        else
        {
            c.GroupLabel = Loc.T("OtherSensors"); c.GroupOrder = 3; c.Bar = BarDisk;
            c.Label = $"{r.Hardware} · {r.Name}";
            c.ShortLabel = r.Name;
            c.Order = 0;
            c.DefaultOn = false;
        }
    }

    private void OnTempChoiceChanged(TempChoice _)
    {
        _settings.TempSensors = TempChoices.Where(x => x.IsChecked).Select(x => x.Key).ToList();
        TempSelectionChanged?.Invoke();
        Rerender();
    }

    public void ResetTempChoices()
    {
        _settings.TempSensors = null;
        foreach (var c in TempChoices) c.SetChecked(c.DefaultOn);
        TempSelectionChanged?.Invoke();
        Rerender();
    }

    /// <summary>Filas del panel "Temperaturas" con los sensores marcados, en el orden de la lista.</summary>
    private List<BarRow> SelectedTempRows() =>
        TempChoices.Where(c => c.IsChecked && c.Value is not null)
                   .Select(c => new BarRow(c.ShortLabel, Fmt.Temp(c.Value), Math.Clamp(c.Value!.Value / 105 * 100, 0, 100), c.Bar))
                   .ToList();
}
