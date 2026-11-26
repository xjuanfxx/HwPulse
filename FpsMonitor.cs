using System.Diagnostics;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace HwPulse;

/// <summary>
/// FPS y frametime del juego en primer plano, leyendo los eventos "Present" que Windows publica por ETW
/// (la misma fuente que usan PresentMon, CapFrameX o FrameView). Solo escucha: no se inyecta en el juego,
/// así que no interfiere con los anti-cheat. Requiere administrador.
/// Cubre DirectX 9–12 (DXGI y D3D9). Juegos Vulkan/OpenGL puros no publican estos eventos.
/// </summary>
public sealed class FpsMonitor : IDisposable
{
    private static readonly Guid Dxgi = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    private static readonly Guid D3D9 = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");
    private const int DxgiPresentStart = 42, DxgiPresentMpoStart = 55, D3D9PresentStart = 1;

    private readonly TraceEventSession _session;
    private readonly object _lock = new();
    private readonly Dictionary<int, List<double>> _presents = new();
    private readonly Dictionary<int, string> _names = new();

    // Red por proceso: Microsoft-Windows-Kernel-Network publica cada envío/recepción TCP y UDP con el PID
    // y el tamaño. Envío: 10/26 (TCP v4/v6), 42/58 (UDP); recepción: 11/27 (TCP), 43/59 (UDP).
    private static readonly Guid KernelNetwork = new("7DD42A49-5329-4832-8DFD-43D979153A88");
    private static readonly HashSet<int> NetSend = new() { 10, 26, 42, 58 };
    private static readonly HashSet<int> NetRecv = new() { 11, 27, 43, 59 };
    private readonly Dictionary<int, (long down, long up)> _net = new();
    private DateTime _netSince = DateTime.UtcNow;

    private FpsMonitor()
    {
        _session = new TraceEventSession("HwPulse-FPS") { StopOnDispose = true };
        _session.EnableProvider(Dxgi, TraceEventLevel.Verbose);
        _session.EnableProvider(D3D9, TraceEventLevel.Verbose);
        _session.EnableProvider(KernelNetwork, TraceEventLevel.Informational);
        _session.Source.AllEvents += OnEvent;
        _session.Source.Dynamic.All += OnNetwork; // el parser dinámico da acceso a los campos PID y size
        new Thread(() => { try { _session.Source.Process(); } catch { } }) { IsBackground = true, Name = "HwPulse ETW" }.Start();
    }

    private void OnNetwork(TraceEvent e)
    {
        if (e.ProviderGuid != KernelNetwork) return;
        int id = (int)e.ID;
        bool send = NetSend.Contains(id), recv = NetRecv.Contains(id);
        if (!send && !recv) return;
        try
        {
            // El PID va en los datos del evento: las recepciones suelen llegar desde el contexto del sistema.
            int pid = Convert.ToInt32(e.PayloadByName("PID"));
            long size = Convert.ToInt64(e.PayloadByName("size"));
            lock (_lock)
            {
                var cur = _net.GetValueOrDefault(pid);
                _net[pid] = send ? (cur.down, cur.up + size) : (cur.down + size, cur.up);
            }
        }
        catch { /* evento con otro formato: se ignora */ }
    }

    /// <summary>Bytes por segundo recibidos y enviados por cada proceso desde la última llamada.</summary>
    public Dictionary<int, (double down, double up)> SampleNetwork()
    {
        Dictionary<int, (long down, long up)> snap;
        double secs;
        lock (_lock)
        {
            snap = new Dictionary<int, (long, long)>(_net);
            _net.Clear();
            var now = DateTime.UtcNow;
            secs = Math.Max(0.2, (now - _netSince).TotalSeconds);
            _netSince = now;
        }
        return snap.ToDictionary(kv => kv.Key, kv => (kv.Value.down / secs, kv.Value.up / secs));
    }

    public static FpsMonitor? TryStart()
    {
        try { return new FpsMonitor(); }
        catch { return null; } // sin admin o ETW no disponible: el overlay muestra "—"
    }

    private void OnEvent(TraceEvent e)
    {
        int id = (int)e.ID;
        bool present = (e.ProviderGuid == Dxgi && (id == DxgiPresentStart || id == DxgiPresentMpoStart))
                       || (e.ProviderGuid == D3D9 && id == D3D9PresentStart);
        if (!present) return;
        lock (_lock)
        {
            if (!_presents.TryGetValue(e.ProcessID, out var list)) _presents[e.ProcessID] = list = new List<double>(256);
            list.Add(e.TimeStampRelativeMSec);
        }
    }

    /// <summary>Llamar ~1 vez por segundo. Devuelve FPS y frametime medio del proceso indicado.</summary>
    public (double? fps, double? frametime, string? name) Sample(int pid)
    {
        List<double>? list;
        lock (_lock)
        {
            _presents.TryGetValue(pid, out list);
            // Nos quedamos solo con el último instante del proceso vigilado para que el siguiente
            // intervalo empiece donde acabó este; el resto se descarta para no acumular memoria.
            _presents.Clear();
            if (list is { Count: > 0 }) _presents[pid] = new List<double>(256) { list[^1] };
        }
        if (list is null || list.Count < 3) return (null, null, null);

        double span = list[^1] - list[0];
        if (span <= 0) return (null, null, null);
        double frametime = span / (list.Count - 1);
        return (1000.0 / frametime, frametime, NameOf(pid));
    }

    private string? NameOf(int pid)
    {
        if (_names.TryGetValue(pid, out var n)) return n;
        try { using var p = Process.GetProcessById(pid); n = p.ProcessName; }
        catch { n = null; }
        if (_names.Count > 256) _names.Clear(); // no acumular nombres de procesos que ya cerraron
        if (n != null) _names[pid] = n;
        return n;
    }

    public void Dispose()
    {
        try { _session.Dispose(); } catch { }
    }
}
