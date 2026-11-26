using System.Diagnostics;
using System.Text.RegularExpressions;

namespace HwPulse;

/// <summary>
/// Uso de GPU y VRAM por proceso, de los contadores "GPU Engine" y "GPU Process Memory" de Windows
/// (los mismos del Administrador de tareas). Como allí, el uso de un proceso es el del motor más ocupado
/// (3D, copia, video…), sumando sus instancias del mismo tipo.
/// </summary>
public sealed class GpuProcessMonitor
{
    private static readonly Regex Pid = new(@"pid_(\d+)_", RegexOptions.Compiled);
    private static readonly Regex EngType = new(@"engtype_([A-Za-z0-9]+)", RegexOptions.Compiled);

    private PerformanceCounterCategory? _engines, _memory;
    private Dictionary<string, CounterSample> _prev = new();
    private bool _broken;

    public Dictionary<int, (double pct, long dedicated)> Sample()
    {
        var result = new Dictionary<int, (double pct, long dedicated)>();
        if (_broken) return result;
        try
        {
            _engines ??= new PerformanceCounterCategory("GPU Engine");
            _memory ??= new PerformanceCounterCategory("GPU Process Memory");

            var util = new Dictionary<int, Dictionary<string, double>>();
            var next = new Dictionary<string, CounterSample>();
            var data = _engines.ReadCategory();
            if (data.Contains("Utilization Percentage"))
            {
                foreach (InstanceData inst in data["Utilization Percentage"].Values)
                {
                    var m = Pid.Match(inst.InstanceName);
                    if (!m.Success) continue;
                    next[inst.InstanceName] = inst.Sample;
                    if (!_prev.TryGetValue(inst.InstanceName, out var old)) continue; // hacen falta dos lecturas
                    double v = CounterSample.Calculate(old, inst.Sample);
                    if (!double.IsFinite(v) || v <= 0) continue;
                    int pid = int.Parse(m.Groups[1].Value);
                    string type = EngType.Match(inst.InstanceName) is { Success: true } t ? t.Groups[1].Value : "other";
                    if (!util.TryGetValue(pid, out var byType)) util[pid] = byType = new();
                    byType[type] = byType.GetValueOrDefault(type) + v;
                }
            }
            _prev = next;

            foreach (var (pid, byType) in util)
                result[pid] = (Math.Min(100, byType.Values.Max()), 0);

            var mem = _memory.ReadCategory();
            if (mem.Contains("Dedicated Usage"))
            {
                foreach (InstanceData inst in mem["Dedicated Usage"].Values)
                {
                    var m = Pid.Match(inst.InstanceName + "_");
                    if (!m.Success) continue;
                    int pid = int.Parse(m.Groups[1].Value);
                    var cur = result.GetValueOrDefault(pid);
                    result[pid] = (cur.pct, cur.dedicated + inst.RawValue);
                }
            }
        }
        catch { _broken = true; } // contadores no disponibles en este equipo: la lista de GPU queda vacía
        return result;
    }
}
