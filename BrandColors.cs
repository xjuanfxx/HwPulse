using System.Windows.Media;

namespace HwPulse;

/// <summary>
/// Color de cada componente según su fabricante, ajustado para verse bien sobre fondo oscuro:
/// Intel azul, AMD rojo, NVIDIA verde. Si no se reconoce la marca, se usan los colores neutros de siempre.
/// </summary>
public static class BrandColors
{
    public static readonly Color Intel = Color.FromRgb(0x3B, 0x8E, 0xEA);
    public static readonly Color Amd = Color.FromRgb(0xE5, 0x48, 0x4D);
    public static readonly Color Nvidia = Color.FromRgb(0x76, 0xB9, 0x00);
    public static readonly Color DefaultCpu = Color.FromRgb(0x5B, 0x9B, 0xD5);
    public static readonly Color DefaultGpu = Color.FromRgb(0x6C, 0xC2, 0x87);

    public static Color ForCpu(string? name)
    {
        var n = name ?? "";
        if (n.Contains("Intel", StringComparison.OrdinalIgnoreCase)) return Intel;
        if (n.Contains("AMD", StringComparison.OrdinalIgnoreCase) || n.Contains("Ryzen", StringComparison.OrdinalIgnoreCase)) return Amd;
        return DefaultCpu;
    }

    public static Color ForGpu(string? name)
    {
        var n = name ?? "";
        if (n.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || n.Contains("GeForce", StringComparison.OrdinalIgnoreCase)) return Nvidia;
        if (n.Contains("AMD", StringComparison.OrdinalIgnoreCase) || n.Contains("Radeon", StringComparison.OrdinalIgnoreCase)) return Amd;
        if (n.Contains("Intel", StringComparison.OrdinalIgnoreCase) || n.Contains("Arc", StringComparison.OrdinalIgnoreCase)) return Intel;
        return DefaultGpu;
    }

    public static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}
