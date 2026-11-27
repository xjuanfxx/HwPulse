using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace HwPulse;

/// <summary>Un dato del overlay personalizado (FPS, temperatura del CPU…) con su propio estilo.</summary>
public sealed class OverlayElement
{
    public string Key { get; set; } = "fps";
    public bool Visible { get; set; } = true;
    /// <summary>Texto antes del valor ("FPS", "CPU"…). Vacío = sin etiqueta.</summary>
    public string Label { get; set; } = "";
    /// <summary>Color del valor "#RRGGBB", o "auto" (temperaturas y FPS cambian de color según el valor).</summary>
    public string ValueColor { get; set; } = "auto";
    public string LabelColor { get; set; } = "#A3A3A3";
    /// <summary>Tamaño de letra del valor; la etiqueta usa ~75 % de este tamaño.</summary>
    public double Size { get; set; } = 13;
    public bool Bold { get; set; } = true;
    /// <summary>Barra de progreso junto al valor (solo para los datos en %).</summary>
    public bool Bar { get; set; }
    /// <summary>Mini gráfica con el historial reciente (solo para los datos que tienen historial).</summary>
    public bool Graph { get; set; }

    public OverlayElement Clone() => (OverlayElement)MemberwiseClone();
}

/// <summary>
/// Diseño completo del overlay personalizado: qué datos, en qué orden y cómo se ven. Se guarda en
/// settings.json y se puede exportar/importar como archivo .json para compartir diseños.
/// </summary>
public sealed class OverlayLayout
{
    public int Version { get; set; } = 1;
    /// <summary>"horizontal" (una línea), "vertical" (una columna) o "grid" (rejilla de N columnas).</summary>
    public string Arrangement { get; set; } = "horizontal";
    public int Columns { get; set; } = 2;
    public string Font { get; set; } = "Inter";
    public string Background { get; set; } = "#000000";
    public double BackgroundOpacity { get; set; } = 0.6;
    public string Border { get; set; } = "#FFFFFF";
    public double BorderOpacity { get; set; } = 0.12;
    public double BorderWidth { get; set; } = 1;
    public double CornerRadius { get; set; } = 6;
    public double PaddingX { get; set; } = 10;
    public double PaddingY { get; set; } = 5;
    public double Spacing { get; set; } = 10;
    /// <summary>Texto entre datos en modo horizontal ("|", "·", "/"…). Vacío = sin separador.</summary>
    public string Separator { get; set; } = "|";
    public string SeparatorColor { get; set; } = "#555555";
    public List<OverlayElement> Elements { get; set; } = new();

    // ------------------------------------------------------------------ catálogo de datos
    /// <summary>Datos disponibles: clave → (propiedad del ViewModel con el texto, propiedad con el color
    /// automático o null, propiedad con el % para la barra o null, etiqueta por defecto).</summary>
    public static readonly IReadOnlyDictionary<string, (string text, string? autoBrush, string? pct, string label)> Catalog =
        new Dictionary<string, (string, string?, string?, string)>
        {
            ["fps"] = ("FpsText", "FpsBrush", null, "FPS"),
            ["frametime"] = ("FrametimeText", null, null, ""),
            ["cpuLoad"] = ("CpuLoadText", null, "CpuLoadPct", "CPU"),
            ["cpuTemp"] = ("CpuTempShort", "CpuTempBrush", null, ""),
            ["cpuClock"] = ("CpuClockShort", null, null, ""),
            ["cpuPower"] = ("CpuPowerText", null, null, "CPU W"),
            ["gpuLoad"] = ("GpuLoadText", null, "GpuLoadPct", "GPU"),
            ["gpuTemp"] = ("GpuTempShort", "GpuTempBrush", null, ""),
            ["gpuClock"] = ("GpuCoreClockText", null, null, ""),
            ["gpuPower"] = ("GpuPowerText", null, null, "GPU W"),
            ["vram"] = ("GpuVramShort", null, "VramPct", "VRAM"),
            ["ram"] = ("RamShort", null, "RamPct", "RAM"),
            ["power"] = ("TotalPowerText", null, null, "PWR"),
            ["time"] = ("ClockTimeText", null, null, ""),
        };

    /// <summary>Datos con historial para la mini gráfica: clave → (serie del ViewModel, máximo fijo o NaN = automático).</summary>
    public static readonly IReadOnlyDictionary<string, (string series, double max)> Series =
        new Dictionary<string, (string, double)>
        {
            ["fps"] = ("FpsSeries", double.NaN),
            ["cpuLoad"] = ("CpuLoadSeries", 100),
            ["cpuTemp"] = ("CpuTempSeries", double.NaN),
            ["cpuClock"] = ("CpuClockSeries", double.NaN),
            ["gpuLoad"] = ("GpuLoadSeries", 100),
            ["gpuTemp"] = ("GpuTempSeries", double.NaN),
            ["ram"] = ("RamSeries", 100),
        };

    public static readonly string[] Fonts = { "Inter", "Segoe UI", "Segoe UI Variable Display", "Bahnschrift", "Consolas", "Cascadia Mono", "Arial", "Verdana" };

    public static OverlayElement El(string key, string? label = null, double size = 13, bool bold = true, string color = "auto", bool bar = false, bool visible = true) =>
        new() { Key = key, Label = label ?? Catalog[key].label, Size = size, Bold = bold, ValueColor = color, Bar = bar, Visible = visible };

    /// <summary>Todos los datos: los del diseño en su orden, y al final los que falten (ocultos).</summary>
    public void EnsureAllElements()
    {
        foreach (var key in Catalog.Keys)
            if (!Elements.Any(e => e.Key == key)) Elements.Add(El(key, visible: false));
    }

    // ------------------------------------------------------------------ presets
    public static readonly string[] PresetKeys = { "line", "minimal", "vertical", "bigfps", "grid" };

    public static OverlayLayout Preset(string key)
    {
        var l = key switch
        {
            "minimal" => new OverlayLayout
            {
                Separator = "", Spacing = 12, BackgroundOpacity = 0, BorderWidth = 0, PaddingX = 4, PaddingY = 2,
                Elements = { El("fps", "", 15), El("cpuTemp", "CPU", 13), El("gpuTemp", "GPU", 13) },
            },
            "vertical" => new OverlayLayout
            {
                Arrangement = "vertical", Separator = "", Spacing = 3, PaddingX = 12, PaddingY = 9, CornerRadius = 8,
                Elements = { El("fps", "FPS", 18), El("frametime", "MS", 12), El("cpuLoad", "CPU", 13, bar: true), El("cpuTemp", "CPU °", 13),
                             El("gpuLoad", "GPU", 13, bar: true), El("gpuTemp", "GPU °", 13), El("vram", "VRAM", 13, bar: true), El("ram", "RAM", 13, bar: true) },
            },
            "bigfps" => new OverlayLayout
            {
                Arrangement = "vertical", Separator = "", Spacing = 2, PaddingX = 12, PaddingY = 8, CornerRadius = 10,
                Elements = { El("fps", "", 34), El("frametime", "", 13, bold: false, color: "#A3A3A3") },
            },
            "grid" => new OverlayLayout
            {
                Arrangement = "grid", Columns = 2, Separator = "", Spacing = 8, PaddingX = 12, PaddingY = 9, CornerRadius = 8,
                Elements = { El("fps", "FPS", 15), El("frametime", "MS", 13), El("cpuLoad", "CPU", 13), El("cpuTemp", "CPU °", 13),
                             El("gpuLoad", "GPU", 13), El("gpuTemp", "GPU °", 13), El("ram", "RAM", 13), El("power", "PWR", 13) },
            },
            _ => new OverlayLayout // "line": como el estilo de una línea de siempre
            {
                Elements = { El("fps", "FPS", 13), El("frametime", "", 13, bold: false, color: "#A3A3A3"), El("cpuLoad", "CPU"), El("cpuTemp", ""),
                             El("gpuLoad", "GPU"), El("gpuTemp", ""), El("vram", "VRAM"), El("ram", "RAM") },
            },
        };
        l.EnsureAllElements();
        return l;
    }

    // ------------------------------------------------------------------ copia, JSON y validación
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    public OverlayLayout Clone()
    {
        var c = (OverlayLayout)MemberwiseClone();
        c.Elements = Elements.Select(e => e.Clone()).ToList();
        return c;
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Lee un diseño exportado. Devuelve null si no es un diseño válido.</summary>
    public static OverlayLayout? FromJson(string json)
    {
        try
        {
            var l = JsonSerializer.Deserialize<OverlayLayout>(json);
            if (l == null || l.Elements == null || l.Elements.Count == 0) return null;
            l.Normalize();
            return l;
        }
        catch { return null; }
    }

    private static readonly Regex Hex = new("^#[0-9A-Fa-f]{6}$", RegexOptions.Compiled);
    private static string Col(string? v, string fallback) => v != null && Hex.IsMatch(v) ? v : fallback;
    private static double Clamp(double v, double min, double max, double fallback) => double.IsFinite(v) ? Math.Clamp(v, min, max) : fallback;
    private static string Text(string? v, int max) => new string((v ?? "").Where(ch => !char.IsControl(ch)).Take(max).ToArray());

    /// <summary>
    /// Deja el diseño dentro de límites seguros: lo lee un proceso de administrador desde settings.json o desde
    /// un archivo que alguien compartió, así que nada fuera de rango ni ningún dato desconocido llega a la UI.
    /// </summary>
    public void Normalize()
    {
        Arrangement = Arrangement is "horizontal" or "vertical" or "grid" ? Arrangement : "horizontal";
        Columns = Math.Clamp(Columns, 1, 6);
        Font = Fonts.Contains(Font) ? Font : "Inter";
        Background = Col(Background, "#000000");
        BackgroundOpacity = Clamp(BackgroundOpacity, 0, 1, 0.6);
        Border = Col(Border, "#FFFFFF");
        BorderOpacity = Clamp(BorderOpacity, 0, 1, 0.12);
        BorderWidth = Clamp(BorderWidth, 0, 4, 1);
        CornerRadius = Clamp(CornerRadius, 0, 24, 6);
        PaddingX = Clamp(PaddingX, 0, 40, 10);
        PaddingY = Clamp(PaddingY, 0, 40, 5);
        Spacing = Clamp(Spacing, 0, 40, 10);
        Separator = Text(Separator, 3);
        SeparatorColor = Col(SeparatorColor, "#555555");
        var seen = new HashSet<string>();
        Elements = (Elements ?? new())
            .Where(e => e != null && Catalog.ContainsKey(e.Key) && seen.Add(e.Key))
            .Select(e =>
            {
                e.Label = Text(e.Label, 12);
                e.ValueColor = e.ValueColor == "auto" ? "auto" : Col(e.ValueColor, "#FAFAFA");
                e.LabelColor = Col(e.LabelColor, "#A3A3A3");
                e.Size = Clamp(e.Size, 8, 48, 13);
                e.Bar = e.Bar && Catalog[e.Key].pct != null;
                e.Graph = e.Graph && Series.ContainsKey(e.Key);
                return e;
            }).ToList();
        EnsureAllElements();
    }
}
