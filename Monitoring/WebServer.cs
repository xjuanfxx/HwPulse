using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace HwPulse;

/// <summary>
/// Panel web de solo lectura: sirve una página (Web/index.html) y los datos en /api/stats.
/// Por defecto solo escucha en este equipo (localhost). Con "acceso desde otros dispositivos" escucha en
/// toda la red local; eso requiere administrador (reserva de URL de http.sys) y una regla de firewall.
/// </summary>
public sealed class WebServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Dictionary<string, (byte[] data, string type)> _files;
    private static readonly byte[] EmptyJson = "{}"u8.ToArray();
    private byte[] _json = EmptyJson;
    private DateTime _lastPollUtc = DateTime.MinValue; // último /api/stats (navegador sin EventSource)

    public int Port { get; }
    public bool LanActive { get; private set; }
    public string Url => $"http://localhost:{Port}/";
    public string? LanUrl => LanActive && LanAddress() is string ip ? $"http://{ip}:{Port}/" : null;

    private WebServer(int port, Dictionary<string, (byte[], string)> files)
    {
        Port = port;
        _files = files;
    }

    /// <summary>
    /// Dirección corta para escribir en el teléfono: solo la IP (puerto 80, el que el navegador usa por defecto).
    /// null si el puerto 80 está ocupado por otro programa.
    /// </summary>
    public string? ShortUrl => LanActive && Port80 && LanAddress() is string ip ? ip : null;
    public bool Port80 { get; private set; }

    public static WebServer? TryStart(int port, bool lan, Dictionary<string, (byte[], string)> files)
    {
        var s = new WebServer(port, files);
        if (lan)
        {
            // En red: el puerto normal y además el 80, para que baste con escribir la IP.
            if (port != 80 && s.TryListen($"http://+:{port}/", "http://+:80/")) { s.LanActive = true; s.Port80 = true; }
            else if (s.TryListen($"http://+:{port}/")) { s.LanActive = true; s.Port80 = port == 80; }
        }
        if (!s.LanActive && !s.TryListen($"http://localhost:{port}/")) return null;
        _ = s.Loop();
        return s;
    }

    private bool TryListen(params string[] prefixes)
    {
        try
        {
            _listener.Prefixes.Clear();
            foreach (var p in prefixes) _listener.Prefixes.Add(p);
            _listener.Start();
            return true;
        }
        catch (HttpListenerException) // sin permiso o puerto ocupado
        {
            try { _listener.Stop(); } catch { }
            return false;
        }
    }

    // Clientes conectados a /api/stream (Server-Sent Events): reciben cada lectura en el mismo instante
    // en que la app la muestra, en vez de preguntar cada segundo y quedarse una lectura atrás.
    // Cada cliente tiene como mucho UN envío en curso: si un teléfono va lento (o se quedó colgado sin
    // cerrar la conexión), sus lecturas intermedias se descartan en vez de acumularse en memoria, y si un
    // envío tarda más de WriteTimeout se le desconecta (el navegador se reconecta solo con "retry").
    private sealed class StreamClient(HttpListenerResponse res)
    {
        public readonly HttpListenerResponse Res = res;
        public int Busy; // 0 = libre, 1 = enviando (Interlocked)
    }
    private readonly List<StreamClient> _streams = new();
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Se llama una vez por segundo con la lectura más reciente ya serializada.</summary>
    public void SetJson(string json)
    {
        _json = Encoding.UTF8.GetBytes(json);
        StreamClient[] clients;
        lock (_streams) clients = _streams.ToArray();
        if (clients.Length == 0) return;
        var frame = Encoding.UTF8.GetBytes("data: " + json + "\n\n");
        foreach (var c in clients)
        {
            // Aún enviando la anterior: se salta esta lectura (la siguiente ya trae datos más nuevos).
            if (Interlocked.Exchange(ref c.Busy, 1) == 1) continue;
            _ = SendAsync(c, frame); // fuera del hilo de la interfaz: un teléfono lento no frena la app
        }
    }

    private async Task SendAsync(StreamClient c, byte[] frame)
    {
        try
        {
            var write = WriteFrameAsync(c.Res, frame);
            if (await Task.WhenAny(write, Task.Delay(WriteTimeout)) != write) throw new TimeoutException();
            await write; // propaga el error si la escritura falló
        }
        catch { Drop(c); }
        finally { Volatile.Write(ref c.Busy, 0); }
    }

    private static async Task WriteFrameAsync(HttpListenerResponse res, byte[] frame)
    {
        await res.OutputStream.WriteAsync(frame);
        await res.OutputStream.FlushAsync();
    }

    private void Drop(StreamClient c)
    {
        lock (_streams) _streams.Remove(c);
        try { c.Res.Abort(); } catch { }
    }

    /// <summary>Hay algún cliente mirando el panel ahora mismo: un SSE conectado o un /api/stats reciente
    /// (el navegador que no soporta EventSource y pregunta cada segundo).</summary>
    public bool HasClients
    {
        get
        {
            lock (_streams)
                return _streams.Count > 0 || (DateTime.UtcNow - _lastPollUtc).TotalSeconds < 10;
        }
    }

    /// <summary>Hay que serializar la lectura nueva: hay algún cliente conectado (SSE o polling reciente),
    /// o todavía no se ha enviado ninguna lectura. Evita construir el JSON cada segundo cuando no hay
    /// nadie mirando el panel (viene activado por defecto).</summary>
    public bool NeedsJson => HasClients || ReferenceEquals(_json, EmptyJson);

    private const int MaxStreams = 16; // límite de pantallas conectadas a la vez (evita agotar recursos)

    private void OpenStream(HttpListenerResponse res)
    {
        lock (_streams)
        {
            if (_streams.Count >= MaxStreams) { res.StatusCode = 503; res.Close(); return; }
        }
        res.ContentType = "text/event-stream; charset=utf-8";
        res.Headers["Cache-Control"] = "no-store";
        res.SendChunked = true;
        var first = Encoding.UTF8.GetBytes("retry: 2000\ndata: " + Encoding.UTF8.GetString(_json) + "\n\n");
        var client = new StreamClient(res) { Busy = 1 };
        lock (_streams) _streams.Add(client);
        _ = SendAsync(client, first); // con el mismo límite de tiempo que el resto de envíos
    }

    private async Task Loop()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { break; } // detenido
            _ = Task.Run(() => Handle(ctx));
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        try
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "/";
            var res = ctx.Response;
            res.Headers["X-Content-Type-Options"] = "nosniff";
            if (ctx.Request.HttpMethod != "GET") { res.StatusCode = 405; res.Close(); return; }
            // Protección contra "DNS rebinding": una página web maliciosa podría hacer que el navegador
            // pida datos a este servidor usando su propio dominio. Solo se aceptan nombres conocidos.
            if (!IsAllowedHost(ctx.Request.Url?.Host)) { res.StatusCode = 403; res.Close(); return; }
            res.Headers["Referrer-Policy"] = "no-referrer";
            res.Headers["X-Frame-Options"] = "DENY";

            if (path == "/api/stream") { OpenStream(res); return; } // queda abierta; no se cierra aquí

            byte[] body;
            if (path == "/api/stats")
            {
                lock (_streams) _lastPollUtc = DateTime.UtcNow; // mantiene viva la serialización del bucle
                body = _json;
                res.ContentType = "application/json; charset=utf-8";
                res.Headers["Cache-Control"] = "no-store";
            }
            else if (_files.TryGetValue(path == "/" ? "/index.html" : path, out var f))
            {
                body = f.data;
                res.ContentType = f.type;
                res.Headers["Cache-Control"] = path.StartsWith("/fonts/") ? "public, max-age=86400" : "no-cache";
            }
            else
            {
                res.StatusCode = 404;
                body = "Not found"u8.ToArray();
            }
            res.ContentLength64 = body.Length;
            res.OutputStream.Write(body);
            res.Close();
        }
        catch { /* el cliente cerró la conexión */ }
    }

    private static bool IsAllowedHost(string? host)
    {
        if (string.IsNullOrEmpty(host)) return false;
        host = host.Trim('[', ']');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host == "127.0.0.1" || host == "::1") return true;
        if (host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)) return true;
        // Cualquier IP literal (la del equipo en la red local). Un dominio desconocido se rechaza.
        return IPAddress.TryParse(host, out _);
    }

    /// <summary>IPv4 de la red local (la del adaptador con puerta de enlace).</summary>
    public static string? LanAddress()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var props = ni.GetIPProperties();
                if (props.GatewayAddresses.Count == 0) continue;
                var ip = props.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
                if (ip != null) return ip.Address.ToString();
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Abre o cierra el puerto en el firewall de Windows, solo para redes privadas (casa), nunca públicas.
    /// Se ejecuta únicamente cuando el usuario activa o desactiva la opción en Ajustes.
    /// </summary>
    public static void SetFirewallRule(bool allow, int port)
    {
        const string name = "HwPulse web dashboard";
        string args = allow
            ? $"advfirewall firewall add rule name=\"{name}\" dir=in action=allow protocol=TCP localport={port},80 profile=private"
            : $"advfirewall firewall delete rule name=\"{name}\"";
        try
        {
            if (allow) RunNetsh($"advfirewall firewall delete rule name=\"{name}\""); // evitar duplicados
            RunNetsh(args);
        }
        catch { }
    }

    private static void RunNetsh(string args)
    {
        using var p = Process.Start(new ProcessStartInfo("netsh", args) { CreateNoWindow = true, UseShellExecute = false });
        p?.WaitForExit(5000);
    }

    public void Dispose()
    {
        lock (_streams)
        {
            foreach (var c in _streams) { try { c.Res.Abort(); } catch { } }
            _streams.Clear();
        }
        try { _listener.Stop(); _listener.Close(); } catch { }
    }
}
