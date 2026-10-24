using System.Text;
using Roost.Core.Platform;
using Roost.Core.Scanning;

namespace Roost.Core.Sites;

/// <summary>
/// Mantiene los <c>.test</c> de los sitios en el fichero <c>hosts</c>. Leerlo no necesita
/// privilegios; escribir el del sistema sí, así que lo hace <c>roost-helper</c> con permisos
/// (<see cref="Platform.Elevation"/>: el UAC en Windows, pkexec o sudo en Linux), y solo si el
/// bloque cambia de verdad: un <c>site add</c> repetido no pide permiso. En Windows el helper lee
/// los nombres del fichero de petición; en Linux le llegan por stdin.
///
/// En un Linux de WSL el hosts que importa es el de Windows (el navegador está allí, y WSL copia ese
/// a <c>/etc/hosts</c> en cada arranque): se lee ese y se escribe con el <c>roost-helper.exe</c> de un
/// Roost de Windows por interop (<see cref="WslInterop"/>); si no lo hay, se dan las líneas para copiar.
///
/// La variable <c>ROOST_HOSTS_FILE</c> apunta a un hosts de pruebas, que Roost escribe
/// directamente, sin helper ni permisos. El helper elevado no la lee nunca: solo toca el del sistema.
/// </summary>
public sealed class HostsUpdater
{
    public const string HostsFileVariable = "ROOST_HOSTS_FILE";

    /// <summary>El hosts del sistema: en Windows, el de <c>System32\drivers\etc</c>; en Linux y macOS, <c>/etc/hosts</c>; desde WSL, el de Windows si se ve.</summary>
    public static string SystemHostsFile =>
        OperatingSystem.IsWindows() ? WindowsHostsFile
        : Wsl.WindowsHostsFile is { } windows && File.Exists(windows) ? windows
        : "/etc/hosts";

    private static string WindowsHostsFile => Path.Combine(Environment.SystemDirectory, "drivers", "etc", "hosts");

    public HostsUpdater()
    {
        var custom = Environment.GetEnvironmentVariable(HostsFileVariable);
        HostsFile = string.IsNullOrWhiteSpace(custom) ? SystemHostsFile : Path.GetFullPath(custom);
        IsSystemFile = string.IsNullOrWhiteSpace(custom);
        IsWindowsHostsFromWsl = IsSystemFile && !OperatingSystem.IsWindows() && HostsFile == Wsl.WindowsHostsFile;
    }

    /// <summary>Para los tests: un hosts concreto, como si fuera el del sistema (o el de Windows visto desde WSL).</summary>
    internal HostsUpdater(string hostsFile, bool isSystemFile, bool windowsFromWsl)
    {
        HostsFile = hostsFile;
        IsSystemFile = isSystemFile;
        IsWindowsHostsFromWsl = windowsFromWsl;
    }

    public string HostsFile { get; }

    public bool IsSystemFile { get; }

    /// <summary>true si es el hosts de Windows leído desde un Linux de WSL: lo escribe el Roost de Windows, no el helper de este Linux.</summary>
    public bool IsWindowsHostsFromWsl { get; }

    /// <summary>Cómo se lanza el helper con privilegios (los tests ponen uno con un runner falso).</summary>
    public Elevation Elevation { get; init; } = Elevation.Default;

    /// <summary>Con qué se ejecutan los <c>.exe</c> de Windows desde WSL (los tests, uno falso).</summary>
    public ICommandRunner Runner { get; init; } = CommandRunner.Default;

    /// <summary>Si existe un fichero (el <c>roost-helper.exe</c> de Windows visto desde WSL); los tests lo cambian.</summary>
    public Func<string, bool> FileExists { get; init; } = File.Exists;

    /// <summary>Cómo ve Windows un fichero de este Linux (WSL: <see cref="WslInterop.ToWindowsPath"/>); los tests ponen el suyo.</summary>
    internal Func<string, string?> WindowsPathOf { get; init; } = path => WslInterop.ToWindowsPath(path, WslInterop.Distro);

    /// <summary>Si el helper es el de Linux (los nombres por stdin) o el de Windows (el fichero de petición); los tests fuerzan el de Linux.</summary>
    internal bool LinuxHelper { get; init; } = !OperatingSystem.IsWindows();

    public IReadOnlyList<string> Current()
    {
        try
        {
            return File.Exists(HostsFile) ? HostsBlock.ReadHosts(File.ReadAllText(HostsFile, Encoding.Latin1)) : [];
        }
        catch (InvalidDataException ex)
        {
            throw new RoostException($"{HostsFile}: {ex.Message}");
        }
    }

    public bool InSync(IEnumerable<string> hostNames) =>
        Current().SequenceEqual(Normalize(hostNames), StringComparer.Ordinal);

    /// <summary>
    /// Deja en el bloque de Roost exactamente <paramref name="hostNames"/>. Devuelve false si ya
    /// estaba así (no pide nada). Con el hosts del sistema lanza <paramref name="helperExe"/> con
    /// privilegios; <paramref name="requestFile"/> es donde se dejan los nombres (lo lee el helper de
    /// Windows; en Linux sirve para la orden manual, <c>&lt; fichero</c>).
    /// </summary>
    public bool Sync(IEnumerable<string> hostNames, string helperExe, string requestFile)
    {
        var wanted = Normalize(hostNames);
        if (InSync(wanted)) return false;

        if (!IsSystemFile)
        {
            // Latin-1, como el helper: cada byte de fuera del bloque se conserva tal cual.
            var current = File.Exists(HostsFile) ? File.ReadAllText(HostsFile, Encoding.Latin1) : "";
            File.WriteAllText(HostsFile, HostsBlock.Apply(current, wanted), Encoding.Latin1);
            return true;
        }

        WriteRequest(requestFile, wanted);
        if (IsWindowsHostsFromWsl)
            SyncFromWsl(wanted, requestFile);
        else if (!LinuxHelper)
            SyncWithHelper(helperExe, ["hosts-sync", requestFile], stdin: null, stdinFile: null);
        else
            SyncWithHelper(helperExe, ["hosts-sync"], stdin: string.Join('\n', wanted) + "\n", stdinFile: requestFile);

        if (!InSync(wanted))
            throw new RoostException($"roost-helper termino, pero {HostsFile} no tiene los nombres esperados. Detalle en {HelperLog}.");
        return true;
    }

    private void SyncWithHelper(string helperExe, string[] arguments, string? stdin, string? stdinFile)
    {
        var run = Elevation.Run(helperExe, arguments, stdin, stdinFile);
        if (run.Status == ElevationStatus.Cancelled)
        {
            throw new RoostException(
                "Has cancelado el permiso de administrador: el hosts no se ha tocado y los .test nuevos no resolveran. "
                + (OperatingSystem.IsWindows() ? "Repite el comando y acepta el aviso de Windows." : "Repite el comando y acepta el permiso."));
        }
        if (run.ExitCode != 0)
        {
            throw new RoostException(
                $"roost-helper no pudo actualizar el hosts (codigo {run.ExitCode}). Detalle en {HelperLog}.{(run.Output.Length == 0 ? "" : $" ({run.Output})")}");
        }
    }

    /// <summary>
    /// El hosts de Windows desde WSL: con un Roost de Windows, su <c>roost-helper.exe</c> con el UAC
    /// (lanzado desde PowerShell por interop) y el fichero de petición visto desde Windows
    /// (<c>\\wsl.localhost\…</c>); sin él, las líneas para que el usuario las pegue a mano.
    /// </summary>
    private void SyncFromWsl(List<string> wanted, string requestFile)
    {
        var block = ManualBlock(wanted);
        var helper = WslInterop.WindowsHelper(FileExists);
        if (helper is null)
        {
            throw new ElevationNeededException(
                $"En WSL los .test los resuelve el hosts de Windows ({HostsFile}) y desde aqui Roost solo puede editarlo con un Roost de Windows instalado ({WslInterop.WindowsRoostDir}). "
                + "Abrelo en Windows como administrador y deja entre '# roost:begin' y '# roost:end' exactamente esto:\n" + block,
                block);
        }

        var requestForWindows = WindowsPathOf(requestFile)
            ?? throw new ElevationNeededException(
                $"No se sabe el nombre de esta distro ({WslInterop.DistroVariable}), asi que Windows no puede leer la peticion. Pon esto a mano en {HostsFile}, entre '# roost:begin' y '# roost:end':\n" + block,
                block);
        // PowerShell corre en Windows: el helper va con su ruta de Windows (C:\Program Files\Roost\…), no con la de /mnt/c.
        var helperForWindows = WslInterop.ToWindowsPath(helper, null)!;
        var result = Runner.RunAsync(WslInterop.RunAs(helperForWindows, ["hosts-sync", requestForWindows]), null, CancellationToken.None).GetAwaiter().GetResult();
        switch (result.ExitCode)
        {
            case 0:
                return;
            case 126:
                throw new RoostException("Has cancelado el permiso de administrador de Windows: el hosts no se ha tocado y los .test nuevos no resolveran. Repite el comando y acepta el aviso de Windows.");
            case 127:
                throw new ElevationNeededException(
                    $"No se ha podido lanzar {helper} desde WSL ({result.Stderr.Trim()}). Pon esto a mano en {HostsFile}, entre '# roost:begin' y '# roost:end':\n" + block,
                    block);
            default:
                throw new RoostException($"El roost-helper.exe de Windows no pudo actualizar el hosts (codigo {result.ExitCode}). Detalle en C:\\ProgramData\\Roost\\roost-helper.log.");
        }
    }

    /// <summary>PURO: el bloque de Roost tal como tiene que quedar en el hosts, para copiarlo a mano (con los saltos de línea de Windows).</summary>
    public static string ManualBlock(IEnumerable<string> hostNames) => HostsBlock.Apply("", Normalize(hostNames));

    private static void WriteRequest(string requestFile, List<string> wanted)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(requestFile)!);
        File.WriteAllLines(requestFile, wanted);
    }

    /// <summary>Donde deja su registro el helper (ruta fija: el helper no acepta rutas para escribir): <c>%ProgramData%\Roost</c> en Windows, <c>/var/log/roost</c> en Linux.</summary>
    public static string HelperLog =>
        OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Roost", "roost-helper.log")
            : "/var/log/roost/roost-helper.log";

    private static List<string> Normalize(IEnumerable<string> hostNames) =>
        hostNames.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
}
