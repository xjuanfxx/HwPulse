using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using LibreHardwareMonitor.Hardware;

namespace HwPulse;

public sealed record CoreReading(int Index, double? ClockMhz, double? TempC, double? Load, string? Label = null);
/// <param name="Short">Nombre corto: tipo y letras de unidad, p. ej. "NVMe C:" o "HDD D: E:".</param>
public sealed record DiskReading(string Key, string Name, double? TempC, double? Activity, double? ReadBps, double? WriteBps, double? UsedPct,
                                 string Short = "");
public sealed record SensorReading(string Key, string Hardware, string Name, SensorType Type, double? Value, double? Min, double? Max);
public sealed record NetReading(string Key, string Name, double? DownBps, double? UpBps, double? DownTotalGb, double? UpTotalGb);
public sealed record FanReading(string Key, string Name, double? Rpm);

/// <summary>Lectura de todos los sensores en un instante.</summary>
public sealed class Snapshot
{
    public bool IsAdmin { get; init; }

    public string? CpuName { get; set; }
    public double? CpuTempMax { get; set; }
    public double? CpuTempPkg { get; set; }
    public double? CpuClockAvg { get; set; }
    public double? CpuLoad { get; set; }
    public double? CpuCoreLoadMax { get; set; }
    public double? CpuPower { get; set; }
    public double? CpuVoltage { get; set; }
    public double? TjDistanceMin { get; set; }
    public List<CoreReading> Cores { get; } = new();

    public string? GpuName { get; set; }
    public double? GpuLoad { get; set; }
    public double? GpuTemp { get; set; }
    public double? GpuHotSpot { get; set; }
    public double? GpuCoreClock { get; set; }
    public double? GpuMemClock { get; set; }
    public double? GpuPower { get; set; }
    /// <summary>Potencia de la GPU en % de su máximo (respaldo de Windows cuando no hay vatios).</summary>
    public double? GpuPowerPct { get; set; }
    public double? VramUsedMb { get; set; }
    public double? VramTotalMb { get; set; }
    public double? GpuFanRpm { get; set; }
    public double? GpuFanPct { get; set; }

    public double? RamUsedGb { get; set; }
    public double? RamAvailGb { get; set; }
    public double? RamLoad { get; set; }
    public double? VirtUsedGb { get; set; }
    public double? VirtAvailGb { get; set; }

    public List<DiskReading> Disks { get; } = new();
    public List<NetReading> Networks { get; } = new();
    public List<FanReading> Fans { get; } = new();
    public double NetDownBps => Networks.Sum(n => n.DownBps ?? 0);
    public double NetUpBps => Networks.Sum(n => n.UpBps ?? 0);
    public List<ProcInfo> Processes { get; set; } = new();
    public bool NetByProcessAvailable { get; set; }

    public double? Fps { get; set; }
    public double? FrametimeMs { get; set; }
    public string? FpsProcess { get; set; }

    public double? BatteryCharge { get; set; }
    public double? BatteryRateW { get; set; }
    public bool? OnAc { get; set; }

    public List<SensorReading> AllSensors { get; } = new();
}

/// <summary>Envuelve LibreHardwareMonitorLib y traduce sus sensores a un <see cref="Snapshot"/>.</summary>
public sealed class SensorService : IDisposable
{
    // Nombres de núcleo en LHM según el fabricante y la generación, todos con el mismo patrón:
    //   Intel clásico:  "CPU Core #N" (reloj/temperatura), "CPU Core #N Thread #M" (carga por hilo)
    //   Intel híbrido:  "P-Core #N" / "E-Core #N" (12.ª gen en adelante: núcleos de rendimiento y eficiencia)
    //   AMD Ryzen:      "Core #N" (reloj por núcleo físico), "CPU Core #1..#12" (carga por hilo lógico)
    // Se excluyen los derivados ("Core #1 (Effective)", "Core #1 VID", "Core #1 (SMU)") porque el patrón
    // está anclado: solo encaja el nombre exacto del núcleo.
    private static readonly Regex CoreName = new(@"^(?:CPU\s+)?(?:(?<k>[PE])-)?Core #(?<n>\d+)(?:\s+Thread #\d+)?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex FilterAdapter = new(@"-\d{4}$", RegexOptions.Compiled);

    private readonly Computer _pc;
    private readonly NvmlPower _nvml = new(); // respaldo de la potencia NVIDIA si LHM no la da
    private readonly NativeSensors _native = new(); // respaldos de Windows (sin driver): reloj CPU, datos GPU
    private readonly object _lock = new();
    private readonly bool _isAdmin;

    /// <summary>
    /// Fase 1: solo lo que se ve primero (CPU, GPU, memoria, batería), que abre rápido.
    /// Discos y red se añaden después con <see cref="EnableExtras"/>: con admin, leer SMART de cada disco
    /// (sobre todo los externos por USB) puede tardar varios segundos y no debe retrasar el resto.
    /// </summary>
    public SensorService(bool isAdmin)
    {
        _isAdmin = isAdmin;
        _pc = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsBatteryEnabled = true,
        };
        // La caché del mapeo se invalida con los eventos de LHM, no releyendo su árbol cada segundo.
        _pc.HardwareAdded += _ => _mapDirty = true;   // discos/red al activarlos, hotplug USB…
        _pc.HardwareRemoved += _ => _mapDirty = true;
        _pc.Open();
    }

    public bool ExtrasEnabled { get; private set; }

    /// <summary>Fase 2: discos y red. Mientras se abren, Read() devuelve null (se conserva la lectura anterior).</summary>
    public void EnableExtras()
    {
        lock (_lock)
        {
            if (ExtrasEnabled) return;
            try
            {
                _pc.IsStorageEnabled = true;
                _pc.IsNetworkEnabled = true;
            }
            catch { /* si un dispositivo falla, el resto sigue funcionando */ }
            ExtrasEnabled = true;
        }
    }

    private static IEnumerable<IHardware> Flatten(IHardware h) // solo para --bench
    {
        yield return h;
        foreach (var sub in h.SubHardware)
            foreach (var x in Flatten(sub))
                yield return x;
    }

    private static double? V(ISensor? s) => s?.Value is float f && !float.IsNaN(f) && !float.IsInfinity(f) ? f : null;
    private static double? V(float? f) => f is float x && !float.IsNaN(x) && !float.IsInfinity(x) ? x : null;

    // ------------------------------------------------------------------ caché del mapeo de hardware y sensores
    // LibreHardwareMonitor crea una lista nueva cada vez que se lee Computer.Hardware y un array nuevo por
    // cada acceso a Sensors. Antes además se recorría su árbol dos veces por segundo con SelectMany(Flatten)
    // anidado, ~15 ToList() por lectura, una regex por sensor del CPU y un Identifier.ToString() por sensor
    // y por segundo. El mapeo se construye una vez y se invalida SOLO con los eventos de LHM:
    // HardwareAdded/HardwareRemoved (discos y redes al activarlos, hotplug) y SensorAdded/SensorRemoved de
    // cada hardware (los discos ganan sensores tras su primera lectura SMART, las GPU tras el primer sondeo).

    private volatile bool _mapDirty = true;
    private readonly HashSet<IHardware> _watched = new();     // hardware al que ya nos suscribimos (sensores)
    private IHardware[] _flat = Array.Empty<IHardware>();    // hardware + subhardware aplanado, orden estable
    private int _topCount;                                    // hardware de primer nivel (red de seguridad)
    private int _mapVersion;                                  // ++ en cada reconstrucción
    private readonly Dictionary<IHardware, ISensor[]> _ownSensors = new();       // sensores del hardware mismo
    private readonly Dictionary<IHardware, ISensor[]> _flattenedSensors = new(); // + subhardware (como SensorsOf)
    private readonly Dictionary<IHardware, Dictionary<(SensorType, string), ISensor>> _named = new(); // por (tipo, nombre)
    private readonly Dictionary<ISensor, string> _sensorKeys = new();    // Identifier.ToString() (inmutable)
    private readonly Dictionary<IHardware, string> _hwKeys = new();      // Identifier.ToString() (inmutable)
    private IHardware? _cpuHw, _gpuNv, _gpuAmd, _gpuI, _memHw, _virtHw, _batHw;
    private IHardware[] _disks = Array.Empty<IHardware>();
    private IHardware[] _netAdapters = Array.Empty<IHardware>(); // sin los filtros "-0000" del mismo adaptador

    // Parse de "CPU Core #N": una vez por sensor, no una regex por segundo.
    private enum CoreRole : byte { None, Clock, Temp, Load, Tj }
    private readonly Dictionary<ISensor, (CoreRole role, int core)> _coreRoles = new();
    private int _coreOrderVersion = -1;
    // Núcleos físicos en orden de pantalla (P primero, luego normales, luego E) y a qué núcleo físico va
    // cada clave de carga. Se calcula una vez por versión del mapa de sensores.
    private int[] _physKeys = Array.Empty<int>();
    private readonly Dictionary<int, int> _loadToPhys = new();
    private readonly Dictionary<int, int> _alias = new(); // clave sin tipo -> núcleo P/E equivalente (híbridos)
    private const int KindPlain = 0, KindP = 1, KindE = 2;
    private static int CoreKey(int kind, int n) => kind * 1000 + n;
    private static int KindOf(int key) => key / 1000;
    private static int NumOf(int key) => key % 1000;
    private static int DisplayOrder(int key) => KindOf(key) switch { KindP => 0, KindPlain => 1, _ => 2 } * 1000 + NumOf(key);
    // Diccionarios reutilizados en cada lectura (antes se creaban nuevos por tick).
    private readonly Dictionary<int, double?> _clocks = new();
    private readonly Dictionary<int, double?> _temps = new();
    private readonly Dictionary<int, List<double>> _loads = new();

    /// <summary>
    /// Lee todos los sensores. Con <paramref name="waitMs"/> finito devuelve null si otra operación
    /// (abrir discos y red) tiene ocupado el acceso, para no congelar el ciclo de lectura.
    /// </summary>
    public Snapshot? Read(int waitMs = Timeout.Infinite)
    {
        if (!Monitor.TryEnter(_lock, waitMs)) return null;
        try { return ReadLocked(); }
        finally { Monitor.Exit(_lock); }
    }

    private Snapshot ReadLocked()
    {
        EnsureMap();
        foreach (var h in _flat)
        {
            try { h.Update(); } catch { /* un dispositivo que falla no debe tumbar el resto */ }
        }
        // Los Update pueden haber añadido sensores o hardware (discos tras su primera lectura): el evento
        // marcó la caché como sucia; se reconstruye antes de leer los valores de este mismo ciclo.
        if (_mapDirty) RebuildMap();

        var snap = new Snapshot { IsAdmin = _isAdmin, OnAc = Native.IsOnAc() };

        if (_cpuHw != null) FillCpu(_cpuHw, snap);
        var gpu = _gpuNv ?? _gpuAmd ?? _gpuI;
        if (gpu != null) FillGpu(gpu, snap);
        FillMemory(snap);
        FillDisks(snap);
        FillBattery(snap);
        FillNetwork(snap);
        FillFans(snap);

        var all = snap.AllSensors;
        foreach (var h in _flat)
        {
            var ss = _ownSensors[h];
            var hwName = h.Name;
            for (int i = 0; i < ss.Length; i++)
            {
                var s = ss[i];
                all.Add(new SensorReading(_sensorKeys[s], hwName, s.Name, s.SensorType, V(s.Value), V(s.Min), V(s.Max)));
            }
        }
        return snap;
    }

    /// <summary>Comprueba que la caché sigue válida (barato). Los eventos la reconstruyen normalmente;
    /// comparar el número de hardware es la red de seguridad por si algo cambiara sin evento.</summary>
    private void EnsureMap()
    {
        if (_mapDirty || _pc.Hardware.Count != _topCount) RebuildMap();
    }

    private void RebuildMap()
    {
        _mapDirty = false;
        try
        {
            _mapVersion++;
            var top = _pc.Hardware; // una sola lista nueva por reconstrucción (LHM crea una por acceso)
            _topCount = top.Count;

            var flat = new List<IHardware>(top.Count * 2);
            var disks = new List<IHardware>();
            var adapters = new List<IHardware>();
            _cpuHw = _gpuNv = _gpuAmd = _gpuI = _memHw = _virtHw = _batHw = null;
            foreach (var h in top)
            {
                AddFlat(h);
                switch (h.HardwareType)
                {
                    case HardwareType.Cpu: _cpuHw ??= h; break;
                    case HardwareType.GpuNvidia: _gpuNv ??= h; break;
                    case HardwareType.GpuAmd: _gpuAmd ??= h; break;
                    case HardwareType.GpuIntel: _gpuI ??= h; break;
                    case HardwareType.Memory:
                        if (h.Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase)) _virtHw ??= h;
                        else _memHw ??= h;
                        break;
                    case HardwareType.Storage: disks.Add(h); break;
                    case HardwareType.Network:
                        if (!FilterAdapter.IsMatch(h.Name)) adapters.Add(h); // filtros QoS: repiten el tráfico
                        break;
                    case HardwareType.Battery: _batHw ??= h; break;
                }
            }
            _flat = flat.ToArray();
            _disks = disks.ToArray();
            _netAdapters = adapters.ToArray();

            // Sensores aplanados (el propio + subhardware), de abajo hacia arriba para que los hijos existan ya.
            for (int i = flat.Count - 1; i >= 0; i--) _flattenedSensors[flat[i]] = BuildFlattened(flat[i]);

            void AddFlat(IHardware h)
            {
                flat.Add(h);
                Watch(h);
                RefreshSensors(h);
                var subs = h.SubHardware;
                for (int i = 0; i < subs.Length; i++) AddFlat(subs[i]);
            }
        }
        catch
        {
            // Un hardware defectuoso no puede dejar la caché a medio construir marcada como válida:
            // se vuelve a marcar sucia y el próximo ciclo reintenta (el error lo registra el bucle).
            _mapDirty = true;
            throw;
        }
    }

    private ISensor[] BuildFlattened(IHardware h)
    {
        var own = _ownSensors[h];
        var subs = h.SubHardware;
        if (subs.Length == 0) return own;
        int n = own.Length;
        var parts = new List<ISensor[]>(subs.Length);
        foreach (var sub in subs) { var a = _flattenedSensors[sub]; parts.Add(a); n += a.Length; }
        var merged = new ISensor[n];
        int off = own.Length;
        Array.Copy(own, merged, own.Length);
        foreach (var a in parts) { Array.Copy(a, 0, merged, off, a.Length); off += a.Length; }
        return merged;
    }

    private void Watch(IHardware h)
    {
        if (!_watched.Add(h)) return;
        h.SensorAdded += _ => _mapDirty = true;
        h.SensorRemoved += _ => _mapDirty = true;
    }

    private void RefreshSensors(IHardware h)
    {
        var own = h.Sensors; // copia estable de un instante (LHM da un array nuevo por acceso)
        _ownSensors[h] = own;
        if (!_hwKeys.ContainsKey(h)) _hwKeys[h] = h.Identifier.ToString();
        var byName = new Dictionary<(SensorType, string), ISensor>(own.Length);
        foreach (var s in own)
        {
            if (!_sensorKeys.ContainsKey(s)) _sensorKeys[s] = s.Identifier.ToString();
            byName.TryAdd((s.SensorType, s.Name.ToLowerInvariant()), s); // TryAdd = como el FirstOrDefault antiguo
        }
        _named[h] = byName;
    }

    /// <summary>Valor de un sensor por (tipo, nombre en minúsculas), o null si el hardware no lo tiene.</summary>
    private double? NamedV(IHardware h, SensorType t, string lowerName) =>
        _named[h].TryGetValue((t, lowerName), out var s) ? V(s) : null;

    /// <summary>Valor del primer sensor del hardware (y su subhardware) del tipo dado.</summary>
    private double? FirstV(IHardware h, SensorType t)
    {
        var ss = _flattenedSensors[h];
        for (int i = 0; i < ss.Length; i++) if (ss[i].SensorType == t) return V(ss[i]);
        return null;
    }

    /// <summary>Clasifica un sensor del CPU una sola vez (antes: una regex por sensor y por segundo).</summary>
    private static (CoreRole role, int core) ParseCoreRole(ISensor s) => ParseCoreRole(s.SensorType, s.Name);

    private static (CoreRole role, int core) ParseCoreRole(SensorType type, string name)
    {
        if (type == SensorType.Temperature && name.Contains("Distance to TjMax", StringComparison.OrdinalIgnoreCase))
            return (CoreRole.Tj, 0);
        var m = CoreName.Match(name);
        if (!m.Success) return (CoreRole.None, 0);
        int kind = m.Groups["k"].Success ? (char.ToUpperInvariant(m.Groups["k"].Value[0]) == 'P' ? KindP : KindE) : KindPlain;
        int key = CoreKey(kind, int.Parse(m.Groups["n"].Value));
        return type switch
        {
            SensorType.Clock => (CoreRole.Clock, key),
            SensorType.Temperature => (CoreRole.Temp, key),
            SensorType.Load => (CoreRole.Load, key),
            _ => (CoreRole.None, 0),
        };
    }

    private void FillCpu(IHardware cpu, Snapshot snap)
    {
        snap.CpuName = cpu.Name;
        var ss = _flattenedSensors[cpu];

        _clocks.Clear();
        _temps.Clear();
        foreach (var list in _loads.Values) list.Clear();
        double? tjMin = null;

        // El conjunto de núcleos solo cambia cuando cambia el hardware: el orden se cachea con el mapa.
        if (_coreOrderVersion != _mapVersion)
        {
            var phys = new HashSet<int>();
            var loadKeys = new HashSet<int>();
            foreach (var s in ss)
            {
                if (!_coreRoles.TryGetValue(s, out var r)) _coreRoles[s] = r = ParseCoreRole(s);
                if (r.role is CoreRole.Clock or CoreRole.Temp) phys.Add(r.core);
                else if (r.role == CoreRole.Load) loadKeys.Add(r.core);
            }
            BuildCoreMap(phys, loadKeys);
            _coreOrderVersion = _mapVersion;
        }

        foreach (var s in ss)
        {
            if (!_coreRoles.TryGetValue(s, out var role))
                _coreRoles[s] = role = ParseCoreRole(s);
            switch (role.role)
            {
                case CoreRole.Clock: _clocks[_alias.GetValueOrDefault(role.core, role.core)] = V(s); break;
                case CoreRole.Temp: _temps[_alias.GetValueOrDefault(role.core, role.core)] = V(s); break;
                case CoreRole.Load:
                    if (V(s) is double l && _loadToPhys.TryGetValue(role.core, out int core)) // hilo -> núcleo físico
                    {
                        if (!_loads.TryGetValue(core, out var list)) _loads[core] = list = new List<double>(2);
                        list.Add(l);
                    }
                    break;
                case CoreRole.Tj:
                    if (V(s) is double d && (tjMin is null || d < tjMin)) tjMin = d;
                    break;
            }
        }
        snap.TjDistanceMin = tjMin;

        double clockSum = 0;
        int clockCount = 0;
        double? coreTempMax = null;
        bool hybrid = false;
        foreach (var k in _physKeys) if (KindOf(k) != KindPlain) { hybrid = true; break; }
        for (int pos = 0; pos < _physKeys.Length; pos++)
        {
            int i = _physKeys[pos];
            _clocks.TryGetValue(i, out var c);
            _temps.TryGetValue(i, out var t);
            double? l = null;
            if (_loads.TryGetValue(i, out var list) && list.Count > 0)
            {
                double sum = 0;
                for (int j = 0; j < list.Count; j++) sum += list[j];
                l = sum / list.Count;
            }
            if (c is double cm && cm > 0) { clockSum += cm; clockCount++; }
            if (t is double tm && (coreTempMax is null || tm > coreTempMax)) coreTempMax = tm;
            string? label = hybrid ? $"{(KindOf(i) == KindP ? "P" : KindOf(i) == KindE ? "E" : "")}{NumOf(i)}" : null;
            snap.Cores.Add(new CoreReading(pos + 1, c, t, l, label));
        }
        snap.CpuClockAvg = clockCount > 0 ? clockSum / clockCount : null;

        // Sin relojes por núcleo reconocibles: el promedio que publique LHM, o el promedio de todos los relojes
        // del CPU que no sean el bus, "Effective" ni un promedio ya calculado.
        snap.CpuClockAvg ??= NamedV(cpu, SensorType.Clock, "cores (average)") ?? AvgCpuClock(ss)
                             ?? _native.CpuClockMhz(); // sin PawnIO (anti-cheat, sin admin): contadores de Windows

        // Intel: "Core Max" / por núcleo / "CPU Package". AMD: "Core (Tctl/Tdie)" (Ryzen) o "CPU Cores";
        // los Ryzen con varios CCD además publican "CCD1 (Tdie)"… Como último recurso, la más alta del CPU.
        snap.CpuTempMax = NamedV(cpu, SensorType.Temperature, "core max") ?? coreTempMax
                          ?? NamedV(cpu, SensorType.Temperature, "core (tctl/tdie)")
                          ?? NamedV(cpu, SensorType.Temperature, "core (tctl)")
                          ?? NamedV(cpu, SensorType.Temperature, "cpu cores")
                          ?? NamedV(cpu, SensorType.Temperature, "cpu package")
                          ?? MaxCpuTemp(ss);
        snap.CpuTempPkg = NamedV(cpu, SensorType.Temperature, "cpu package");
        snap.CpuLoad = NamedV(cpu, SensorType.Load, "cpu total");
        snap.CpuCoreLoadMax = NamedV(cpu, SensorType.Load, "cpu core max");
        snap.CpuPower = NamedV(cpu, SensorType.Power, "cpu package") ?? NamedV(cpu, SensorType.Power, "package")
                        ?? FirstContaining(ss, SensorType.Power, "package");
        snap.CpuVoltage = NamedV(cpu, SensorType.Voltage, "cpu core") ?? NamedV(cpu, SensorType.Voltage, "core (svi2 tfn)")
                          ?? NamedV(cpu, SensorType.Voltage, "core (svi3 tfn)") ?? FirstContaining(ss, SensorType.Voltage, "core");
    }

    /// <summary>
    /// Decide qué núcleos físicos se muestran y a cuál va cada carga, sin depender de un fabricante:
    /// 1) la carga ya usa la misma clave que un reloj/temperatura (Intel clásico) → directo;
    /// 2) mismo número de claves de carga que núcleos → por posición (Intel híbrido con cargas "CPU Core #N");
    /// 3) dos hilos por núcleo con SMT → de dos en dos (AMD Ryzen, cargas por hilo lógico);
    /// 4) Intel híbrido con cargas por hilo: P-cores con dos hilos y E-cores con uno → en ese orden.
    /// Si nada encaja, cada clave de carga es su propio núcleo (mejor ver algo que nada).
    /// </summary>
    private void BuildCoreMap(HashSet<int> phys, HashSet<int> loadKeys) =>
        _physKeys = PlanCores(phys, loadKeys, _loadToPhys, _alias);

    private static int[] PlanCores(HashSet<int> phys, HashSet<int> loadKeys, Dictionary<int, int> _loadToPhys, Dictionary<int, int> _alias)
    {
        _loadToPhys.Clear();
        _alias.Clear();
        // Híbridos donde unos sensores dicen "P-Core #N"/"E-Core #N" y otros "CPU Core #N": si hay tantos
        // núcleos sin tipo como con tipo, son los mismos núcleos (P primero) y se unen por posición.
        var typed = phys.Where(k => KindOf(k) != KindPlain).OrderBy(DisplayOrder).ToList();
        var plain = phys.Where(k => KindOf(k) == KindPlain).OrderBy(DisplayOrder).ToList();
        if (typed.Count > 0 && plain.Count == typed.Count)
        {
            for (int i = 0; i < plain.Count; i++) _alias[plain[i]] = typed[i];
            phys = new HashSet<int>(typed);
        }

        var physOrder = phys.OrderBy(DisplayOrder).ToList();
        var loads = loadKeys.OrderBy(DisplayOrder).ToList();

        if (physOrder.Count == 0)
        {
            foreach (var k in loads) _loadToPhys[k] = k;
            physOrder = loads;
        }
        else if (loads.All(k => phys.Contains(k) || _alias.ContainsKey(k)))
            foreach (var k in loads) _loadToPhys[k] = _alias.GetValueOrDefault(k, k);
        else if (loads.Count == physOrder.Count)
            for (int i = 0; i < loads.Count; i++) _loadToPhys[loads[i]] = physOrder[i];
        else if (loads.Count == physOrder.Count * 2)
            for (int i = 0; i < loads.Count; i++) _loadToPhys[loads[i]] = physOrder[i / 2];
        else if (loads.Count == physOrder.Sum(k => KindOf(k) == KindE ? 1 : 2))
        {
            int li = 0;
            foreach (var core in physOrder)
                for (int t = KindOf(core) == KindE ? 1 : 2; t > 0; t--) _loadToPhys[loads[li++]] = core;
        }
        else
        {
            foreach (var k in loads) { _loadToPhys[k] = k; if (!phys.Contains(k)) physOrder.Add(k); }
        }
        return physOrder.ToArray();
    }

    /// <summary>
    /// --selftest-cpu: aplica el reconocimiento de núcleos a los nombres reales que publica LHM en varios CPU
    /// (sin necesitar ese hardware) y describe el resultado: qué núcleos salen y cuántas cargas van a cada uno.
    /// </summary>
    public static string SelfTestCores()
    {
        static IEnumerable<string> R(string fmt, int from, int to) => Enumerable.Range(from, to - from + 1).Select(i => string.Format(fmt, i));
        var cases = new (string name, (SensorType t, string n)[] sensors)[]
        {
            ("Intel i5-10300H (4c/8t)", R("CPU Core #{0}", 1, 4).Select(n => (SensorType.Clock, n))
                .Concat(R("CPU Core #{0}", 1, 4).Select(n => (SensorType.Temperature, n)))
                .Concat(R("CPU Core #{0} Thread #1", 1, 4).Concat(R("CPU Core #{0} Thread #2", 1, 4)).Select(n => (SensorType.Load, n))).ToArray()),
            ("AMD Ryzen 5 5500 (6c/12t)", R("Core #{0}", 1, 6).Select(n => (SensorType.Clock, n))
                .Concat(R("Core #{0} (Effective)", 1, 6).Select(n => (SensorType.Clock, n)))
                .Concat(R("CPU Core #{0}", 1, 12).Select(n => (SensorType.Load, n))).ToArray()),
            ("Intel hybrid A: P/E clocks+temps, loads CPU Core #1..24 Thread", R("P-Core #{0}", 1, 8).Concat(R("E-Core #{0}", 1, 16)).Select(n => (SensorType.Clock, n))
                .Concat(R("P-Core #{0}", 1, 8).Concat(R("E-Core #{0}", 1, 16)).Select(n => (SensorType.Temperature, n)))
                .Concat(R("CPU Core #{0} Thread #1", 1, 24).Concat(R("CPU Core #{0} Thread #2", 1, 8)).Select(n => (SensorType.Load, n))).ToArray()),
            ("Intel hybrid B: P/E clocks, temps CPU Core #1..24, loads per thread CPU Core #1..32", R("P-Core #{0}", 1, 8).Concat(R("E-Core #{0}", 1, 16)).Select(n => (SensorType.Clock, n))
                .Concat(R("CPU Core #{0}", 1, 24).Select(n => (SensorType.Temperature, n)))
                .Concat(R("CPU Core #{0}", 1, 32).Select(n => (SensorType.Load, n))).ToArray()),
            ("Intel hybrid C: everything P/E, loads P-Core #N Thread #M", R("P-Core #{0}", 1, 8).Concat(R("E-Core #{0}", 1, 16)).Select(n => (SensorType.Clock, n))
                .Concat(R("P-Core #{0} Thread #1", 1, 8).Concat(R("P-Core #{0} Thread #2", 1, 8)).Concat(R("E-Core #{0}", 1, 16)).Select(n => (SensorType.Load, n))).ToArray()),
        };
        var sb = new StringBuilder();
        foreach (var (name, sensors) in cases)
        {
            var phys = new HashSet<int>(); var loads = new HashSet<int>(); int loadSensors = 0;
            var loadKeyCounts = new Dictionary<int, int>();
            foreach (var (t, n) in sensors)
            {
                var (role, key) = ParseCoreRole(t, n);
                if (role is CoreRole.Clock or CoreRole.Temp) phys.Add(key);
                else if (role == CoreRole.Load) { loads.Add(key); loadSensors++; loadKeyCounts[key] = loadKeyCounts.GetValueOrDefault(key) + 1; }
            }
            var map = new Dictionary<int, int>(); var alias = new Dictionary<int, int>();
            var order = PlanCores(phys, loads, map, alias);
            var perCore = order.Select(k => (k, n: map.Where(kv => kv.Value == k).Sum(kv => loadKeyCounts[kv.Key]))).ToList();
            bool hybrid = order.Any(k => KindOf(k) != KindPlain);
            string Label(int k) => hybrid ? $"{(KindOf(k) == KindP ? "P" : KindOf(k) == KindE ? "E" : "")}{NumOf(k)}" : NumOf(k).ToString();
            sb.AppendLine($"{name}: {order.Length} cores, {loadSensors} load sensors, unmapped loads {loads.Count(k => !map.ContainsKey(k))}");
            sb.AppendLine("  " + string.Join(" ", perCore.Select(p => $"{Label(p.k)}:{p.n}")));
        }
        return sb.ToString();
    }

    /// <summary>Promedio de los relojes del CPU que parecen de núcleo (sin bus, "Effective", promedios ni anillo).</summary>
    private static double? AvgCpuClock(ISensor[] ss)
    {
        double sum = 0; int n = 0;
        foreach (var s in ss)
        {
            if (s.SensorType != SensorType.Clock || s.Value is not float v || !float.IsFinite(v) || v <= 0) continue;
            string name = s.Name;
            if (name.Contains("Bus", StringComparison.OrdinalIgnoreCase) || name.Contains("Effective", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Average", StringComparison.OrdinalIgnoreCase) || name.Contains("Ring", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Uncore", StringComparison.OrdinalIgnoreCase) || name.Contains("Memory", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Fabric", StringComparison.OrdinalIgnoreCase) || name.Contains("Graphics", StringComparison.OrdinalIgnoreCase))
                continue;
            sum += v; n++;
        }
        return n > 0 ? sum / n : null;
    }

    /// <summary>Primer sensor del tipo cuyo nombre contiene el texto (respaldo cuando el nombre exacto no existe).</summary>
    private static double? FirstContaining(ISensor[] ss, SensorType type, string text)
    {
        foreach (var s in ss)
            if (s.SensorType == type && s.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
                && s.Value is float v && float.IsFinite(v))
                return v;
        return null;
    }

    /// <summary>La temperatura más alta del CPU, sin las "distancias a TjMax" (que no son temperaturas).</summary>
    private static double? MaxCpuTemp(ISensor[] ss)
    {
        double? max = null;
        foreach (var s in ss)
            if (s.SensorType == SensorType.Temperature && !s.Name.Contains("Distance", StringComparison.Ordinal)
                && s.Value is float v && float.IsFinite(v) && v > 0 && (max is null || v > max))
                max = v;
        return max;
    }

    private void FillGpu(IHardware gpu, Snapshot snap)
    {
        snap.GpuName = gpu.Name;
        snap.GpuLoad = NamedV(gpu, SensorType.Load, "gpu core");
        snap.GpuTemp = NamedV(gpu, SensorType.Temperature, "gpu core");
        snap.GpuHotSpot = NamedV(gpu, SensorType.Temperature, "gpu hot spot");
        snap.GpuCoreClock = NamedV(gpu, SensorType.Clock, "gpu core");
        snap.GpuMemClock = NamedV(gpu, SensorType.Clock, "gpu memory");
        snap.GpuPower = NamedV(gpu, SensorType.Power, "gpu package") ?? FirstV(gpu, SensorType.Power)
                        ?? (gpu == _gpuNv ? _nvml.ReadWatts(gpu.Name) : null);
        snap.VramUsedMb = NamedV(gpu, SensorType.SmallData, "gpu memory used");
        snap.VramTotalMb = NamedV(gpu, SensorType.SmallData, "gpu memory total");
        snap.GpuFanRpm = FirstV(gpu, SensorType.Fan);
        snap.GpuFanPct = FirstV(gpu, SensorType.Control);

        // Respaldo de Windows (lo que muestra el Administrador de tareas) para lo que LHM no dé.
        if (snap.GpuTemp is null || snap.GpuFanRpm is null || snap.GpuPower is null)
        {
            var n = _native.Gpu(gpu.Name);
            if (n != null)
            {
                snap.GpuTemp ??= n.TempC;
                snap.GpuFanRpm ??= n.FanRpm;
                if (snap.GpuPower is null) snap.GpuPowerPct = n.PowerPct;
            }
        }
    }

    private void FillMemory(Snapshot snap)
    {
        if (_memHw != null)
        {
            snap.RamLoad = NamedV(_memHw, SensorType.Load, "memory");
            snap.RamUsedGb = NamedV(_memHw, SensorType.Data, "memory used");
            snap.RamAvailGb = NamedV(_memHw, SensorType.Data, "memory available");
        }
        if (_virtHw != null)
        {
            snap.VirtUsedGb = NamedV(_virtHw, SensorType.Data, "memory used");
            snap.VirtAvailGb = NamedV(_virtHw, SensorType.Data, "memory available");
        }
        else if (_memHw != null)
        {
            snap.VirtUsedGb = NamedV(_memHw, SensorType.Data, "virtual memory used");
            snap.VirtAvailGb = NamedV(_memHw, SensorType.Data, "virtual memory available");
        }
    }

    private void FillNetwork(Snapshot snap)
    {
        foreach (var n in _netAdapters)
        {
            var down = NamedV(n, SensorType.Throughput, "download speed");
            var up = NamedV(n, SensorType.Throughput, "upload speed");
            var dTot = NamedV(n, SensorType.Data, "data downloaded");
            var uTot = NamedV(n, SensorType.Data, "data uploaded");
            // Adaptadores virtuales sin tráfico acumulado no aportan nada.
            if ((dTot ?? 0) + (uTot ?? 0) < 0.001 && (down ?? 0) + (up ?? 0) < 1) continue;
            snap.Networks.Add(new NetReading(_hwKeys[n], n.Name, down, up, dTot, uTot));
        }
    }

    private void FillFans(Snapshot snap)
    {
        foreach (var h in _flat)
        {
            var ss = _ownSensors[h];
            for (int i = 0; i < ss.Length; i++)
            {
                var s = ss[i];
                if (s.SensorType == SensorType.Fan)
                    snap.Fans.Add(new FanReading(_sensorKeys[s], s.Name, V(s)));
            }
        }
    }

    private void FillDisks(Snapshot snap)
    {
        foreach (var d in _disks)
        {
            snap.Disks.Add(new DiskReading(
                _hwKeys[d],
                d.Name,
                FirstV(d, SensorType.Temperature),
                NamedV(d, SensorType.Load, "total activity"),
                NamedV(d, SensorType.Throughput, "read rate"),
                NamedV(d, SensorType.Throughput, "write rate"),
                NamedV(d, SensorType.Load, "used space"),
                ShortDiskName(d)));
        }
    }

    private readonly Dictionary<string, string> _diskNames = new();

    /// <summary>
    /// "NVMe C:", "SSD D:", "HDD E:"… El tipo sale de la clase con la que LibreHardwareMonitor detectó el disco
    /// y las letras de su lista de unidades lógicas (propiedad DriveInfos, leída por reflexión por si cambia).
    /// </summary>
    private string ShortDiskName(IHardware d)
    {
        var key = _hwKeys[d];
        if (_diskNames.TryGetValue(key, out var cached)) return cached;

        // Letras y tipo desde Windows (MSFT_Partition / MSFT_PhysicalDisk); NVMe lo dice el propio sensor.
        var info = DiskMap.Find(d.Name, key);
        string cls = d.GetType().Name;
        string kind = key.Contains("nvme", StringComparison.OrdinalIgnoreCase) || cls.Contains("NVMe", StringComparison.OrdinalIgnoreCase) ? "NVMe"
                    : info?.MediaType == 4 || cls.Contains("Ssd", StringComparison.OrdinalIgnoreCase) ? "SSD"
                    : info?.MediaType == 3 || cls.Contains("HardDisk", StringComparison.OrdinalIgnoreCase) ? "HDD"
                    : "Disk";
        string letters = info?.Letters ?? "";
        var name = letters.Length > 0 ? $"{kind} {letters}" : kind;
        if (letters.Length > 0) _diskNames[key] = name; // sin letra: se reintenta más tarde (la unidad puede montarse después)
        return name;
    }

    private void FillBattery(Snapshot snap)
    {
        if (_batHw == null) return;
        snap.BatteryCharge = NamedV(_batHw, SensorType.Level, "charge level");
        var charge = NamedV(_batHw, SensorType.Power, "charge rate");
        var discharge = NamedV(_batHw, SensorType.Power, "discharge rate");
        snap.BatteryRateW = charge is > 0 ? charge : discharge is > 0 ? -discharge : charge ?? discharge;
    }

    /// <summary>Texto con todos los sensores (modo --dump, para diagnóstico).</summary>
    public string Dump()
    {
        EnableExtras();
        var snap = Read()!;
        var sb = new StringBuilder();
        sb.AppendLine($"HwPulse dump {DateTime.Now:yyyy-MM-dd HH:mm:ss} · admin={snap.IsAdmin}");
        foreach (var g in snap.AllSensors.GroupBy(s => s.Hardware))
        {
            sb.AppendLine().AppendLine($"[{g.Key}]");
            foreach (var s in g)
                sb.AppendLine($"  {s.Type,-12} {s.Name,-40} {Fmt.Sensor(s.Type, s.Value)}");
        }
        sb.AppendLine().AppendLine("--- Resumen interpretado ---");
        sb.AppendLine($"CPU {snap.CpuName}: temp máx {snap.CpuTempMax}, reloj prom {snap.CpuClockAvg:F0} MHz, uso {snap.CpuLoad:F1} %, {snap.CpuPower:F1} W, núcleos {snap.Cores.Count}, TjMax dist {snap.TjDistanceMin}");
        sb.AppendLine($"GPU {snap.GpuName}: uso {snap.GpuLoad}, temp {snap.GpuTemp}, reloj {snap.GpuCoreClock}, {snap.GpuPower} W, VRAM {snap.VramUsedMb}/{snap.VramTotalMb} MB, fan {snap.GpuFanRpm} rpm");
        sb.AppendLine($"RAM: {snap.RamUsedGb:F1} GB usados, {snap.RamAvailGb:F1} GB libres ({snap.RamLoad:F0} %)");
        foreach (var d in snap.Disks) sb.AppendLine($"Disco {d.Name}: {d.TempC} °C, actividad {d.Activity:F0} %");
        sb.AppendLine($"Energía: AC={snap.OnAc}, batería {snap.BatteryCharge} %, tasa {snap.BatteryRateW} W");
        sb.AppendLine($"Windows counters: CPU clock {_native.CpuClockMhz()?.ToString("0") ?? "unavailable"} MHz; " +
                      string.Join("; ", _native.AllGpus().Select(g => $"GPU '{g.Name}' temp={g.TempC} fan={g.FanRpm} power%={g.PowerPct}")));
        if (_gpuNv != null) sb.AppendLine($"NVML (direct): {_nvml.ReadWatts(_gpuNv.Name)?.ToString("0.0") ?? "unavailable"} W");
        sb.AppendLine($"PawnIO: version={PawnIOInstaller.InstalledVersion()?.ToString() ?? "none"}, uninstallEntry={PawnIOInstaller.UninstallEntryExists()}, " +
                      $"service={PawnIOInstaller.ServiceState()}, device={PawnIOInstaller.DeviceAvailable()}");
        // El reporte de LibreHardwareMonitor explica qué detectó (familia del CPU, módulos, registros leídos).
        try { sb.AppendLine().AppendLine("--- LibreHardwareMonitor report ---").AppendLine(_pc.GetReport()); }
        catch (Exception ex) { sb.AppendLine("LHM report failed: " + ex.Message); }
        return sb.ToString();
    }

    /// <summary>--bench: cuánto tarda en abrirse (y en leerse por primera vez) cada grupo de sensores.</summary>
    public static string Bench()
    {
        var sb = new StringBuilder();
        var groups = new (string name, Action<Computer> on)[]
        {
            ("CPU", c => c.IsCpuEnabled = true),
            ("GPU", c => c.IsGpuEnabled = true),
            ("Memory", c => c.IsMemoryEnabled = true),
            ("Storage", c => c.IsStorageEnabled = true),
            ("Battery", c => c.IsBatteryEnabled = true),
            ("Network", c => c.IsNetworkEnabled = true),
        };
        foreach (var (name, on) in groups)
        {
            var c = new Computer();
            on(c);
            var sw = Stopwatch.StartNew();
            c.Open();
            long open = sw.ElapsedMilliseconds;
            sw.Restart();
            foreach (var h in c.Hardware.SelectMany(Flatten)) { try { h.Update(); } catch { } }
            long upd = sw.ElapsedMilliseconds;
            sb.AppendLine($"{name,-8} open {open,5} ms   first update {upd,5} ms   hardware {c.Hardware.Count}");
            c.Close();
        }
        return sb.ToString();
    }

    public void Dispose()
    {
        lock (_lock) { try { _pc.Close(); } catch { } _nvml.Dispose(); _native.Dispose(); }
    }
}
