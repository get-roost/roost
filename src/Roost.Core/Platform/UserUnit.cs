using Roost.Core.Scanning;

namespace Roost.Core.Platform;

/// <summary>Cómo se arranca el stack al iniciar sesión en Linux.</summary>
public enum AutostartKind
{
    None,

    /// <summary>Una unidad de <c>systemd --user</c> (<c>~/.config/systemd/user/roost.service</c>).</summary>
    SystemdUser,

    /// <summary>Un <c>.desktop</c> en <c>~/.config/autostart</c> (sin systemd de usuario: lo lanza el escritorio).</summary>
    Desktop,
}

/// <summary>Cómo está el arranque al iniciar sesión en Linux (lo que cuenta <c>roost autostart status</c>).</summary>
/// <param name="Exists">Hay una unidad o un .desktop de Roost.</param>
/// <param name="Enabled">Y está activado (la unidad, en <c>default.target.wants</c>; el .desktop, sin <c>Hidden=true</c>).</param>
/// <param name="File">El fichero (la unidad o el .desktop).</param>
/// <param name="Command">El roost que lanza.</param>
/// <param name="RoostHome">El <c>ROOST_HOME</c> que lleva, si lleva.</param>
/// <param name="Error">Por qué no se ha podido saber; null si se pudo.</param>
public sealed record UserUnitStatus(bool Exists, bool Enabled, AutostartKind Kind, string? File, string? Command, string? RoostHome, string? Error = null)
{
    public static readonly UserUnitStatus Missing = new(false, false, AutostartKind.None, null, null, null);

    /// <summary>Lanza un roost que ya no está (se movió o se desinstaló Roost).</summary>
    public bool IsStale(Func<string, bool> fileExists) => Exists && Command is { } exe && !fileExists(exe);
}

/// <summary>
/// E4 en Linux, el autoarranque sin la app: al iniciar sesión, <c>roost up</c> (el daemon y el stack).
/// Es el equivalente de <see cref="LogonTask"/> (la tarea programada de Windows):
/// <list type="bullet">
/// <item>con <c>systemd --user</c> (Ubuntu, Debian, Fedora con escritorio o con lingering), una unidad
/// de usuario <c>roost.service</c>: <c>oneshot</c> con <c>RemainAfterExit</c>, <c>ExecStart=roost up</c> y
/// <c>ExecStop=roost down</c> (al cerrar la sesión, systemd para la unidad y el stack se apaga limpio en
/// vez de morir con la sesión), enganchada a <c>default.target</c> con <c>systemctl --user enable</c>;</item>
/// <item>sin él (la sesión de usuario de systemd no arranca: pasa en WSL, «Failed to start the systemd
/// user session»), un <c>.desktop</c> en <c>~/.config/autostart</c>, que lanzan GNOME, KDE, XFCE…
/// al entrar; sin escritorio, nada lo lanza, y se dice.</item>
/// </list>
/// A diferencia de Windows, una unidad sí lleva variables: con <c>ROOST_HOME</c> se pone
/// <c>Environment=</c> y arranca esa Roost. Todo pasa por un <see cref="ICommandRunner"/>: los tests
/// nunca tocan systemd.
/// </summary>
public sealed class UserUnit(ICommandRunner runner, string? home = null, Func<string, string?>? env = null)
{
    public const string UnitName = "roost.service";
    public const string DesktopName = "roost-stack.desktop";

    private readonly Func<string, string?> _env = env ?? Environment.GetEnvironmentVariable;
    private readonly string _home = home ?? (env ?? Environment.GetEnvironmentVariable)("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public string UnitFile => Path.Combine(_home, ".config", "systemd", "user", UnitName);

    /// <summary>El enlace que crea <c>systemctl --user enable</c>: si está, la unidad arranca con la sesión.</summary>
    public string WantsLink => Path.Combine(_home, ".config", "systemd", "user", "default.target.wants", UnitName);

    public string DesktopFile => Path.Combine(_home, ".config", "autostart", DesktopName);

    /// <summary>Por qué no se puede aquí: fuera de Linux, nada (en Windows es <see cref="LogonTask"/>).</summary>
    public static string? WhyNot() =>
        OperatingSystem.IsLinux() ? null : "El arranque al iniciar sesion con systemd o un .desktop es de Linux; en Windows es una tarea programada.";

    /// <summary>PURO: la unidad de usuario. <paramref name="roostHome"/> va en <c>Environment=</c> si no es null.</summary>
    public static string Unit(string roostExe, string? roostHome) => $"""
        # Lo escribe Roost (roost autostart on): arranca el stack al iniciar sesion, sin la app.
        # Se quita con: roost autostart off (o systemctl --user disable {UnitName})
        [Unit]
        Description=Roost: arranca el stack (roost up) al iniciar sesion

        [Service]
        Type=oneshot
        RemainAfterExit=yes
        {(roostHome is null ? "" : $"Environment=\"{RoostPaths.HomeVariable}={Escape(roostHome)}\"\n")}ExecStart="{Escape(roostExe)}" up
        ExecStop="{Escape(roostExe)}" down
        TimeoutStopSec=90

        [Install]
        WantedBy=default.target

        """.Replace("\r\n", "\n");

    /// <summary>PURO: el <c>.desktop</c> de autostart (sin systemd de usuario). Con <paramref name="roostHome"/>, por <c>env</c>.</summary>
    public static string Desktop(string roostExe, string? roostHome) => $"""
        [Desktop Entry]
        Type=Application
        Name=Roost (stack)
        Comment=Arranca el stack de Roost (roost up) al iniciar sesion. Se quita con: roost autostart off
        Exec={(roostHome is null ? "" : $"env {RoostPaths.HomeVariable}={DesktopQuote(roostHome)} ")}{DesktopQuote(roostExe)} up
        Terminal=false
        NoDisplay=true
        X-GNOME-Autostart-enabled=true
        X-Roost-Autostart=stack

        """.Replace("\r\n", "\n");

    /// <summary>PURO: lo que importa de una unidad (el roost de <c>ExecStart</c> y el <c>ROOST_HOME</c> de <c>Environment</c>).</summary>
    public static UserUnitStatus ParseUnit(string text, string file, bool enabled)
    {
        string? command = null, roostHome = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("ExecStart=", StringComparison.Ordinal)) command = FirstWord(line["ExecStart=".Length..]);
            else if (line.StartsWith("Environment=", StringComparison.Ordinal))
            {
                var value = line["Environment=".Length..].Trim().Trim('"');
                if (value.StartsWith(RoostPaths.HomeVariable + "=", StringComparison.Ordinal)) roostHome = Unescape(value[(RoostPaths.HomeVariable.Length + 1)..]);
            }
        }
        return new UserUnitStatus(true, enabled, AutostartKind.SystemdUser, file, command, roostHome);
    }

    /// <summary>PURO: lo que importa de un .desktop (el roost de <c>Exec</c>, el <c>ROOST_HOME</c> del <c>env</c>, y si está escondido).</summary>
    public static UserUnitStatus ParseDesktop(string text, string file)
    {
        string? command = null, roostHome = null;
        var hidden = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("Exec=", StringComparison.Ordinal))
            {
                var rest = line["Exec=".Length..].Trim();
                if (rest.StartsWith("env ", StringComparison.Ordinal))
                {
                    rest = rest[4..].TrimStart();
                    var assignment = FirstWord(rest);
                    if (assignment.StartsWith(RoostPaths.HomeVariable + "=", StringComparison.Ordinal)) roostHome = assignment[(RoostPaths.HomeVariable.Length + 1)..];
                    rest = AfterFirstWord(rest);
                }
                command = FirstWord(rest);
            }
            else if (line.Equals("Hidden=true", StringComparison.OrdinalIgnoreCase) || line.Equals("X-GNOME-Autostart-enabled=false", StringComparison.OrdinalIgnoreCase))
                hidden = true;
        }
        return new UserUnitStatus(true, !hidden, AutostartKind.Desktop, file, command, roostHome);
    }

    /// <summary>
    /// Si la sesión de usuario de systemd contesta (<c>systemctl --user is-system-running</c>): sin ella
    /// (WSL cuando no arranca, un contenedor, una sesión por ssh sin lingering) no se puede usar una
    /// unidad. Devuelve por qué no, o null si sí. Nunca lanza.
    /// </summary>
    public async Task<string?> WhySystemdUnavailableAsync(CancellationToken cancellation = default)
    {
        try
        {
            var result = await runner.RunAsync(new CommandSpec("systemctl", ["--user", "is-system-running"], Timeout: TimeSpan.FromSeconds(10), Stdin: ""), null, cancellation);
            var state = result.Stdout.Trim().Split('\n')[0].Trim();
            if (state is "running" or "degraded" or "starting" or "initializing" or "maintenance") return null;
            var why = GitOutputHeadline(result.Stderr.Length > 0 ? result.Stderr : result.Stdout);
            return $"la sesion de usuario de systemd no contesta ({(why.Length > 0 ? why : result.TimedOut ? "no contesto a tiempo" : $"systemctl salio con {result.ExitCode}")})";
        }
        catch (RoostException ex)
        {
            return $"no se pudo ejecutar systemctl ({ex.Message})";
        }
    }

    /// <summary>Lo puesto, sin tocar nada: la unidad (y si esta en default.target.wants) o el .desktop.</summary>
    public UserUnitStatus Status()
    {
        try
        {
            if (File.Exists(UnitFile)) return ParseUnit(File.ReadAllText(UnitFile), UnitFile, File.Exists(WantsLink) || Directory.Exists(WantsLink) || new FileInfo(WantsLink).LinkTarget is not null);
            if (File.Exists(DesktopFile)) return ParseDesktop(File.ReadAllText(DesktopFile), DesktopFile);
            return UserUnitStatus.Missing;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new UserUnitStatus(false, false, AutostartKind.None, null, null, null, ex.Message);
        }
    }

    /// <summary>
    /// Pone el arranque: la unidad si systemd de usuario contesta (<c>daemon-reload</c> y <c>enable</c>, sin
    /// arrancarla ahora: es para la próxima sesión, como la tarea de Windows); si no, el .desktop.
    /// Con <c>ROOST_HOME</c> en el entorno, esa Roost. Devuelve lo que ha puesto (con <c>Error</c> con
    /// el aviso de por qué no fue systemd, si no fue).
    /// </summary>
    public async Task<UserUnitStatus> EnableAsync(string roostExe, CancellationToken cancellation = default)
    {
        // Sin guarda de sistema: quien llama (la CLI, la app) elige esto en Linux (WhyNot); los tests
        // escriben el .desktop en una HOME temporal en cualquier sistema.
        var roostHome = _env(RoostPaths.HomeVariable);
        if (string.IsNullOrWhiteSpace(roostHome)) roostHome = null;
        var unavailable = await WhySystemdUnavailableAsync(cancellation);
        try
        {
            if (unavailable is null)
            {
                if (File.Exists(DesktopFile)) File.Delete(DesktopFile);   // no las dos cosas a la vez
                Directory.CreateDirectory(Path.GetDirectoryName(UnitFile)!);
                File.WriteAllText(UnitFile, Unit(roostExe, roostHome));
                await Systemctl(["daemon-reload"], "recargar las unidades de usuario", cancellation);
                await Systemctl(["enable", UnitName], $"activar {UnitName}", cancellation);
                return ParseUnit(Unit(roostExe, roostHome), UnitFile, true);
            }

            if (File.Exists(UnitFile)) await RemoveUnitAsync(cancellation);
            Directory.CreateDirectory(Path.GetDirectoryName(DesktopFile)!);
            File.WriteAllText(DesktopFile, Desktop(roostExe, roostHome));
            return ParseDesktop(Desktop(roostExe, roostHome), DesktopFile) with { Error = unavailable };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new RoostException($"No se pudo poner el arranque al iniciar sesion: {ex.Message}");
        }
    }

    /// <summary>Quita lo que haya (la unidad, con <c>disable</c> y <c>daemon-reload</c>; el .desktop). Sin nada, no hace nada. No para el stack.</summary>
    public async Task DisableAsync(CancellationToken cancellation = default)
    {
        try
        {
            if (File.Exists(UnitFile)) await RemoveUnitAsync(cancellation);
            if (File.Exists(DesktopFile)) File.Delete(DesktopFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new RoostException($"No se pudo quitar el arranque al iniciar sesion: {ex.Message}");
        }
    }

    private async Task RemoveUnitAsync(CancellationToken cancellation)
    {
        // Sin systemd que conteste, disable falla: el enlace de wants se quita a mano y vale igual.
        try { await Systemctl(["disable", UnitName], $"desactivar {UnitName}", cancellation); }
        catch (RoostException) { }
        if (File.Exists(WantsLink) || new FileInfo(WantsLink).LinkTarget is not null) File.Delete(WantsLink);
        File.Delete(UnitFile);
        try { await Systemctl(["daemon-reload"], "recargar las unidades de usuario", cancellation); }
        catch (RoostException) { }
    }

    private async Task Systemctl(string[] arguments, string what, CancellationToken cancellation)
    {
        var result = await runner.RunAsync(new CommandSpec("systemctl", ["--user", .. arguments], Timeout: TimeSpan.FromSeconds(30), Stdin: ""), null, cancellation);
        if (result.Succeeded) return;
        var why = GitOutputHeadline(result.Stderr.Length > 0 ? result.Stderr : result.Stdout);
        throw new RoostException($"No se pudo {what}: {(why.Length > 0 ? why : $"systemctl salio con {result.ExitCode}")}");
    }

    private static string GitOutputHeadline(string text) =>
        text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";

    // Lo que admite systemd entre comillas dobles: \ y " escapados.
    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string Unescape(string value) => value.Replace("\\\"", "\"").Replace("\\\\", "\\");

    // El Exec de un .desktop: entre comillas dobles si hace falta, con \ y " escapados (y % doblado).
    private static string DesktopQuote(string value)
    {
        var escaped = value.Replace("%", "%%");
        return escaped.IndexOfAny([' ', '\t', '"', '\'', '\\', '>', '<', '~', '|', '&', ';', '$', '*', '?', '#', '(', ')', '`']) < 0
            ? escaped
            : "\"" + escaped.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("`", "\\`").Replace("$", "\\$") + "\"";
    }

    private static string FirstWord(string text)
    {
        text = text.TrimStart();
        if (text.StartsWith('"'))
        {
            var sb = new System.Text.StringBuilder();
            for (var i = 1; i < text.Length; i++)
            {
                if (text[i] == '\\' && i + 1 < text.Length) { sb.Append(text[++i]); continue; }
                if (text[i] == '"') break;
                sb.Append(text[i]);
            }
            return sb.ToString().Replace("%%", "%");
        }
        var end = text.IndexOf(' ');
        return (end < 0 ? text : text[..end]).Replace("%%", "%");
    }

    private static string AfterFirstWord(string text)
    {
        text = text.TrimStart();
        if (text.StartsWith('"'))
        {
            for (var i = 1; i < text.Length; i++)
            {
                if (text[i] == '\\') { i++; continue; }
                if (text[i] == '"') return text[(i + 1)..].TrimStart();
            }
            return "";
        }
        var end = text.IndexOf(' ');
        return end < 0 ? "" : text[(end + 1)..].TrimStart();
    }
}
