using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace HwPulse;

/// <summary>
/// Registro de errores en %AppData%\HwPulse\errors.log (máx. ~512 KB, se recorta solo).
/// Los errores de la interfaz se anotan y la app sigue funcionando en vez de cerrarse;
/// un error en un hilo de fondo se anota y no tumba la lectura de sensores.
/// </summary>
internal static class ErrorLog
{
    private const long MaxBytes = 512 * 1024;
    private static readonly object Lock = new();

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HwPulse", "errors.log");

    public static void Install(Application app)
    {
        app.DispatcherUnhandledException += (_, e) =>
        {
            Write("UI", e.Exception);
            e.Handled = true; // seguir funcionando: un fallo puntual de dibujo no debe cerrar la app
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Write("Fatal", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Write("Task", e.Exception); e.SetObserved(); };
    }

    /// <summary>Anota una línea de diagnóstico (no es un error de la app, pero ayuda a explicar datos que faltan).</summary>
    public static void Info(string where, string message) => Append($"{where}: {message}");

    public static void Write(string where, Exception? ex)
    {
        if (ex == null) return;
        Append($"{where}: {ex}");
    }

    private static void Append(string text)
    {
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > MaxBytes) File.WriteAllText(FilePath, ""); // empezar de cero
                File.AppendAllText(FilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}\n\n");
            }
        }
        catch { /* el registro nunca debe provocar otro error */ }
    }
}
