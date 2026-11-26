using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HwPulse;

public sealed record ProcInfo(string Name, double CpuPct, long MemBytes, int Count,
                              double GpuPct = 0, long GpuMem = 0, double ReadBps = 0, double WriteBps = 0,
                              double NetDownBps = 0, double NetUpBps = 0);

/// <summary>
/// Uso por proceso: CPU (diferencia de TotalProcessorTime), memoria privada, lectura/escritura (contadores de E/S),
/// y, si se pasan, GPU y red. Los procesos con el mismo nombre (p. ej. los de un navegador) se suman.
/// </summary>
public sealed class ProcessMonitor
{
    private readonly Dictionary<int, TimeSpan> _lastCpu = new();
    private readonly Dictionary<int, (ulong read, ulong write)> _lastIo = new();
    private DateTime _lastTime = DateTime.MinValue;
    private readonly int _cores = Environment.ProcessorCount;

    /// <summary>
    /// Partes de Windows que aparecen como proceso pero no son programas: no se pueden cerrar ni hacer nada
    /// con ellas, así que no se listan (Memory Compression guarda memoria comprimida de otros programas).
    /// </summary>
    private static readonly HashSet<string> SystemPseudo = new(StringComparer.OrdinalIgnoreCase)
        { "Memory Compression", "Registry", "Secure System", "System", "Idle", "System Idle Process" };

    public List<ProcInfo> Sample(Dictionary<int, (double pct, long dedicated)>? gpu = null,
                                 Dictionary<int, (double down, double up)>? net = null)
    {
        var now = DateTime.UtcNow;
        double elapsedMs = _lastTime == DateTime.MinValue ? 0 : (now - _lastTime).TotalMilliseconds;
        _lastTime = now;

        var groups = new Dictionary<string, Acc>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<int>();

        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.Id == 0 || p.Id == 4 || SystemPseudo.Contains(p.ProcessName)) continue;
                    seen.Add(p.Id);
                    double cpu = 0;
                    try
                    {
                        var t = p.TotalProcessorTime;
                        if (elapsedMs > 0 && _lastCpu.TryGetValue(p.Id, out var prev))
                            cpu = (t - prev).TotalMilliseconds / (elapsedMs * _cores) * 100;
                        _lastCpu[p.Id] = t;
                    }
                    catch { /* sin acceso (procesos protegidos) */ }

                    // Memoria privada (la que muestra el Administrador de tareas). El "working set" normal
                    // incluye librerías compartidas: al sumar varios procesos del mismo programa (p. ej. los 20+
                    // de un navegador) se contaban repetidas y salía más del doble.
                    var (mem, io) = QueryProcess(p.Id);
                    if (mem <= 0) { try { mem = p.WorkingSet64; } catch { } }

                    // Lectura/escritura por segundo (toda la E/S del proceso, como "IO Read/Write Bytes/sec")
                    double rd = 0, wr = 0;
                    if (io is { } cur)
                    {
                        if (elapsedMs > 0 && _lastIo.TryGetValue(p.Id, out var prevIo))
                        {
                            rd = (cur.read >= prevIo.read ? cur.read - prevIo.read : 0) / (elapsedMs / 1000);
                            wr = (cur.write >= prevIo.write ? cur.write - prevIo.write : 0) / (elapsedMs / 1000);
                        }
                        _lastIo[p.Id] = cur;
                    }

                    string name = p.ProcessName + ".exe";
                    if (!groups.TryGetValue(name, out var a)) groups[name] = a = new Acc();
                    a.Cpu += Math.Max(cpu, 0); a.Mem += mem; a.Count++; a.Read += rd; a.Write += wr;
                    if (gpu != null && gpu.TryGetValue(p.Id, out var g)) { a.Gpu = Math.Max(a.Gpu, g.pct); a.GpuMem += g.dedicated; }
                    if (net != null && net.TryGetValue(p.Id, out var n)) { a.Down += n.down; a.Up += n.up; }
                }
                catch { /* el proceso terminó mientras lo leíamos */ }
            }
        }

        foreach (var dead in _lastCpu.Keys.Where(id => !seen.Contains(id)).ToList()) _lastCpu.Remove(dead);
        foreach (var dead in _lastIo.Keys.Where(id => !seen.Contains(id)).ToList()) _lastIo.Remove(dead);

        return groups.Select(kv => new ProcInfo(kv.Key, Math.Min(kv.Value.Cpu, 100), kv.Value.Mem, kv.Value.Count,
            kv.Value.Gpu, kv.Value.GpuMem, kv.Value.Read, kv.Value.Write, kv.Value.Down, kv.Value.Up)).ToList();
    }

    private sealed class Acc
    {
        public double Cpu, Gpu, Read, Write, Down, Up;
        public long Mem, GpuMem;
        public int Count;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessIoCounters(IntPtr process, out IO_COUNTERS counters);

    /// <summary>Memoria privada y bytes leídos/escritos, con un solo acceso (consulta limitada) al proceso.</summary>
    private static (long privateWs, (ulong read, ulong write)? io) QueryProcess(int pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return (0, null);
        try
        {
            var c = new PROCESS_MEMORY_COUNTERS_EX2 { cb = (uint)Marshal.SizeOf<PROCESS_MEMORY_COUNTERS_EX2>() };
            long mem = GetProcessMemoryInfo(h, ref c, c.cb) ? (long)c.PrivateWorkingSetSize : 0;
            (ulong, ulong)? io = GetProcessIoCounters(h, out var ioc) ? (ioc.ReadTransferCount, ioc.WriteTransferCount) : null;
            return (mem, io);
        }
        finally { CloseHandle(h); }
    }

    // ------------------------------------------------------------------ memoria privada
    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_MEMORY_COUNTERS_EX2
    {
        public uint cb, PageFaultCount;
        public nuint PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
                     QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage,
                     PrivateWorkingSetSize;
        public ulong SharedCommitUsage;
    }

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool GetProcessMemoryInfo(IntPtr process, ref PROCESS_MEMORY_COUNTERS_EX2 counters, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    /// <summary>Icono lineal según el tipo de programa (navegador, chat, juego…).</summary>
    public static string IconKey(string exe)
    {
        var n = exe.ToLowerInvariant().Replace(".exe", "");
        static bool Any(string n, params string[] k) => k.Any(x => n.Contains(x));
        // Primero los del sistema, para que "searchhost" no caiga en "arc" (navegador).
        if (Any(n, "svchost", "csrss", "dwm", "lsass", "services", "smss", "wininit", "winlogon", "msmpeng", "searchhost", "runtimebroker",
                "ctfmon", "sihost", "taskhostw", "registry", "hwpulse", "librehardwaremonitor", "memcompression") || n == "system")
            return "IcCpu";
        if (Any(n, "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "helium", "arc", "zen")) return "IcGlobe";
        if (Any(n, "discord", "telegram", "whatsapp", "slack", "teams", "zoom", "signal")) return "IcChat";
        if (Any(n, "fortnite", "steam", "epicgames", "genshin", "valorant", "riot", "minecraft", "javaw", "battle.net", "hoyoplay", "clashroyale", "eaapp", "ubisoft"))
            return "IcGamepad";
        if (Any(n, "spotify", "audiodg", "wavelink", "vlc", "mpc-hc", "harmonoid", "music", "fl64", "fl studio", "ableton")) return "IcMusic";
        if (Any(n, "code", "devenv", "claude", "node", "dotnet", "powershell", "pwsh", "terminal", "cmd", "python", "git", "conhost", "rider"))
            return "IcCode";
        if (n == "explorer") return "IcFolder";
        return "IcApp";
    }
}
