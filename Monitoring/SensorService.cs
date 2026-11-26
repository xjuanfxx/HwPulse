using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using LibreHardwareMonitor.Hardware;

namespace HwPulse;

public sealed record CoreReading(int Index, double? ClockMhz, double? TempC, double? Load);
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
    private static readonly Regex CoreExact = new(@"^CPU Core #(\d+)$", RegexOptions.Compiled);
    private static readonly Regex CoreLoad = new(@"^CPU Core #(\d+)( Thread #\d+)?$", RegexOptions.Compiled);
    private static readonly Regex FilterAdapter = new(@"-\d{4}$", RegexOptions.Compiled);

    private readonly Computer _pc;
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
    private int[] _coreOrder = Array.Empty<int>();
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
    private static (CoreRole role, int core) ParseCoreRole(ISensor s)
    {
        switch (s.SensorType)
        {
            case SensorType.Clock when CoreExact.Match(s.Name) is { Success: true } m:
                return (CoreRole.Clock, int.Parse(m.Groups[1].Value));
            case SensorType.Temperature when CoreExact.Match(s.Name) is { Success: true } m:
                return (CoreRole.Temp, int.Parse(m.Groups[1].Value));
            case SensorType.Load when CoreLoad.Match(s.Name) is { Success: true } m:
                return (CoreRole.Load, int.Parse(m.Groups[1].Value));
            case SensorType.Temperature when s.Name.Contains("Distance to TjMax"):
                return (CoreRole.Tj, 0);
        }
        return (CoreRole.None, 0);
    }

    private void FillCpu(IHardware cpu, Snapshot snap)
    {
        snap.CpuName = cpu.Name;
        var ss = _flattenedSensors[cpu];

        _clocks.Clear();
        _temps.Clear();
        foreach (var list in _loads.Values) list.Clear();
        double? tjMin = null;

        foreach (var s in ss)
        {
            if (!_coreRoles.TryGetValue(s, out var role))
                _coreRoles[s] = role = ParseCoreRole(s);
            switch (role.role)
            {
                case CoreRole.Clock: _clocks[role.core] = V(s); break;
                case CoreRole.Temp: _temps[role.core] = V(s); break;
                case CoreRole.Load:
                    if (V(s) is double l)
                    {
                        if (!_loads.TryGetValue(role.core, out var list)) _loads[role.core] = list = new List<double>(2);
                        list.Add(l);
                    }
                    break;
                case CoreRole.Tj:
                    if (V(s) is double d && (tjMin is null || d < tjMin)) tjMin = d;
                    break;
            }
        }
        snap.TjDistanceMin = tjMin;

        // El conjunto de núcleos solo cambia cuando cambia el hardware: el orden se cachea con el mapa.
        if (_coreOrderVersion != _mapVersion)
        {
            var set = new HashSet<int>();
            foreach (var s in ss)
                if (_coreRoles.TryGetValue(s, out var r) && r.role is CoreRole.Clock or CoreRole.Temp or CoreRole.Load)
                    set.Add(r.core);
            _coreOrder = set.ToArray();
            Array.Sort(_coreOrder);
            _coreOrderVersion = _mapVersion;
        }

        double clockSum = 0;
        int clockCount = 0;
        double? coreTempMax = null;
        foreach (var i in _coreOrder)
        {
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
            snap.Cores.Add(new CoreReading(i, c, t, l));
        }
        snap.CpuClockAvg = clockCount > 0 ? clockSum / clockCount : null;

        snap.CpuTempMax = NamedV(cpu, SensorType.Temperature, "core max") ?? coreTempMax;
        snap.CpuTempPkg = NamedV(cpu, SensorType.Temperature, "cpu package");
        snap.CpuLoad = NamedV(cpu, SensorType.Load, "cpu total");
        snap.CpuCoreLoadMax = NamedV(cpu, SensorType.Load, "cpu core max");
        snap.CpuPower = NamedV(cpu, SensorType.Power, "cpu package");
        snap.CpuVoltage = NamedV(cpu, SensorType.Voltage, "cpu core");
    }

    private void FillGpu(IHardware gpu, Snapshot snap)
    {
        snap.GpuName = gpu.Name;
        snap.GpuLoad = NamedV(gpu, SensorType.Load, "gpu core");
        snap.GpuTemp = NamedV(gpu, SensorType.Temperature, "gpu core");
        snap.GpuHotSpot = NamedV(gpu, SensorType.Temperature, "gpu hot spot");
        snap.GpuCoreClock = NamedV(gpu, SensorType.Clock, "gpu core");
        snap.GpuMemClock = NamedV(gpu, SensorType.Clock, "gpu memory");
        snap.GpuPower = NamedV(gpu, SensorType.Power, "gpu package") ?? FirstV(gpu, SensorType.Power);
        snap.VramUsedMb = NamedV(gpu, SensorType.SmallData, "gpu memory used");
        snap.VramTotalMb = NamedV(gpu, SensorType.SmallData, "gpu memory total");
        snap.GpuFanRpm = FirstV(gpu, SensorType.Fan);
        snap.GpuFanPct = FirstV(gpu, SensorType.Control);
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
        lock (_lock) { try { _pc.Close(); } catch { } }
    }
}
