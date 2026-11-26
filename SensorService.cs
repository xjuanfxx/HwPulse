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

    private static IEnumerable<IHardware> Flatten(IHardware h)
    {
        yield return h;
        foreach (var sub in h.SubHardware)
            foreach (var x in Flatten(sub))
                yield return x;
    }

    private static IEnumerable<ISensor> SensorsOf(IHardware h) => Flatten(h).SelectMany(x => x.Sensors);

    private static double? V(ISensor? s) => s?.Value is float f && !float.IsNaN(f) && !float.IsInfinity(f) ? f : null;
    private static double? V(float? f) => f is float x && !float.IsNaN(x) && !float.IsInfinity(x) ? x : null;

    private static ISensor? Find(IEnumerable<ISensor> ss, SensorType t, string name) =>
        ss.FirstOrDefault(s => s.SensorType == t && s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

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
        {
            foreach (var h in _pc.Hardware.SelectMany(Flatten))
            {
                try { h.Update(); } catch { /* un dispositivo que falla no debe tumbar el resto */ }
            }

            var snap = new Snapshot { IsAdmin = _isAdmin, OnAc = Native.IsOnAc() };

            var cpu = _pc.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
            if (cpu != null) FillCpu(cpu, snap);

            var gpu = _pc.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.GpuNvidia)
                   ?? _pc.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.GpuAmd)
                   ?? _pc.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.GpuIntel);
            if (gpu != null) FillGpu(gpu, snap);

            FillMemory(snap);
            FillDisks(snap);
            FillBattery(snap);
            FillNetwork(snap);
            FillFans(snap);

            foreach (var h in _pc.Hardware.SelectMany(Flatten))
                foreach (var s in h.Sensors)
                    snap.AllSensors.Add(new SensorReading(s.Identifier.ToString(), h.Name, s.Name, s.SensorType, V(s.Value), V(s.Min), V(s.Max)));

            return snap;
        }
    }

    private static void FillCpu(IHardware cpu, Snapshot snap)
    {
        var ss = SensorsOf(cpu).ToList();
        snap.CpuName = cpu.Name;

        var clocks = new Dictionary<int, double?>();
        var temps = new Dictionary<int, double?>();
        var loads = new Dictionary<int, List<double>>();

        foreach (var s in ss)
        {
            switch (s.SensorType)
            {
                case SensorType.Clock when CoreExact.Match(s.Name) is { Success: true } m:
                    clocks[int.Parse(m.Groups[1].Value)] = V(s);
                    break;
                case SensorType.Temperature when CoreExact.Match(s.Name) is { Success: true } m:
                    temps[int.Parse(m.Groups[1].Value)] = V(s);
                    break;
                case SensorType.Load when CoreLoad.Match(s.Name) is { Success: true } m:
                    if (V(s) is double l)
                    {
                        var i = int.Parse(m.Groups[1].Value);
                        if (!loads.TryGetValue(i, out var list)) loads[i] = list = new List<double>();
                        list.Add(l);
                    }
                    break;
            }
        }

        foreach (var i in clocks.Keys.Union(temps.Keys).Union(loads.Keys).OrderBy(i => i))
        {
            clocks.TryGetValue(i, out var c);
            temps.TryGetValue(i, out var t);
            double? l = loads.TryGetValue(i, out var list) && list.Count > 0 ? list.Average() : null;
            snap.Cores.Add(new CoreReading(i, c, t, l));
        }

        var validClocks = snap.Cores.Select(c => c.ClockMhz).OfType<double>().Where(c => c > 0).ToList();
        snap.CpuClockAvg = validClocks.Count > 0 ? validClocks.Average() : null;

        var coreTemps = snap.Cores.Select(c => c.TempC).OfType<double>().ToList();
        snap.CpuTempMax = V(Find(ss, SensorType.Temperature, "Core Max"))
                          ?? (coreTemps.Count > 0 ? (double?)coreTemps.Max() : null);
        snap.CpuTempPkg = V(Find(ss, SensorType.Temperature, "CPU Package"));
        snap.CpuLoad = V(Find(ss, SensorType.Load, "CPU Total"));
        snap.CpuCoreLoadMax = V(Find(ss, SensorType.Load, "CPU Core Max"));
        snap.CpuPower = V(Find(ss, SensorType.Power, "CPU Package"));
        snap.CpuVoltage = V(Find(ss, SensorType.Voltage, "CPU Core"));

        var dist = ss.Where(s => s.SensorType == SensorType.Temperature && s.Name.Contains("Distance to TjMax"))
                     .Select(V).OfType<double>().ToList();
        snap.TjDistanceMin = dist.Count > 0 ? dist.Min() : null;
    }

    private static void FillGpu(IHardware gpu, Snapshot snap)
    {
        var ss = SensorsOf(gpu).ToList();
        snap.GpuName = gpu.Name;
        snap.GpuLoad = V(Find(ss, SensorType.Load, "GPU Core"));
        snap.GpuTemp = V(Find(ss, SensorType.Temperature, "GPU Core"));
        snap.GpuHotSpot = V(Find(ss, SensorType.Temperature, "GPU Hot Spot"));
        snap.GpuCoreClock = V(Find(ss, SensorType.Clock, "GPU Core"));
        snap.GpuMemClock = V(Find(ss, SensorType.Clock, "GPU Memory"));
        snap.GpuPower = V(Find(ss, SensorType.Power, "GPU Package")) ?? V(ss.FirstOrDefault(s => s.SensorType == SensorType.Power));
        snap.VramUsedMb = V(Find(ss, SensorType.SmallData, "GPU Memory Used"));
        snap.VramTotalMb = V(Find(ss, SensorType.SmallData, "GPU Memory Total"));
        snap.GpuFanRpm = V(ss.FirstOrDefault(s => s.SensorType == SensorType.Fan));
        snap.GpuFanPct = V(ss.FirstOrDefault(s => s.SensorType == SensorType.Control));
    }

    private void FillMemory(Snapshot snap)
    {
        var mem = _pc.Hardware.Where(h => h.HardwareType == HardwareType.Memory && !h.Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase))
                              .SelectMany(SensorsOf)
                              .Where(s => !s.Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase))
                              .ToList();
        snap.RamLoad = V(Find(mem, SensorType.Load, "Memory"));
        snap.RamUsedGb = V(Find(mem, SensorType.Data, "Memory Used"));
        snap.RamAvailGb = V(Find(mem, SensorType.Data, "Memory Available"));

        var virt = _pc.Hardware.Where(h => h.HardwareType == HardwareType.Memory).SelectMany(SensorsOf).ToList();
        var vh = _pc.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Memory && h.Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase));
        if (vh != null)
        {
            var vs = SensorsOf(vh).ToList();
            snap.VirtUsedGb = V(Find(vs, SensorType.Data, "Memory Used"));
            snap.VirtAvailGb = V(Find(vs, SensorType.Data, "Memory Available"));
        }
        else
        {
            snap.VirtUsedGb = V(Find(virt, SensorType.Data, "Virtual Memory Used"));
            snap.VirtAvailGb = V(Find(virt, SensorType.Data, "Virtual Memory Available"));
        }
    }

    private void FillNetwork(Snapshot snap)
    {
        foreach (var n in _pc.Hardware.Where(h => h.HardwareType == HardwareType.Network))
        {
            // "Wi-Fi-QoS Packet Scheduler-0000" y similares son filtros del mismo adaptador:
            // repiten su tráfico y duplicarían el total.
            if (FilterAdapter.IsMatch(n.Name)) continue;
            var ss = SensorsOf(n).ToList();
            var down = V(Find(ss, SensorType.Throughput, "Download Speed"));
            var up = V(Find(ss, SensorType.Throughput, "Upload Speed"));
            var dTot = V(Find(ss, SensorType.Data, "Data Downloaded"));
            var uTot = V(Find(ss, SensorType.Data, "Data Uploaded"));
            // Adaptadores virtuales sin tráfico acumulado no aportan nada.
            if ((dTot ?? 0) + (uTot ?? 0) < 0.001 && (down ?? 0) + (up ?? 0) < 1) continue;
            snap.Networks.Add(new NetReading(n.Identifier.ToString(), n.Name, down, up, dTot, uTot));
        }
    }

    private void FillFans(Snapshot snap)
    {
        foreach (var h in _pc.Hardware.SelectMany(Flatten))
            foreach (var s in h.Sensors.Where(s => s.SensorType == SensorType.Fan))
                snap.Fans.Add(new FanReading(s.Identifier.ToString(), $"{s.Name}", V(s)));
    }

    private void FillDisks(Snapshot snap)
    {
        foreach (var d in _pc.Hardware.Where(h => h.HardwareType == HardwareType.Storage))
        {
            var ss = SensorsOf(d).ToList();
            snap.Disks.Add(new DiskReading(
                d.Identifier.ToString(),
                d.Name,
                V(ss.FirstOrDefault(s => s.SensorType == SensorType.Temperature)),
                V(Find(ss, SensorType.Load, "Total Activity")),
                V(Find(ss, SensorType.Throughput, "Read Rate")),
                V(Find(ss, SensorType.Throughput, "Write Rate")),
                V(Find(ss, SensorType.Load, "Used Space")),
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
        var key = d.Identifier.ToString();
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
        var bat = _pc.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Battery);
        if (bat == null) return;
        var ss = SensorsOf(bat).ToList();
        snap.BatteryCharge = V(Find(ss, SensorType.Level, "Charge Level"));
        var charge = V(Find(ss, SensorType.Power, "Charge Rate"));
        var discharge = V(Find(ss, SensorType.Power, "Discharge Rate"));
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
