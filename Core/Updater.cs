using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace HwPulse;

/// <summary>
/// Actualización automática desde GitHub Releases, pensada para la versión portable de un solo .exe:
/// descarga el nuevo HwPulse.exe junto al actual, comprueba su SHA-256 (publicado por GitHub Actions en la
/// misma release), renombra el actual a .old (Windows permite renombrar un .exe en uso), pone el nuevo en su
/// lugar y lo arranca. El .old se borra en el siguiente inicio.
/// </summary>
public static class Updater
{
    public const string Repo = "xjuanfxx/HwPulse";
    private const string DownloadPrefix = "https://github.com/" + Repo + "/releases/download/";

    public sealed record Release(Version Version, string Tag, string ExeUrl, string? ShaUrl, string PageUrl);

    public static Version Current
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    /// <summary>Un solo .exe (lo que se actualiza solo). La versión en carpeta (zip) solo abre la página.</summary>
    // En el exe de un solo archivo HwPulse.dll va dentro del .exe; en la versión en carpeta está al lado.
    // (Antes se miraba Assembly.Location vacío, que funciona pero el compilador lo marca como advertencia IL3000.)
    public static bool IsSingleFile => !File.Exists(Path.Combine(AppContext.BaseDirectory, "HwPulse.dll"));

    private static HttpClient NewClient(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"HwPulse/{Current}");
        return http;
    }

    /// <summary>La release más reciente si es más nueva que esta; null si ya está al día.</summary>
    public static async Task<Release?> CheckAsync(CancellationToken ct = default)
    {
        using var http = NewClient(TimeSpan.FromSeconds(30));
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        using var res = await http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest", ct);
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var ver)) return null;
        ver = new Version(ver.Major, ver.Minor, Math.Max(0, ver.Build));
        if (ver <= Current) return null;

        string? exe = null, sha = null;
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            string name = a.GetProperty("name").GetString() ?? "";
            string url = a.GetProperty("browser_download_url").GetString() ?? "";
            if (!url.StartsWith(DownloadPrefix, StringComparison.Ordinal)) continue; // solo descargas de este repo
            if (name.Equals("HwPulse.exe", StringComparison.OrdinalIgnoreCase)) exe = url;
            else if (name.Equals("HwPulse.exe.sha256", StringComparison.OrdinalIgnoreCase)) sha = url;
        }
        string page = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";
        return exe == null ? null : new Release(ver, tag, exe, sha, page);
    }

    /// <summary>Descarga, verifica y deja el nuevo .exe en su sitio. Después hay que arrancarlo y salir.</summary>
    public static async Task InstallAsync(Release r, CancellationToken ct = default)
    {
#if DEBUG
        await Task.CompletedTask;
        throw new InvalidOperationException("Debug builds don't self-update.");
#else
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("No process path.");
        string tmp = exe + ".new", old = exe + ".old";
        using var http = NewClient(TimeSpan.FromMinutes(5));

        await using (var src = await http.GetStreamAsync(r.ExeUrl, ct))
        await using (var dst = File.Create(tmp))
            await src.CopyToAsync(dst, ct);

        try
        {
            // Integridad: el hash lo publica el mismo flujo de GitHub Actions que compila el .exe.
            if (r.ShaUrl == null) throw new InvalidDataException("The release has no checksum.");
            string expected = (await http.GetStringAsync(r.ShaUrl, ct)).Trim().Split(' ', '\t', '\r', '\n')[0];
            string actual;
            await using (var f = File.OpenRead(tmp)) actual = Convert.ToHexString(await SHA256.HashDataAsync(f, ct));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Checksum mismatch.");

            if (File.Exists(old)) File.Delete(old);
            File.Move(exe, old);
            try { File.Move(tmp, exe); }
            catch { File.Move(old, exe); throw; } // deja la versión anterior como estaba
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
#endif
    }

    /// <summary>Arranca el .exe actualizado; esa instancia espera a que esta termine.</summary>
    public static void Relaunch(bool minimized)
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        psi.ArgumentList.Add("--updated");
        if (minimized) psi.ArgumentList.Add("--minimized");
        Process.Start(psi);
    }

    /// <summary>Borra el .exe anterior que dejó una actualización.</summary>
    public static void CleanupOld()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (exe != null && File.Exists(exe + ".old")) File.Delete(exe + ".old");
        }
        catch { /* aún en uso: se intentará la próxima vez */ }
    }
}
