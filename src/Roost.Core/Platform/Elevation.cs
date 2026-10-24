using System.ComponentModel;
using System.Diagnostics;
using Roost.Core.Scanning;

namespace Roost.Core.Platform;

/// <summary>Cómo acabó una llamada a <c>roost-helper</c> con privilegios.</summary>
public enum ElevationStatus
{
    /// <summary>El helper corrió: <see cref="HelperRun.ExitCode"/> es su código de salida (0 = bien; los demás los traduce quien llama).</summary>
    Done,

    /// <summary>El usuario dijo que no: «No» en el UAC, Cancelar en el diálogo de polkit, Ctrl+C o una contraseña mal en sudo.</summary>
    Cancelled,
}

/// <summary>Lo que dejó una llamada al helper: el estado, su código, lo que escribió y la orden para hacerlo a mano.</summary>
public sealed record HelperRun(ElevationStatus Status, int ExitCode, string Output, string ManualCommand)
{
    public bool Ok => Status == ElevationStatus.Done && ExitCode == 0;
}

/// <summary>
/// Desde aquí no hay forma de pedir permiso de administrador (la app sin agente de polkit, un WSL sin
/// polkit ni terminal): el mensaje dice qué hacer y <see cref="Command"/> es la orden exacta para
/// copiarla y ejecutarla en una terminal («Copiar» en la app).
/// </summary>
public sealed class ElevationNeededException(string message, string command) : RoostException(message)
{
    public string Command { get; } = command;
}

/// <summary>
/// La única forma de lanzar <c>roost-helper</c> con privilegios, para el hosts, el DNS local, el
/// cortafuegos, la carpeta de proyectos y la CA del sistema (ROOST.md §5.1 y §6: nunca corre Roost
/// elevado, solo el helper con su lista cerrada).
/// <list type="bullet">
/// <item>Windows: <c>ShellExecute</c> con <c>Verb = runas</c> (el UAC); cancelar es el error 1223.</item>
/// <item>Linux: <c>pkexec &lt;helper&gt; &lt;op&gt;</c> con la política <c>dev.roost.helper</c> del
/// paquete (cancelar es 126; sin agente de polkit o sin autorizar, 127) y, si eso no vale y hay una
/// terminal, <c>sudo</c> (pregunta la contraseña en la terminal); sin terminal se prueba <c>sudo -n</c>
/// por si no pide contraseña. Como root, el helper tal cual.</item>
/// <item>Si no hay manera (sin pkexec ni sudo, sin agente y sin terminal), no se lanza nada:
/// <see cref="ElevationNeededException"/> trae la orden exacta para que el usuario la ejecute él.</item>
/// </list>
/// Los nombres del hosts van al helper por stdin en Linux (<paramref name="stdin"/>): pkexec y sudo
/// se lo pasan tal cual. En Windows el UAC no deja stdin, y el helper lee el fichero de siempre.
/// Los tests la construyen con un <see cref="ICommandRunner"/> falso y sus propias respuestas.
/// </summary>
public sealed class Elevation
{
    public const string Pkexec = "/usr/bin/pkexec";
    public const string Sudo = "/usr/bin/sudo";

    /// <summary>La de verdad: pkexec y sudo del sistema, el UAC de Windows.</summary>
    public static Elevation Default { get; } = new();

    private readonly ICommandRunner _runner;
    private readonly bool _linux;
    private readonly Func<bool> _isRoot;
    private readonly Func<bool> _hasTerminal;
    private readonly Func<string, bool> _exists;
    private readonly Func<ProcessStartInfo, int> _launchWindows;

    /// <param name="runner">Con qué se ejecutan pkexec, sudo o el helper (en Linux).</param>
    /// <param name="linux">Forzar el camino de Linux o el de Windows (los tests); null = el de este sistema.</param>
    /// <param name="isRoot">Si ya somos root (entonces el helper va directo).</param>
    /// <param name="hasTerminal">Si hay una terminal en la que sudo pueda preguntar la contraseña.</param>
    /// <param name="exists">Si existe un fichero (el helper, pkexec, sudo).</param>
    /// <param name="launchWindows">Cómo se lanza el helper con el UAC en Windows; devuelve su código de salida.</param>
    public Elevation(ICommandRunner? runner = null, bool? linux = null, Func<bool>? isRoot = null, Func<bool>? hasTerminal = null,
        Func<string, bool>? exists = null, Func<ProcessStartInfo, int>? launchWindows = null)
    {
        _runner = runner ?? CommandRunner.Default;
        _linux = linux ?? !OperatingSystem.IsWindows();
        _isRoot = isRoot ?? (() => HelperChecks.CurrentUid() == 0);
        _hasTerminal = hasTerminal ?? HasTerminal;
        _exists = exists ?? File.Exists;
        _launchWindows = launchWindows ?? LaunchWithUac;
    }

    /// <summary>
    /// Lanza <paramref name="helperExe"/> con privilegios y <paramref name="arguments"/>; espera a que
    /// termine. <paramref name="stdin"/> es lo que se le escribe por la entrada (Linux) y
    /// <paramref name="stdinFile"/> el fichero con lo mismo, solo para componer la orden manual
    /// (<c>&lt; fichero</c>). Lanza <see cref="RoostException"/> si no se encuentra el helper o no se
    /// puede lanzar, y <see cref="ElevationNeededException"/> si no hay forma de pedir permiso.
    /// </summary>
    public HelperRun Run(string helperExe, IReadOnlyList<string> arguments, string? stdin = null, string? stdinFile = null)
    {
        if (!_exists(helperExe))
            throw new RoostException($"No se encuentra {helperExe}, que es el que hace los cambios que piden permiso de administrador.");
        var manual = ManualCommand(helperExe, arguments, stdinFile);
        return _linux ? RunLinux(helperExe, arguments, stdin, manual) : RunWindows(helperExe, arguments, manual);
    }

    /// <summary>La orden para hacerlo a mano en una terminal: <c>sudo &lt;helper&gt; &lt;op&gt; [&lt; fichero]</c> en Linux y el helper tal cual (en una consola de administrador) en Windows.</summary>
    public string ManualCommand(string helperExe, IReadOnlyList<string> arguments, string? stdinFile = null)
    {
        Func<string, string> quote = _linux ? ShellQuote : WindowsQuote;
        var command = string.Join(' ', new[] { helperExe }.Concat(arguments).Select(quote));
        if (_linux) command = "sudo " + command;
        if (stdinFile is not null) command += " < " + (_linux ? ShellQuote(stdinFile) : WindowsQuote(stdinFile));
        return command;
    }

    private HelperRun RunLinux(string helperExe, IReadOnlyList<string> arguments, string? stdin, string manual)
    {
        if (_isRoot())
            return Done(Exec(helperExe, arguments, stdin), manual);

        var reasons = new List<string>();
        if (_exists(Pkexec))
        {
            var result = Exec(Pkexec, [helperExe, .. arguments], stdin);
            switch (result.ExitCode)
            {
                case 126:
                    return new HelperRun(ElevationStatus.Cancelled, result.ExitCode, Output(result), manual);
                case 127:
                    // Sin agente de polkit (una terminal por SSH, un WSL sin escritorio, la app sin sesión gráfica) o sin autorizar.
                    reasons.Add("pkexec no ha podido autorizar" + Detail(result));
                    break;
                default:
                    return Done(result, manual);   // el helper corrió: pkexec devuelve su código
            }
        }
        else
        {
            reasons.Add("no hay pkexec");
        }

        if (_exists(Sudo))
        {
            if (_hasTerminal())
            {
                // sudo pregunta la contraseña en la terminal. Lo que sale con 1 y «sudo:» es de sudo (tres intentos
                // mal, Ctrl+C, «no estás en sudoers»), no del helper, que nunca sale con 1.
                var result = Exec(Sudo, ["--", helperExe, .. arguments], stdin);
                return result.ExitCode == 1 && result.Stderr.TrimStart().StartsWith("sudo:", StringComparison.Ordinal)
                    ? new HelperRun(ElevationStatus.Cancelled, result.ExitCode, Output(result), manual)
                    : Done(result, manual);
            }

            // Sin terminal, solo si sudo no pide contraseña (NOPASSWD o la tiene reciente): -n nunca pregunta.
            var quiet = Exec(Sudo, ["-n", "--", helperExe, .. arguments], stdin);
            if (!(quiet.ExitCode == 1 && quiet.Stderr.Contains("sudo:", StringComparison.Ordinal)))
                return Done(quiet, manual);
            reasons.Add("sudo pide la contrasena y no hay terminal");
        }
        else
        {
            reasons.Add("no hay sudo");
        }

        throw new ElevationNeededException(
            $"No se ha podido pedir permiso de administrador ({string.Join("; ", reasons)}). Ejecuta esto en una terminal y repite la orden:\n  {manual}",
            manual);
    }

    private HelperRun RunWindows(string helperExe, IReadOnlyList<string> arguments, string manual)
    {
        var start = new ProcessStartInfo(helperExe)
        {
            UseShellExecute = true,
            Verb = "runas", // UAC
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        try
        {
            return new HelperRun(ElevationStatus.Done, _launchWindows(start), "", manual);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED: «No» en el UAC
        {
            return new HelperRun(ElevationStatus.Cancelled, 1223, ex.Message, manual);
        }
    }

    private CommandResult Exec(string exe, IReadOnlyList<string> arguments, string? stdin) =>
        _runner.RunAsync(new CommandSpec(exe, arguments, Stdin: stdin), null, CancellationToken.None).GetAwaiter().GetResult();

    private static HelperRun Done(CommandResult result, string manual) => new(ElevationStatus.Done, result.ExitCode, Output(result), manual);

    private static string Output(CommandResult result) => $"{result.Stderr.Trim()} {result.Stdout.Trim()}".Trim();

    private static string Detail(CommandResult result)
    {
        var line = result.Stderr.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        return line is null ? " (no hay agente de polkit)" : $" ({line})";
    }

    private static int LaunchWithUac(ProcessStartInfo start)
    {
        using var helper = Process.Start(start) ?? throw new RoostException("No se pudo lanzar roost-helper.");
        helper.WaitForExit();
        return helper.ExitCode;
    }

    /// <summary>Si alguno de los tres flujos estándar es una terminal: entonces sudo (y el agente de texto de pkexec) pueden preguntar en <c>/dev/tty</c>.</summary>
    private static bool HasTerminal() => !Console.IsInputRedirected || !Console.IsOutputRedirected || !Console.IsErrorRedirected;

    /// <summary>Entrecomillado POSIX: tal cual si solo lleva lo seguro, y si no entre comillas simples.</summary>
    public static string ShellQuote(string value) =>
        value.Length > 0 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or '/' or ':' or '=' or ',' or '+' or '@')
            ? value
            : "'" + value.Replace("'", "'\\''") + "'";

    private static string WindowsQuote(string value) =>
        value.Length > 0 && !value.Any(char.IsWhiteSpace) ? value : "\"" + value + "\"";
}
