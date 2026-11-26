using System.Diagnostics;
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

    /// <summary>El servicio del driver está registrado (instalado), aunque esté detenido: lo arranca LHM.</summary>
    public static bool IsInstalled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO");
            return key != null;
        }
        catch { return true; } // sin poder leer el registro, no intentamos instalar nada
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
                0 => IsInstalled() ? Result.Installed : Result.Failed,
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
