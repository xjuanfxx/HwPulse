using System.IO;
using System.Text.Json;

namespace HwPulse;

// Los valores numéricos se guardan en settings.json: los nuevos van al final para no cambiar los existentes.
public enum OverlayCorner { TopLeft, TopRight, BottomRight, BottomLeft, TopCenter }

public sealed class AppSettings
{
    private const int CurrentVersion = 2;

    /// <summary>0 = archivo de una versión anterior (antes de existir este campo).</summary>
    public int SettingsVersion { get; set; } = CurrentVersion;

    public bool OverlayVisible { get; set; } = true;
    public OverlayCorner Corner { get; set; } = OverlayCorner.TopCenter;
    public double OverlayOpacity { get; set; } = 0.6;
    public double OverlayScale { get; set; } = 1.0;
    public bool TrayNoticeShown { get; set; }
    public string Language { get; set; } = "en";
    public string OverlayStyle { get; set; } = "compact";
    public int ChartRange { get; set; } = 300;

    /// <summary>Colores del overlay elegidos por el usuario ("#RRGGBB"); lo que falte usa <see cref="DefaultOverlayColors"/>.</summary>
    public Dictionary<string, string> OverlayColors { get; set; } = new();

    public static readonly IReadOnlyDictionary<string, string> DefaultOverlayColors = new Dictionary<string, string>
    {
        ["bg"] = "#000000",
        ["border"] = "#FFFFFF",
        ["text"] = "#FAFAFA",
        ["labels"] = "#A3A3A3",
        ["cpu"] = "#5B9BD5",
        ["gpu"] = "#6CC287",
        ["mem"] = "#9C8BD0",
    };

    public string OverlayColor(string key) =>
        OverlayColors.TryGetValue(key, out var v) ? v : DefaultOverlayColors[key];

    /// <summary>
    /// Sensores de temperatura elegidos para el panel "Temperaturas" (identificadores de LibreHardwareMonitor).
    /// null = los de por defecto (CPU máx., GPU, GPU hotspot y la temperatura principal de cada disco).
    /// </summary>
    public List<string>? TempSensors { get; set; }

    /// <summary>Valores visibles en el overlay (claves de <see cref="OverlayItemKeys"/>).</summary>
    public List<string> OverlayItems { get; set; } = new() { "fps", "frametime", "cpuLoad", "cpuTemp", "gpuLoad", "gpuTemp", "vram", "ram" };

    public static readonly string[] OverlayItemKeys =
        { "fps", "frametime", "cpuLoad", "cpuTemp", "cpuClock", "gpuLoad", "gpuTemp", "vram", "ram", "power" };

    public bool WebEnabled { get; set; } = true;
    public bool WebLan { get; set; }
    public int WebPort { get; set; } = 8086;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HwPulse", "settings.json");

    public static AppSettings Load()
    {
        AppSettings s;
        try
        {
            s = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings()
                : new AppSettings();
        }
        catch { s = new AppSettings(); } // archivo dañado: valores por defecto

        // Migración: el nuevo valor por defecto del overlay (una línea, centrado arriba) también
        // se aplica a quien ya tenía la app instalada.
        if (s.SettingsVersion < 2)
        {
            s.OverlayStyle = "compact";
            s.Corner = OverlayCorner.TopCenter;
        }
        s.SettingsVersion = CurrentVersion;

        // Validación: settings.json está en una carpeta del usuario y lo lee un proceso de administrador.
        // Cualquier valor fuera de rango vuelve al de por defecto en vez de llegar a la red o al firewall.
        if (s.WebPort is < 1024 or > 65535) s.WebPort = 8086;
        s.OverlayOpacity = double.IsFinite(s.OverlayOpacity) ? Math.Clamp(s.OverlayOpacity, 0, 1) : 0.6;
        s.OverlayScale = double.IsFinite(s.OverlayScale) ? Math.Clamp(s.OverlayScale, 0.7, 2) : 1.0;
        if (s.ChartRange is not (60 or 300 or 600)) s.ChartRange = 300;
        if (s.Language is not ("en" or "es")) s.Language = "en";
        if (s.OverlayStyle is not ("panel" or "compact")) s.OverlayStyle = "compact";
        if (!Enum.IsDefined(s.Corner)) s.Corner = OverlayCorner.TopCenter;
        s.OverlayItems = (s.OverlayItems ?? new()).Where(OverlayItemKeys.Contains).Distinct().ToList();
        s.OverlayColors = (s.OverlayColors ?? new())
            .Where(kv => DefaultOverlayColors.ContainsKey(kv.Key) && System.Text.RegularExpressions.Regex.IsMatch(kv.Value ?? "", "^#[0-9A-Fa-f]{6}$"))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        return s;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* no es crítico */ }
    }
}
