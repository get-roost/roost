using System.Diagnostics;
using System.Text;
using Roost.Core.Dns;
using Roost.Core.Share;
using Roost.Core.Sites;

// roost-helper: lo UNICO de Roost que se ejecuta con privilegios de administrador (ROOST.md §5.1,
// decision 5). Una lista cerrada de operaciones, sin nada libre que llegue al sistema:
//
//   roost-helper hosts-sync <fichero>
//     <fichero> trae un nombre por linea. Solo valen nombres <etiqueta>.test (HostsBlock.IsValidHost).
//     En el hosts del sistema (ruta fija, no se acepta otra) solo se reescribe el bloque
//     "# roost:begin" / "# roost:end" con lineas "127.0.0.1 <nombre>"; lo demas queda intacto byte a
//     byte (Latin-1 lee y escribe cada byte tal cual).
//
//   roost-helper dns-enable | dns-disable
//     Pone o quita la regla NRPT ".test" -> 127.53.0.1 (el DNS local de Roost, ROOST.md §5.7). Sin
//     argumentos: el script es fijo (NrptScripts) y lo ejecuta el PowerShell del sistema, por su ruta.
//
//   roost-helper firewall-allow <puerto> | firewall-remove <puerto>
//     Pone o quita la regla del cortafuegos "Roost LAN <puerto>" de roost share --lan: entrada TCP a ese
//     puerto, solo en el perfil privado y solo desde la subred local. Lo unico que llega de fuera es el
//     puerto, validado (FirewallRule.TryParsePort: 80, 443 o de 1024 a 65535, sin los de administracion
//     remota ni los de bases de datos); el script es fijo (FirewallRule) y lo ejecuta el mismo PowerShell.
//
// Cada ejecucion deja una linea en un registro de ruta fija: %ProgramData%\Roost\roost-helper.log.

const int Ok = 0, Usage = 64, BadInput = 65, ForeignRule = 69, ScriptFailed = 70, IoError = 74;

switch (args)
{
    case ["hosts-sync", var requestFile]:
        return HostsSync(requestFile);
    case ["dns-enable"]:
        return RunNrpt("dns-enable", NrptScripts.Enable);
    case ["dns-disable"]:
        return RunNrpt("dns-disable", NrptScripts.Disable);
    case ["firewall-allow", var port]:
        return RunFirewall("firewall-allow", port, allow: true);
    case ["firewall-remove", var port]:
        return RunFirewall("firewall-remove", port, allow: false);
    default:
        Console.Error.WriteLine("Uso: roost-helper hosts-sync <fichero con un nombre .test por linea> | dns-enable | dns-disable | firewall-allow <puerto> | firewall-remove <puerto>");
        return Usage;
}

static int HostsSync(string requestFile)
{
    var hostsFile = Path.Combine(Environment.SystemDirectory, "drivers", "etc", "hosts");

    List<string> names;
    try
    {
        var request = new FileInfo(requestFile);
        if (!request.Exists || request.Length > 64 * 1024)
        {
            Log("rechazado: la peticion no existe o es demasiado grande");
            return BadInput;
        }
        names = File.ReadAllLines(request.FullName).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
    }
    catch (Exception ex)
    {
        Log($"rechazado: no se pudo leer la peticion ({ex.GetType().Name})");
        return BadInput;
    }

    if (names.Count > 1000 || names.Any(n => !HostsBlock.IsValidHost(n)))
    {
        // No se registra el contenido rechazado: podria ser cualquier cosa.
        Log($"rechazado: {names.Count} lineas, y no todas son un nombre <etiqueta>.test");
        return BadInput;
    }

    for (var attempt = 1; ; attempt++)
    {
        try
        {
            var current = File.Exists(hostsFile) ? File.ReadAllText(hostsFile, Encoding.Latin1) : "";
            var updated = HostsBlock.Apply(current, names);
            if (updated != current) File.WriteAllText(hostsFile, updated, Encoding.Latin1);
            Log($"hosts-sync: {names.Count} nombres ({string.Join(", ", names)})");
            return Ok;
        }
        catch (InvalidDataException ex)
        {
            Log("rechazado: " + ex.Message);
            return BadInput;
        }
        catch (IOException) when (attempt < 5)
        {
            Thread.Sleep(200 * attempt); // un antivirus puede tener el hosts abierto un momento
        }
        catch (Exception ex)
        {
            Log("error al escribir el hosts: " + ex.Message);
            return IoError;
        }
    }
}

static int RunNrpt(string operation, string script)
{
    var result = RunPowerShell(operation, script);
    if (result is null) return ScriptFailed;
    var (exitCode, output) = result.Value;
    switch (exitCode)
    {
        case 0:
            Log($"{operation}: regla {NrptScripts.Namespace} -> {NrptScripts.Address} {(operation == "dns-enable" ? "puesta" : "quitada")}");
            return Ok;
        case NrptScripts.ForeignRuleExit:
            Log($"{operation}: ya hay una regla para {NrptScripts.Namespace} que no es de Roost; no se toca");
            return ForeignRule;
        default:
            Log($"{operation}: PowerShell salio con {exitCode}: {output}");
            return ScriptFailed;
    }
}

static int RunFirewall(string operation, string portText, bool allow)
{
    if (!FirewallRule.TryParsePort(portText, out var port))
    {
        // Como en hosts-sync, lo rechazado no se copia al registro: podria ser cualquier cosa.
        Log($"rechazado: {operation} con un puerto que no vale");
        return BadInput;
    }

    var result = RunPowerShell(operation, allow ? FirewallRule.AllowScript(port) : FirewallRule.RemoveScript(port));
    if (result is null) return ScriptFailed;
    var (exitCode, output) = result.Value;
    if (exitCode != 0)
    {
        Log($"{operation}: PowerShell salio con {exitCode}: {output}");
        return ScriptFailed;
    }
    Log($"{operation}: regla \"{FirewallRule.DisplayName(port)}\" {(allow ? "puesta (TCP, perfil privado, subred local)" : "quitada")}");
    return Ok;
}

// El codigo de salida del script y lo que escribio (stderr y stdout), o null si no se pudo ejecutar o
// no termino a tiempo (ya registrado).
static (int ExitCode, string Output)? RunPowerShell(string operation, string script)
{
    // El PowerShell del sistema por su ruta, nunca el que encuentre el PATH; el script va en
    // -EncodedCommand (UTF-16LE en base64), sin comillas que escapar.
    var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
    var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
    var start = new ProcessStartInfo(powershell, $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}")
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };
    try
    {
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60_000))
        {
            process.Kill(entireProcessTree: true);
            Log($"{operation}: PowerShell no termino en 60 s");
            return null;
        }
        return (process.ExitCode, $"{error.Result.Trim()} {output.Result.Trim()}");
    }
    catch (Exception ex)
    {
        Log($"{operation}: no se pudo ejecutar PowerShell ({ex.Message})");
        return null;
    }
}

static void Log(string message)
{
    try
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Roost");
        Directory.CreateDirectory(dir);
        File.AppendAllText(Path.Combine(dir, "roost-helper.log"),
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {Environment.UserName}: {message}{Environment.NewLine}");
    }
    catch
    {
        // sin registro no se deja de hacer el trabajo
    }
}
