using System.Text;
using System.Xml.Linq;
using Roost.Core.Scanning;

namespace Roost.Core.Platform;

/// <summary>Cómo está la tarea al iniciar sesión (E4).</summary>
/// <param name="Exists">La tarea está en el Programador de tareas.</param>
/// <param name="Enabled">Y está activada (se puede desactivar a mano en el Programador).</param>
/// <param name="Command">Lo que lanza (su ejecutable).</param>
/// <param name="Arguments">Con qué argumentos.</param>
/// <param name="Error">Por qué no se ha podido preguntar (sin tildes, como la CLI); null si se pudo.</param>
public sealed record LogonTaskStatus(bool Exists, bool Enabled, string? Command, string? Arguments, string? Error = null)
{
    public static readonly LogonTaskStatus Missing = new(false, false, null, null);

    /// <summary>La tarea lanza un roost.exe que ya no está (se movió o se desinstaló Roost).</summary>
    public bool IsStale(Func<string, bool> fileExists) =>
        Exists && LogonTask.RoostExeIn(Arguments) is { } exe && !fileExists(exe);
}

/// <summary>
/// E4, el autoarranque sin la app: una tarea programada del usuario que, al iniciar él sesión, lanza
/// <c>roost up</c> (el daemon y el stack) sin abrir Roost. Se hace con <c>schtasks /create /xml</c>
/// y una definición que no pide administrador:
/// <list type="bullet">
/// <item>el disparador de inicio de sesión lleva el <c>UserId</c> del usuario (sin él, «cualquier
/// usuario», y eso solo lo puede crear un administrador: es lo que hace <c>schtasks /sc onlogon</c>);</item>
/// <item>se ejecuta como ese usuario, con su sesión (<c>InteractiveToken</c>) y sin elevar
/// (<c>LeastPrivilege</c>): no guarda contraseña ni pide UAC;</item>
/// <item><c>roost up</c> va dentro de <c>conhost.exe --headless</c>, para que al iniciar sesión no
/// salga una consola (ni una ventana de Windows Terminal, que es la consola por defecto en Windows 11);</item>
/// <item>sin límite de tiempo y también con batería: el stack se queda en marcha.</item>
/// </list>
/// No sirve con <c>ROOST_HOME</c> (una tarea no lleva variables de entorno: arrancaría la Roost de
/// siempre). Todo pasa por un <see cref="ICommandRunner"/>: los tests nunca crean una tarea de verdad.
/// </summary>
public sealed class LogonTask(ICommandRunner runner, string? schtasks = null, Func<string, string?>? env = null)
{
    /// <summary>La carpeta de Roost en el Programador de tareas y el nombre de la tarea.</summary>
    public const string TaskName = @"\Roost\Iniciar Roost al iniciar sesion";

    private const string Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    private readonly string _schtasks = schtasks ?? Path.Combine(Environment.SystemDirectory, "schtasks.exe");

    /// <summary>
    /// <c>roost autostart on</c> y la casilla de la app solo valen para la Roost de siempre: con
    /// <c>ROOST_HOME</c>, la tarea arrancaría otra. Devuelve por qué no, o null.
    /// </summary>
    public static string? WhyNot(Func<string, string?>? env = null)
    {
        if (!OperatingSystem.IsWindows()) return "La tarea al iniciar sesion solo existe en Windows.";
        var home = (env ?? Environment.GetEnvironmentVariable)(RoostPaths.HomeVariable);
        return string.IsNullOrWhiteSpace(home) ? null
            : $"Con {RoostPaths.HomeVariable} no se crea la tarea: no lleva esa variable y arrancaria la Roost de siempre, no la de {home}.";
    }

    /// <summary>Por qué no se puede poner con el entorno de esta instancia (<see cref="WhyNot"/>); null si se puede.</summary>
    public string? Unavailable => WhyNot(env);

    /// <summary>
    /// PURO: la definición de la tarea (el XML del Programador de tareas, esquema 1.2). Lanza
    /// <c>conhost.exe --headless "&lt;roost.exe&gt;" up</c> como <paramref name="user"/>
    /// (<c>EQUIPO\usuario</c>) 15 s después de que inicie sesión.
    /// </summary>
    public static string Xml(string roostExe, string user, string conhost)
    {
        XNamespace ns = Ns;
        var task = new XElement(ns + "Task", new XAttribute("version", "1.2"),
            new XElement(ns + "RegistrationInfo",
                new XElement(ns + "Author", "Roost"),
                new XElement(ns + "Description", "Arranca el stack de Roost (roost up) al iniciar sesion, sin abrir la app. Se quita en Roost, Ajustes, o con: roost autostart off"),
                new XElement(ns + "URI", TaskName)),
            new XElement(ns + "Triggers",
                new XElement(ns + "LogonTrigger",
                    new XElement(ns + "Enabled", "true"),
                    new XElement(ns + "UserId", user),
                    new XElement(ns + "Delay", "PT15S"))),
            new XElement(ns + "Principals",
                new XElement(ns + "Principal", new XAttribute("id", "Author"),
                    new XElement(ns + "UserId", user),
                    new XElement(ns + "LogonType", "InteractiveToken"),
                    new XElement(ns + "RunLevel", "LeastPrivilege"))),
            new XElement(ns + "Settings",
                new XElement(ns + "MultipleInstancesPolicy", "IgnoreNew"),
                new XElement(ns + "DisallowStartIfOnBatteries", "false"),
                new XElement(ns + "StopIfGoingOnBatteries", "false"),
                new XElement(ns + "AllowHardTerminate", "true"),
                new XElement(ns + "StartWhenAvailable", "false"),
                new XElement(ns + "RunOnlyIfNetworkAvailable", "false"),
                new XElement(ns + "IdleSettings",
                    new XElement(ns + "StopOnIdleEnd", "false"),
                    new XElement(ns + "RestartOnIdle", "false")),
                new XElement(ns + "AllowStartOnDemand", "true"),
                new XElement(ns + "Enabled", "true"),
                new XElement(ns + "Hidden", "false"),
                new XElement(ns + "RunOnlyIfIdle", "false"),
                new XElement(ns + "WakeToRun", "false"),
                // Sin límite: si el Programador «parase» la tarea, podría llevarse el daemon con ella.
                new XElement(ns + "ExecutionTimeLimit", "PT0S"),
                new XElement(ns + "Priority", "7")),
            new XElement(ns + "Actions", new XAttribute("Context", "Author"),
                new XElement(ns + "Exec",
                    new XElement(ns + "Command", conhost),
                    new XElement(ns + "Arguments", $"--headless \"{roostExe}\" up"),
                    new XElement(ns + "WorkingDirectory", Path.GetDirectoryName(roostExe) ?? ""))));
        return "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" + task.ToString();
    }

    /// <summary>PURO: lee lo que importa de la definición que devuelve <c>schtasks /query /xml</c>.</summary>
    public static LogonTaskStatus Parse(string xml)
    {
        try
        {
            var doc = XDocument.Parse(xml.TrimStart('\uFEFF'));
            XNamespace ns = Ns;
            var enabled = doc.Root?.Element(ns + "Settings")?.Element(ns + "Enabled")?.Value;
            var exec = doc.Root?.Element(ns + "Actions")?.Element(ns + "Exec");
            return new LogonTaskStatus(true, enabled is null || enabled.Equals("true", StringComparison.OrdinalIgnoreCase),
                exec?.Element(ns + "Command")?.Value, exec?.Element(ns + "Arguments")?.Value);
        }
        catch (System.Xml.XmlException ex)
        {
            return new LogonTaskStatus(true, true, null, null, $"no se entiende la definicion de la tarea: {ex.Message}");
        }
    }

    /// <summary>PURO: el roost.exe que va entre comillas en los argumentos (<c>--headless "C:\…\roost.exe" up</c>).</summary>
    public static string? RoostExeIn(string? arguments)
    {
        if (arguments is null) return null;
        var start = arguments.IndexOf('"');
        var end = start < 0 ? -1 : arguments.IndexOf('"', start + 1);
        return end > start + 1 ? arguments[(start + 1)..end] : null;
    }

    /// <summary>El usuario actual como lo quiere el Programador de tareas: <c>EQUIPO\usuario</c> (o <c>DOMINIO\usuario</c>).</summary>
    public static string CurrentUser => $"{Environment.UserDomainName}\\{Environment.UserName}";

    /// <summary>Si la tarea está y qué lanza (<c>schtasks /query /xml</c>, solo lectura). Nunca lanza.</summary>
    public async Task<LogonTaskStatus> StatusAsync(CancellationToken cancellation = default)
    {
        try
        {
            var result = await runner.RunAsync(Spec(["/query", "/tn", TaskName, "/xml", "ONE"]), null, cancellation);
            if (result.Succeeded) return Parse(result.Stdout);
            // 1 = no existe (el texto del error va en el idioma de Windows: se mira el código).
            return result.ExitCode == 1 && !result.TimedOut ? LogonTaskStatus.Missing
                : new LogonTaskStatus(false, false, null, null, Clean(result) ?? $"schtasks salio con {result.ExitCode}");
        }
        catch (RoostException ex)
        {
            return new LogonTaskStatus(false, false, null, null, ex.Message);
        }
    }

    /// <summary>
    /// Crea (o sustituye) la tarea con <paramref name="roostExe"/>. El XML va en un fichero temporal en
    /// UTF-16, que es lo que lee <c>schtasks /xml</c>, y se borra al acabar.
    /// </summary>
    public async Task EnableAsync(string roostExe, CancellationToken cancellation = default)
    {
        if (WhyNot(env) is { } why) throw new RoostException(why);
        var conhost = Path.Combine(Environment.SystemDirectory, "conhost.exe");
        var file = Path.Combine(Path.GetTempPath(), $"roost-tarea-{Guid.NewGuid():N}.xml");
        try
        {
            await File.WriteAllTextAsync(file, Xml(roostExe, CurrentUser, conhost), new UnicodeEncoding(bigEndian: false, byteOrderMark: true), cancellation);
            var result = await runner.RunAsync(Spec(["/create", "/tn", TaskName, "/xml", file, "/f"]), null, cancellation);
            if (!result.Succeeded)
                throw new RoostException($"No se pudo crear la tarea al iniciar sesion: {Clean(result) ?? $"schtasks salio con {result.ExitCode}"}");
        }
        finally
        {
            try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Quita la tarea; si no estaba, no hace nada. Se pregunta antes: schtasks sale con 1 por cualquier
    /// error (también «acceso denegado»), así que su código no dice si es que no estaba.
    /// </summary>
    public async Task DisableAsync(CancellationToken cancellation = default)
    {
        if (!(await StatusAsync(cancellation)).Exists) return;
        var result = await runner.RunAsync(Spec(["/delete", "/tn", TaskName, "/f"]), null, cancellation);
        if (result.Succeeded) return;
        throw new RoostException($"No se pudo quitar la tarea al iniciar sesion: {Clean(result) ?? $"schtasks salio con {result.ExitCode}"}");
    }

    private CommandSpec Spec(string[] arguments) => new(_schtasks, arguments, Timeout: TimeSpan.FromSeconds(20));

    private static string? Clean(CommandResult result)
    {
        var text = (result.Stderr.Length > 0 ? result.Stderr : result.Stdout).Trim();
        if (text.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase)) text = text[6..].Trim();
        return text.Length == 0 ? null : text;
    }
}
