using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace HwPulse;

/// <summary>
/// Potencia de GPUs NVIDIA leída directamente de NVML (nvml.dll, viene con el driver). LibreHardwareMonitor
/// la publica como "GPU Package", pero en algunos equipos su lectura de NVML falla sin decir por qué (visto
/// en una RTX 4060: ningún sensor de potencia). Esto es el respaldo: solo se usa si LHM no da potencia.
///
/// Seguridad: el proceso corre como administrador, así que nvml.dll solo se carga desde carpetas que solo
/// el administrador puede escribir (System32, el DriverStore del driver o la carpeta de NVIDIA en Program Files).
/// </summary>
public sealed class NvmlPower : IDisposable
{
    private delegate int InitFn();
    private delegate int ShutdownFn();
    private delegate int CountFn(out uint count);
    private delegate int HandleFn(uint index, out IntPtr device);
    private delegate int NameFn(IntPtr device, byte[] name, uint length);
    private delegate int PowerFn(IntPtr device, out uint milliwatts);

    private IntPtr _lib;
    private ShutdownFn? _shutdown;
    private PowerFn? _power;
    private readonly Dictionary<string, IntPtr> _byName = new(StringComparer.OrdinalIgnoreCase);
    private IntPtr _first;
    private bool _tried, _failed, _powerErrorLogged;

    /// <summary>Vatios que consume la GPU con ese nombre (o la primera NVIDIA), o null si NVML no está.</summary>
    public double? ReadWatts(string gpuName)
    {
        if (!_tried) { _tried = true; _failed = !Init(); }
        if (_failed || _power == null) return null;
        var dev = _byName.TryGetValue(gpuName, out var d) ? d : _first;
        if (dev == IntPtr.Zero) return null;
        int rc = _power(dev, out uint mw);
        if (rc != 0)
        {
            // NVML_ERROR_NOT_SUPPORTED (3) y similares: la tarjeta no informa potencia. Se anota una vez.
            if (!_powerErrorLogged) { _powerErrorLogged = true; ErrorLog.Info("NVML", $"nvmlDeviceGetPowerUsage failed for '{gpuName}': code {rc}"); }
            return null;
        }
        return mw / 1000.0;
    }

    private bool Init()
    {
        string? path = FindNvml();
        if (path == null) { ErrorLog.Info("NVML", "nvml.dll not found in System32, DriverStore or NVSMI: no GPU power fallback"); return false; }
        try
        {
            _lib = NativeLibrary.Load(path);
            var init = Fn<InitFn>("nvmlInit_v2");
            _shutdown = Fn<ShutdownFn>("nvmlShutdown");
            var count = Fn<CountFn>("nvmlDeviceGetCount_v2");
            var handle = Fn<HandleFn>("nvmlDeviceGetHandleByIndex_v2");
            var name = Fn<NameFn>("nvmlDeviceGetName");
            _power = Fn<PowerFn>("nvmlDeviceGetPowerUsage");

            int rc = init();
            if (rc != 0) { ErrorLog.Info("NVML", $"nvmlInit_v2 failed: code {rc} ({path})"); _shutdown = null; return false; }
            if (count(out uint n) != 0) return false;
            var buf = new byte[96];
            for (uint i = 0; i < n; i++)
            {
                if (handle(i, out var dev) != 0) continue;
                if (_first == IntPtr.Zero) _first = dev;
                if (name(dev, buf, (uint)buf.Length) == 0)
                {
                    int len = Array.IndexOf(buf, (byte)0);
                    string devName = Encoding.ASCII.GetString(buf, 0, len < 0 ? buf.Length : len);
                    _byName.TryAdd(devName, dev);
                    // LHM antepone "NVIDIA " al nombre; NVML a veces no.
                    _byName.TryAdd(devName.StartsWith("NVIDIA ", StringComparison.OrdinalIgnoreCase) ? devName : "NVIDIA " + devName, dev);
                }
            }
            return _first != IntPtr.Zero;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("NVML", ex);
            return false;
        }
    }

    private T Fn<T>(string export) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_lib, export));

    /// <summary>Busca nvml.dll solo en ubicaciones protegidas (escribibles únicamente por administradores).</summary>
    private static string? FindNvml()
    {
        string sys = Environment.SystemDirectory;
        string sys32 = Path.Combine(sys, "nvml.dll");
        if (File.Exists(sys32)) return sys32;
        try
        {
            // Drivers DCH: nvml.dll vive en el DriverStore del paquete del driver (nv_dispi*/nvd*).
            string repo = Path.Combine(sys, "DriverStore", "FileRepository");
            var found = Directory.EnumerateDirectories(repo, "nv*")
                .Select(d => Path.Combine(d, "nvml.dll"))
                .Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc) // el driver más reciente
                .FirstOrDefault();
            if (found != null) return found;
        }
        catch { }
        string smi = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvml.dll");
        return File.Exists(smi) ? smi : null;
    }

    public void Dispose()
    {
        try { _shutdown?.Invoke(); } catch { }
        if (_lib != IntPtr.Zero) { try { NativeLibrary.Free(_lib); } catch { } _lib = IntPtr.Zero; }
    }
}
