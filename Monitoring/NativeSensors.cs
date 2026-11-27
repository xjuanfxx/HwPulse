using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HwPulse;

/// <summary>
/// Respaldos que vienen con Windows: sin driver, sin administrador y sin tocar ningún juego.
/// Se usan solo cuando LibreHardwareMonitor no da el valor (PawnIO bloqueado por un anti-cheat, sin admin,
/// un CPU o una GPU que la librería aún no reconoce).
/// - Reloj real del CPU: contadores "Processor Information" (% Processor Performance × Processor Frequency),
///   la misma fuente que el Administrador de tareas.
/// - GPU (cualquier marca con driver WDDM 2.4+): temperatura, ventilador y potencia en % del máximo, de
///   D3DKMTQueryAdapterInfo(KMTQAITYPE_ADAPTERPERFDATA), lo que muestra el Administrador de tareas.
/// </summary>
public sealed class NativeSensors : IDisposable
{
    // ------------------------------------------------------------------ CPU
    private PerformanceCounter? _perf, _freq;
    private bool _cpuFailed;

    /// <summary>Reloj medio actual del CPU en MHz, o null si los contadores no están disponibles.</summary>
    public double? CpuClockMhz()
    {
        if (_cpuFailed) return null;
        try
        {
            if (_perf == null)
            {
                _perf = new PerformanceCounter("Processor Information", "% Processor Performance", "_Total", readOnly: true);
                _freq = new PerformanceCounter("Processor Information", "Processor Frequency", "_Total", readOnly: true);
                _perf.NextValue(); // los contadores de porcentaje necesitan una primera muestra de referencia
                return null;
            }
            double perf = _perf.NextValue(), baseMhz = _freq!.NextValue();
            return perf > 0 && baseMhz > 0 ? baseMhz * perf / 100.0 : null;
        }
        catch (Exception ex)
        {
            _cpuFailed = true;
            ErrorLog.Write("NativeSensors CPU", ex);
            return null;
        }
    }

    // ------------------------------------------------------------------ GPU (D3DKMT)
    private const int KmtqaiAdapterRegistryInfo = 8;
    private const int KmtqaiAdapterPerfData = 62;

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint Low; public int High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_ADAPTERINFO { public uint hAdapter; public LUID AdapterLuid; public uint NumOfSources; public int bPrecisePresentRegionsPreferred; }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_ENUMADAPTERS2 { public uint NumAdapters; public IntPtr pAdapters; }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_QUERYADAPTERINFO { public uint hAdapter; public int Type; public IntPtr pPrivateDriverData; public uint PrivateDriverDataSize; }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_CLOSEADAPTER { public uint hAdapter; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct D3DKMT_ADAPTERREGISTRYINFO
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string AdapterString;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string BiosString;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DacType;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ChipType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_ADAPTER_PERFDATA
    {
        public uint PhysicalAdapterIndex;
        public ulong MemoryFrequency, MaxMemoryFrequency, MaxMemoryFrequencyOC, MemoryBandwidth, PCIEBandwidth;
        public uint FanRPM;
        public uint Power;        // décimas de % del máximo (1 = 0.1 %)
        public uint Temperature;  // décimas de °C (1 = 0.1 °C)
        public byte PowerStateOverride;
    }

    [DllImport("gdi32.dll")] private static extern int D3DKMTEnumAdapters2(ref D3DKMT_ENUMADAPTERS2 args);
    [DllImport("gdi32.dll")] private static extern int D3DKMTQueryAdapterInfo(ref D3DKMT_QUERYADAPTERINFO args);
    [DllImport("gdi32.dll")] private static extern int D3DKMTCloseAdapter(ref D3DKMT_CLOSEADAPTER args);

    public sealed record GpuPerf(string Name, double? TempC, double? FanRpm, double? PowerPct);

    private List<(uint handle, string name)>? _adapters;
    private bool _gpuFailed;

    /// <summary>Datos de la GPU cuyo nombre coincide (o contiene) <paramref name="gpuName"/>; null si no hay.</summary>
    public GpuPerf? Gpu(string gpuName)
    {
        foreach (var g in AllGpus())
            if (g.Name.Equals(gpuName, StringComparison.OrdinalIgnoreCase)
                || gpuName.Contains(g.Name, StringComparison.OrdinalIgnoreCase)
                || g.Name.Contains(gpuName, StringComparison.OrdinalIgnoreCase))
                return g;
        return null;
    }

    /// <summary>Todas las GPU que informan datos de rendimiento (para el diagnóstico).</summary>
    public List<GpuPerf> AllGpus()
    {
        var result = new List<GpuPerf>();
        if (_gpuFailed) return result;
        try
        {
            _adapters ??= OpenAdapters();
            foreach (var (h, name) in _adapters)
            {
                if (!TryQuery(h, KmtqaiAdapterPerfData, out D3DKMT_ADAPTER_PERFDATA p)) continue;
                // Valores que el driver no implementa llegan a 0: se tratan como "no disponible".
                double? temp = p.Temperature is > 0 and < 1500 ? p.Temperature / 10.0 : null;
                double? fan = p.FanRPM is > 0 and < 20000 ? p.FanRPM : null;
                double? power = p.Power is > 0 and <= 1500 ? p.Power / 10.0 : null;
                result.Add(new GpuPerf(name, temp, fan, power));
            }
        }
        catch (Exception ex)
        {
            _gpuFailed = true;
            ErrorLog.Write("NativeSensors GPU", ex);
        }
        return result;
    }

    private static List<(uint, string)> OpenAdapters()
    {
        var list = new List<(uint, string)>();
        var e = new D3DKMT_ENUMADAPTERS2();
        if (D3DKMTEnumAdapters2(ref e) != 0 || e.NumAdapters == 0) return list; // primera llamada: cuántos hay
        int size = Marshal.SizeOf<D3DKMT_ADAPTERINFO>();
        e.pAdapters = Marshal.AllocHGlobal(size * (int)e.NumAdapters);
        try
        {
            if (D3DKMTEnumAdapters2(ref e) != 0) return list;
            var seen = new HashSet<string>();
            for (int i = 0; i < e.NumAdapters; i++)
            {
                var info = Marshal.PtrToStructure<D3DKMT_ADAPTERINFO>(e.pAdapters + i * size);
                string name = TryQuery(info.hAdapter, KmtqaiAdapterRegistryInfo, out D3DKMT_ADAPTERREGISTRYINFO reg)
                    ? reg.AdapterString?.Trim() ?? "" : "";
                // Adaptadores sin nombre (Microsoft Basic Render) o repetidos por cada salida de vídeo: fuera.
                if (name.Length == 0 || name.Contains("Microsoft Basic", StringComparison.OrdinalIgnoreCase) || !seen.Add(name))
                {
                    var c = new D3DKMT_CLOSEADAPTER { hAdapter = info.hAdapter };
                    D3DKMTCloseAdapter(ref c);
                    continue;
                }
                list.Add((info.hAdapter, name));
            }
        }
        finally { Marshal.FreeHGlobal(e.pAdapters); }
        return list;
    }

    private static bool TryQuery<T>(uint adapter, int type, out T value) where T : struct
    {
        value = default;
        int size = Marshal.SizeOf<T>();
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(value, buf, false);
            var q = new D3DKMT_QUERYADAPTERINFO { hAdapter = adapter, Type = type, pPrivateDriverData = buf, PrivateDriverDataSize = (uint)size };
            if (D3DKMTQueryAdapterInfo(ref q) != 0) return false; // STATUS_SUCCESS = 0
            value = Marshal.PtrToStructure<T>(buf);
            return true;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    public void Dispose()
    {
        _perf?.Dispose();
        _freq?.Dispose();
        if (_adapters != null)
            foreach (var (h, _) in _adapters) { var c = new D3DKMT_CLOSEADAPTER { hAdapter = h }; try { D3DKMTCloseAdapter(ref c); } catch { } }
        _adapters = null;
    }
}
