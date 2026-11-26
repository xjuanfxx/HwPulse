using System.Management;

namespace HwPulse;

/// <summary>
/// Relaciona cada disco físico con sus letras de unidad y su tipo, usando el almacenamiento de Windows
/// (MSFT_Partition / MSFT_PhysicalDisk). Se refresca como mucho cada 30 s (los discos USB van y vienen).
/// </summary>
internal static class DiskMap
{
    public sealed record Disk(int Number, string FriendlyName, string Letters, int MediaType, int BusType);

    private static List<Disk> _disks = new();
    private static DateTime _loaded = DateTime.MinValue;
    private static readonly object Lock = new();

    public static IReadOnlyList<Disk> All
    {
        get
        {
            lock (Lock)
            {
                if ((DateTime.UtcNow - _loaded).TotalSeconds > 30) { _disks = Load(); _loaded = DateTime.UtcNow; }
                return _disks;
            }
        }
    }

    private static List<Disk> Load()
    {
        var result = new List<Disk>();
        try
        {
            var letters = new Dictionary<int, List<string>>();
            // Cada ManagementObject envuelve un objeto COM: se liberan al terminar (using) en vez de esperar al GC.
            using (var q = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", "SELECT DiskNumber, DriveLetter FROM MSFT_Partition"))
            using (var rows = q.Get())
                foreach (ManagementObject p in rows)
                using (p)
                {
                    if (p["DriveLetter"] is char c && c != '\0')
                    {
                        int n = Convert.ToInt32(p["DiskNumber"]);
                        if (!letters.TryGetValue(n, out var l)) letters[n] = l = new List<string>();
                        l.Add($"{char.ToUpperInvariant(c)}:");
                    }
                }
            using (var q = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", "SELECT DeviceId, FriendlyName, MediaType, BusType FROM MSFT_PhysicalDisk"))
            using (var rows = q.Get())
                foreach (ManagementObject d in rows)
                using (d)
                {
                    if (!int.TryParse(d["DeviceId"]?.ToString(), out int n)) continue;
                    letters.TryGetValue(n, out var l);
                    result.Add(new Disk(n, d["FriendlyName"]?.ToString() ?? "", string.Join(" ", (l ?? new()).OrderBy(x => x)),
                        Convert.ToInt32(d["MediaType"] ?? 0), Convert.ToInt32(d["BusType"] ?? 0)));
                }
        }
        catch { /* WMI no disponible: los discos se muestran sin letra */ }
        return result;
    }

    /// <summary>Busca el disco por nombre (lo más fiable) y, si no, por el número al final del identificador del sensor.</summary>
    public static Disk? Find(string sensorName, string identifier)
    {
        var all = All;
        var name = sensorName.Trim();
        var byName = all.FirstOrDefault(d => d.FriendlyName.Length > 0 &&
            (d.FriendlyName.Equals(name, StringComparison.OrdinalIgnoreCase)
             || d.FriendlyName.Contains(name, StringComparison.OrdinalIgnoreCase)
             || name.Contains(d.FriendlyName, StringComparison.OrdinalIgnoreCase)));
        if (byName != null) return byName;
        var last = identifier.TrimEnd('/').Split('/').LastOrDefault();
        return int.TryParse(last, out int idx) ? all.FirstOrDefault(d => d.Number == idx) : null;
    }
}
