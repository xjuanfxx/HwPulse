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
    private readonly object _netProviderLock = new();
    private bool _netProviderOn;

    private FpsMonitor()
    {
        _session = new TraceEventSession("HwPulse-FPS") { StopOnDispose = true };
        _session.EnableProvider(Dxgi, TraceEventLevel.Verbose);
        _session.EnableProvider(D3D9, TraceEventLevel.Verbose);
        // Kernel-Network no se activa aquí: capturaría cada envío y recepción TCP/UDP de todo el sistema,
        // mientras la app esté abierta. Se enciende bajo demanda con SetNetworkEnabled (página de Red).
        _session.Source.AllEvents += OnEvent;
        _session.Source.Dynamic.All += OnNetwork; // el parser dinámico da acceso a los campos PID y size
        new Thread(() => { try { _session.Source.Process(); } catch { } }) { IsBackground = true, Name = "HwPulse ETW" }.Start();
    }

    /// <summary>Activa o desactiva la captura de red por proceso (Kernel-Network) según la página de Red
    /// esté visible. Con el proveedor apagado, Windows deja de publicar esos eventos y OnNetwork no corre
    /// por cada paquete del sistema entero; la llamada es un simple "ya está así" si el estado no cambia.</summary>
    public void SetNetworkEnabled(bool on)
    {
        lock (_netProviderLock)
        {
            if (on == _netProviderOn) return;
            _netProviderOn = on;
            try
            {
                if (on) _session.EnableProvider(KernelNetwork, TraceEventLevel.Informational);
                else _session.DisableProvider(KernelNetwork);
            }
            catch { /* ETW no disponible: la red por proceso queda a cero */ }
        }
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

    /// <summary>Descarta el tráfico acumulado por proceso. Con la ventana oculta en la bandeja nadie lo
    /// consume (SampleNetwork no se llama), y el diccionario crecería sin límite: una entrada por cada PID
    /// que haya tenido tráfico desde el arranque.</summary>
    public void ResetNetwork()
    {
        lock (_lock) { _net.Clear(); _netSince = DateTime.UtcNow; }
    }

    public static FpsMonitor? TryStart()
    {
        try
        {
            // Un cierre abrupto (kill, corte de luz, crash) deja la sesión de ETW huérfana: es un objeto de
            // kernel que sobrevive al proceso, y el siguiente arranque fallaría al crear otra con el mismo
            // nombre (y los FPS quedarían muertos hasta reiniciar Windows). Si existe, se detiene antes.
            using var orphan = TraceEventSession.GetActiveSession("HwPulse-FPS");
            orphan?.Stop();
            return new FpsMonitor();
        }
        catch { return null; } // sin admin o ETW no disponible: el overlay muestra "—"
    }

    private double _latestTs; // marca de tiempo del último Present visto (ms relativos a la sesión)

    private void OnEvent(TraceEvent e)
    {
        int id = (int)e.ID;
        bool present = (e.ProviderGuid == Dxgi && (id == DxgiPresentStart || id == DxgiPresentMpoStart))
                       || (e.ProviderGuid == D3D9 && id == D3D9PresentStart);
        if (!present) return;
        lock (_lock)
        {
            if (!_presents.TryGetValue(e.ProcessID, out var list)) _presents[e.ProcessID] = list = new List<double>(512);
            double ts = e.TimeStampRelativeMSec;
            list.Add(ts);
            if (ts > _latestTs) _latestTs = ts;
        }
    }

    // ETW entrega los eventos en ráfagas (por búferes), no uno a uno: un segundo puede llegar vacío y el
    // siguiente doble. Por eso se mide sobre una ventana deslizante por marca de tiempo y, si no llega
    // nada nuevo, se mantiene la última lectura un momento en vez de mostrar "—".
    private const double WindowMs = 1000, KeepMs = 2000;
    private static readonly TimeSpan HoldFor = TimeSpan.FromSeconds(2.5);
    private int _gamePid;                      // último proceso medido (el juego)
    private (double fps, double ft, string? name) _last;
    private DateTime _lastAt;

    /// <summary>Llamar ~1 vez por segundo. Devuelve FPS y frametime medio del proceso en primer plano.</summary>
    public (double? fps, double? frametime, string? name) Sample(int fgPid)
    {
        double[]? win = null;
        int pid = 0;
        lock (_lock)
        {
            // Descarta lo viejo de todos los procesos para no acumular memoria.
            double cut = _latestTs - KeepMs;
            foreach (var key in _presents.Keys.ToList())
            {
                var l = _presents[key];
                int old = 0;
                while (old < l.Count && l[old] < cut) old++;
                if (old > 0) l.RemoveRange(0, old);
                if (l.Count == 0) _presents.Remove(key);
            }
            // El juego en primer plano; si ahora mismo no presenta (una notificación, la superposición de
            // Steam/Epic con el foco), se sigue con el último juego medido mientras siga dibujando.
            foreach (int cand in new[] { fgPid, _gamePid })
            {
                if (cand == 0 || !_presents.TryGetValue(cand, out var l)) continue;
                double from = l[^1] - WindowMs;
                int i = l.FindIndex(t => t >= from);
                if (l.Count - i >= 3 && l[^1] >= _latestTs - KeepMs) { win = l.GetRange(i, l.Count - i).ToArray(); pid = cand; break; }
            }
        }

        if (win != null)
        {
            double span = win[^1] - win[0];
            if (span > 0)
            {
                double ft = span / (win.Length - 1);
                _gamePid = pid;
                _last = (1000.0 / ft, ft, NameOf(pid));
                _lastAt = DateTime.UtcNow;
                return _last;
            }
        }
        // Sin eventos nuevos en este ciclo (ráfaga retrasada): se mantiene la última lectura un instante.
        if (_gamePid != 0 && DateTime.UtcNow - _lastAt < HoldFor) return _last;
        _gamePid = 0;
        return (null, null, null);
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
