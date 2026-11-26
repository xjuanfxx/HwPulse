using System.Diagnostics;
using System.Runtime.InteropServices;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace HwPulse;

/// <summary>
/// PawnIO es el driver que usa LibreHardwareMonitorLib para leer temperatura, reloj y potencia del CPU.
/// La librería NO lo instala: si falta, esos valores quedan vacíos sin ningún error. Aquí se detecta y se
/// instala con el instalador oficial del autor (namazso), en modo silencioso.
///
/// Seguridad (lo ejecuta un proceso de administrador):
/// - URL y SHA-256 fijos de una versión concreta (los mismos que publica el manifiesto de winget):
///   si el archivo descargado no coincide byte a byte, no se ejecuta.
/// - Mientras se comprueba y se ejecuta, el archivo queda abierto sin permiso de escritura ni borrado
///   para otros procesos, así nadie puede cambiarlo entre la comprobación y la ejecución.
/// </summary>
public static class PawnIOInstaller
{
    private const string Version = "2.2.0";
    private const string Url = "https://github.com/namazso/PawnIO.Setup/releases/download/" + Version + "/PawnIO_setup.exe";
    private const string Sha256 = "1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032";
    public const string ManualUrl = "https://pawnio.eu/";

    public enum Result { Installed, RebootRequired, Failed }

    // LibreHardwareMonitorLib 0.9.6 decide si hay PawnIO mirando la clave de desinstalación y luego abre
    // el dispositivo del driver directamente. Si el servicio existe pero el driver no está cargado (detenido,
    // instalación rota), no hay dispositivo: sin temperatura ni reloj del CPU, y sin ningún error.
    private const string DevicePath = @"\\?\GLOBALROOT\Device\PawnIO";

    /// <summary>El servicio del driver está registrado en Windows.</summary>
    public static bool ServiceExists() => KeyExists(@"SYSTEM\CurrentControlSet\Services\PawnIO");

    /// <summary>Hay una instalación registrada (lo que mira LHM).</summary>
    public static bool UninstallEntryExists() => KeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");

    private static bool KeyExists(string path)
    {
        try { using var key = Registry.LocalMachine.OpenSubKey(path); return key != null; }
        catch { return false; }
    }

    /// <summary>El driver está cargado y su dispositivo existe (lo que de verdad necesita LHM).</summary>
    public static bool DeviceAvailable()
    {
        // Solo se piden atributos: basta para saber si el dispositivo existe, sin usarlo.
        var h = CreateFile(DevicePath, 0x80 /* FILE_READ_ATTRIBUTES */, 3, IntPtr.Zero, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero);
        if (h != new IntPtr(-1)) { CloseHandle(h); return true; }
        int err = Marshal.GetLastWin32Error();
        return err is not (2 or 3 or 161); // no encontrado / ruta no encontrada / ruta inválida
    }

    /// <summary>Está bien instalado y funcionando: no hace falta hacer nada.</summary>
    public static bool IsWorking() => UninstallEntryExists() && DeviceAvailable();

    /// <summary>Estado del servicio según sc.exe (RUNNING, STOPPED…), o "missing"/"unknown".</summary>
    public static string ServiceState()
    {
        if (!ServiceExists()) return "missing";
        try
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"), "query PawnIO")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            using var p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            foreach (var line in output.Split('\n'))
                if (line.Contains("STATE", StringComparison.Ordinal))
                    return line.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? "unknown";
        }
        catch { }
        return "unknown";
    }

    /// <summary>
    /// Si el servicio existe pero el driver no está cargado, lo arranca (sc start) y espera a que aparezca
    /// el dispositivo. Se llama ANTES de abrir los sensores, porque LHM solo mira PawnIO al abrir.
    /// </summary>
    public static void TryStartDriver()
    {
        try
        {
            if (DeviceAvailable() || !ServiceExists()) return;
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"), "start PawnIO")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            using (var p = Process.Start(psi)!) { p.StandardOutput.ReadToEnd(); p.WaitForExit(5000); }
            for (int i = 0; i < 30 && !DeviceAvailable(); i++) Thread.Sleep(100);
        }
        catch (Exception ex) { ErrorLog.Write("PawnIO start", ex); }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

    /// <summary>Tras instalar, el driver puede tardar un momento en crear su dispositivo.</summary>
    private static bool WaitWorking()
    {
        TryStartDriver();
        for (int i = 0; i < 30 && !IsWorking(); i++) Thread.Sleep(100);
        return IsWorking();
    }

    public static async Task<Result> InstallAsync(CancellationToken ct = default)
    {
        string dir = Path.Combine(Path.GetTempPath(), "HwPulse-PawnIO-" + Guid.NewGuid().ToString("N"));
        string exe = Path.Combine(dir, "PawnIO_setup.exe");
        Directory.CreateDirectory(dir);
        try
        {
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd($"HwPulse/{Updater.Current}");
                await using var src = await http.GetStreamAsync(Url, ct);
                await using var dst = File.Create(exe);
                await src.CopyToAsync(dst, ct);
            }

            // Abierto en solo lectura y sin compartir escritura/borrado hasta que el instalador termine.
            await using var locked = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.Read);
            string hash = Convert.ToHexString(await SHA256.HashDataAsync(locked, ct));
            if (!hash.Equals(Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"PawnIO installer checksum mismatch ({hash}).");

            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = dir };
            psi.ArgumentList.Add("-install");
            psi.ArgumentList.Add("-silent");
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("Installer did not start.");
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(TimeSpan.FromMinutes(3));
                try { await p.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException) { try { p.Kill(); } catch { } throw new TimeoutException("PawnIO installer timed out."); }
            }
            // 0 = instalado; 3010 = ERROR_SUCCESS_REBOOT_REQUIRED (instalado, falta reiniciar).
            return p.ExitCode switch
            {
                0 => WaitWorking() ? Result.Installed : Result.Failed,
                3010 => Result.RebootRequired,
                _ => throw new InvalidOperationException($"PawnIO installer exit code {p.ExitCode}."),
            };
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
