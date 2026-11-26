using System.Diagnostics;
using System.IO;
using System.Security;

namespace HwPulse;

/// <summary>
/// "Iniciar con Windows" mediante una tarea programada al iniciar sesión con privilegios elevados.
/// La clave Run del registro no sirve: Windows omite en silencio las apps que piden administrador.
/// Se importa como XML para poder permitir que arranque con batería (schtasks /SC no lo permite).
/// </summary>
public static class Autostart
{
    private const string TaskName = "HwPulse";

    public static bool IsEnabled() => Run("/Query", "/TN", TaskName) == 0;

    /// <summary>Crea o quita la tarea. Devuelve false si falló (por ejemplo, sin administrador).</summary>
    public static bool Set(bool on)
    {
        if (!on) return Run("/Delete", "/TN", TaskName, "/F") == 0 || !IsEnabled();
        string? exe = Environment.ProcessPath;
        if (exe == null) return false;
        string user = SecurityElement.Escape(Environment.UserDomainName + "\\" + Environment.UserName);
        string xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Starts HwPulse in the tray when you sign in.</Description></RegistrationInfo>
              <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId><Delay>PT5S</Delay></LogonTrigger></Triggers>
              <Principals><Principal id="Author"><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author"><Exec><Command>{SecurityElement.Escape(exe)}</Command><Arguments>--minimized</Arguments></Exec></Actions>
            </Task>
            """;
        string tmp = Path.Combine(Path.GetTempPath(), $"hwpulse-task-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(tmp, xml, System.Text.Encoding.Unicode);
            return Run("/Create", "/TN", TaskName, "/XML", tmp, "/F") == 0;
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    private static int Run(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"))
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
            if (!p.WaitForExit(10000)) { p.Kill(); return -1; }
            return p.ExitCode;
        }
        catch (Exception ex) { ErrorLog.Write("Autostart", ex); return -1; }
    }
}
