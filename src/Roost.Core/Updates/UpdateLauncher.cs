using System.Diagnostics;
using System.Text;

namespace Roost.Core.Updates;

/// <summary>
/// Lanza el instalador ya verificado y deja un vigilante que vuelve a abrir la app si el
/// instalador no llega a actualizar. Los tests lo sustituyen (es <c>virtual</c>) para no
/// ejecutar nada.
/// <para>
/// Cómo vuelve la app: el setup de Inno arranca <b>sin elevar</b> (es su cargador), pide el
/// UAC desde dentro y, al terminar bien, relanza <c>roost-desktop.exe --updated</c> (entrada
/// <c>[Run]</c> de <c>build/roost.iss</c>). Si el usuario rechaza el UAC, o el instalador falla
/// después, su proceso sale con un código distinto de 0 y no relanza nada: para eso está el
/// vigilante, un PowerShell oculto que no tiene abierto ningún fichero de Roost, espera a ese
/// proceso y, si el código no es 0, abre la app con <c>--update-failed=&lt;código&gt;</c>.
/// </para>
/// </summary>
public class UpdateLauncher
{
    public static readonly UpdateLauncher Default = new();

    /// <summary>El argumento con el que vuelve la app si el instalador no actualizó (seguido del código de salida).</summary>
    public const string FailedArgumentPrefix = "--update-failed=";

    /// <summary>Lanza el setup con sus argumentos y devuelve el pid de su proceso. Lanza <see cref="RoostException"/> si no arranca.</summary>
    public virtual int StartSetup(string setupPath, string arguments)
    {
        try
        {
            // La carpeta de trabajo, la del propio setup: ni la de Roost (que el instalador va a sustituir) ni la de quien lo lanza.
            var start = new ProcessStartInfo(setupPath, arguments) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(setupPath)) };
            using var process = Process.Start(start)
                ?? throw new RoostException($"Windows no ha arrancado {Path.GetFileName(setupPath)}.");
            return process.Id;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            throw new RoostException($"No se pudo lanzar el instalador {Path.GetFileName(setupPath)}: {ex.Message} No se ha actualizado nada.", ex);
        }
    }

    /// <summary>
    /// El vigilante: espera al proceso <paramref name="setupPid"/> y relanza <paramref name="appExe"/>
    /// si sale con error. Es lo mejor que se puede hacer sin dejar la app abierta (tendría sus
    /// ficheros en uso): si no se puede arrancar, no es motivo para no actualizar, y el marcador
    /// de <see cref="UpdateMarker"/> dirá al abrir la app a mano que no se actualizó.
    /// </summary>
    public virtual void StartWatchdog(int setupPid, string appExe)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var script = WatchdogScript(setupPid, appExe);
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            var start = new ProcessStartInfo(PowerShellPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                // Nada abierto en la carpeta de Roost: ni siquiera como carpeta de trabajo.
                WorkingDirectory = Environment.SystemDirectory,
            };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded })
                start.ArgumentList.Add(arg);
            Process.Start(start)?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // Sin vigilante la app no vuelve sola si falla el UAC; el marcador lo cuenta al abrirla.
        }
    }

    /// <summary>
    /// Una terminal visible que ejecuta <paramref name="script"/> y se queda abierta (Scoop y
    /// winget: el usuario ve qué hace el gestor de paquetes). Lanza <see cref="RoostException"/> si no arranca.
    /// </summary>
    public virtual void StartTerminal(string script)
    {
        try
        {
            var start = new ProcessStartInfo(PowerShellPath) { UseShellExecute = true };
            foreach (var arg in new[] { "-NoProfile", "-NoExit", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
                start.ArgumentList.Add(arg);
            Process.Start(start)?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            throw new RoostException($"No se pudo abrir la terminal: {ex.Message}", ex);
        }
    }

    /// <summary>El PowerShell de Windows (siempre está): el vigilante no puede depender de nada instalado.</summary>
    public static string PowerShellPath => Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    /// <summary>
    /// PURO: el guion del vigilante. Coge el handle del proceso antes de esperar
    /// (<c>$p.Handle</c>): sin eso, PowerShell 5.1 no sabe el código de salida de un proceso que
    /// no es hijo suyo. Si el proceso ya no existe cuando arranca, no hace nada.
    /// </summary>
    public static string WatchdogScript(int setupPid, string appExe)
    {
        var exe = appExe.Replace("'", "''");
        return $$"""
            $p = $null
            try { $p = [System.Diagnostics.Process]::GetProcessById({{setupPid}}); $null = $p.Handle } catch { }
            if ($p -ne $null) {
              $p.WaitForExit()
              $c = $p.ExitCode
              if ($c -ne 0) { Start-Process -FilePath '{{exe}}' -ArgumentList ('{{FailedArgumentPrefix}}' + $c) }
            }
            """;
    }

    /// <summary>
    /// PURO: el código de salida que trae <c>--update-failed=N</c>, o null si el argumento no es ese.
    /// </summary>
    public static int? ParseFailedArgument(string? argument) =>
        argument is not null && argument.StartsWith(FailedArgumentPrefix, StringComparison.Ordinal)
        && int.TryParse(argument.AsSpan(FailedArgumentPrefix.Length), out var code)
            ? code
            : null;

    /// <summary>
    /// PURO: qué significa el código con el que salió el setup de Inno (sin tildes: lo enseña
    /// también la CLI). El 2 es el habitual: el UAC rechazado o cancelado antes de empezar.
    /// </summary>
    public static string DescribeSetupExit(int code) => code switch
    {
        1 => "el instalador no pudo arrancar (codigo 1)",
        2 => "Windows no dio permiso o se cancelo antes de empezar (codigo 2)",
        3 => "fallo al preparar la instalacion (codigo 3)",
        4 => "fallo mientras copiaba los ficheros (codigo 4)",
        5 => "se cancelo durante la instalacion (codigo 5)",
        7 or 8 => "la preparacion de la instalacion no pudo seguir (codigo " + code + ")",
        _ => $"el instalador salio con el codigo {code}",
    };
}
