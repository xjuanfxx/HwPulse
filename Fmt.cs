using System.Globalization;
using System.Windows.Media;
using LibreHardwareMonitor.Hardware;

namespace HwPulse;

/// <summary>Formato de valores y colores por umbral.</summary>
public static class Fmt
{
    private static readonly CultureInfo C = CultureInfo.InvariantCulture;
    public const string None = "—";

    public static readonly Brush Ok = Freeze(Color.FromRgb(0x6C, 0xC2, 0x87));
    public static readonly Brush Warn = Freeze(Color.FromRgb(0xE0, 0xA8, 0x4A));
    public static readonly Brush Hot = Freeze(Color.FromRgb(0xE5, 0x58, 0x4F));
    public static readonly Brush Neutral = Freeze(Color.FromRgb(0xD9, 0xDC, 0xE1));
    public static readonly Brush Info = Freeze(Color.FromRgb(0x5B, 0x9B, 0xD5));
    public static readonly Brush Faint = Freeze(Color.FromRgb(0x50, 0x56, 0x5E));

    public static Brush Freeze(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    /// <summary>Color por temperatura (umbrales pensados para laptop).</summary>
    public static Brush TempBrush(double? t) => t switch
    {
        null => Neutral,
        >= 90 => Hot,
        >= 78 => Warn,
        _ => Ok,
    };

    public static string Temp(double? t) => t is double v ? $"{v:F0} °C" : None;
    public static string TempShort(double? t) => t is double v ? $"{v:F0}°" : None;
    public static string Pct(double? p) => p is double v ? $"{v:F0} %" : None;
    public static string Watts(double? w) => w is double v ? $"{v.ToString("F1", C)} W" : None;
    public static string Volts(double? v) => v is double x ? $"{x.ToString("F3", C)} V" : None;
    public static string Mhz(double? m) => m is double v ? $"{v:F0} MHz" : None;
    public static string Ghz(double? m) => m is double v ? $"{(v / 1000).ToString("F2", C)} GHz" : None;
    public static string Gb(double? g) => g is double v ? $"{v.ToString("F1", C)} GB" : None;

    public static string Rate(double? bps)
    {
        if (bps is not double b) return None;
        if (b >= 1024 * 1024) return $"{(b / 1024 / 1024).ToString("F1", C)} MB/s";
        if (b >= 1024) return $"{b / 1024:F0} KB/s";
        return $"{b:F0} B/s";
    }

    public static string MinMax(MinMax m, Func<double?, string> f) =>
        m.HasValue ? Loc.F("MinMax", f(m.Min), f(m.Max)) : " ";

    public static string Sensor(SensorType t, double? v)
    {
        if (v is not double x) return None;
        return t switch
        {
            SensorType.Voltage => $"{x.ToString("F3", C)} V",
            SensorType.Current => $"{x.ToString("F2", C)} A",
            SensorType.Power => $"{x.ToString("F1", C)} W",
            SensorType.Clock => $"{x:F0} MHz",
            SensorType.Temperature => $"{x.ToString("F1", C)} °C",
            SensorType.Load or SensorType.Control or SensorType.Level => $"{x.ToString("F1", C)} %",
            SensorType.Frequency => $"{x:F0} Hz",
            SensorType.Fan => $"{x:F0} RPM",
            SensorType.Flow => $"{x:F0} L/h",
            SensorType.Data => $"{x.ToString("F2", C)} GB",
            SensorType.SmallData => $"{x:F0} MB",
            SensorType.Throughput => Rate(x),
            SensorType.TimeSpan => TimeSpan.FromSeconds(x).ToString(@"hh\:mm\:ss"),
            SensorType.Energy => $"{x:F0} mWh",
            _ => x.ToString("F2", C),
        };
    }

    public static string TypeName(SensorType t)
    {
        var s = Loc.T("Type." + t);
        return s.StartsWith("Type.") ? t.ToString() : s;
    }
}

/// <summary>Mínimo y máximo de la sesión.</summary>
public sealed class MinMax
{
    public double? Min { get; private set; }
    public double? Max { get; private set; }
    public bool HasValue => Min.HasValue;

    public void Add(double? v)
    {
        if (v is not double x) return;
        if (Min is null || x < Min) Min = x;
        if (Max is null || x > Max) Max = x;
    }
}

/// <summary>Historial de tamaño fijo para las gráficas.</summary>
public sealed class RingBuffer
{
    private readonly double[] _data;
    private int _start, _count;

    public RingBuffer(int capacity) { _data = new double[capacity]; }
    public int Capacity => _data.Length;

    public void Add(double? v)
    {
        if (v is not double x) return;
        if (_count < _data.Length) { _data[(_start + _count) % _data.Length] = x; _count++; }
        else { _data[_start] = x; _start = (_start + 1) % _data.Length; }
    }

    public double[] ToArray()
    {
        var r = new double[_count];
        for (int i = 0; i < _count; i++) r[i] = _data[(_start + i) % _data.Length];
        return r;
    }
}
