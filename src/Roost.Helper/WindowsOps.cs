using System.Diagnostics;
using System.Text;
using Roost.Core.Dns;
using Roost.Core.Platform;
using Roost.Core.Share;

namespace Roost.Helper;

/// <summary>
/// Las operaciones de Windows que van por PowerShell: la regla NRPT del DNS local y la regla del
/// cortafuegos. Los scripts son fijos (<c>NrptScripts</c>, <c>FirewallRule</c>) y los ejecuta el PowerShell
/// del sistema por su ruta, nunca el del PATH, con <c>-EncodedCommand</c> (sin comillas que escapar).
/// </summary>
internal static class WindowsOps
{
    public static int Nrpt(string operation, string script)
    {
        var result = RunPowerShell(operation, script);
        if (result is null) return HelperExit.ScriptFailed;
        var (exitCode, output) = result.Value;
        switch (exitCode)
        {
            case 0:
                Log.Write($"{operation}: regla {NrptScripts.Namespace} -> {NrptScripts.Address} {(operation == "dns-enable" ? "puesta" : "quitada")}");
                return HelperExit.Ok;
            case NrptScripts.ForeignRuleExit:
                Log.Write($"{operation}: ya hay una regla para {NrptScripts.Namespace} que no es de Roost; no se toca");
                return HelperExit.ForeignRule;
            default:
                Log.Write($"{operation}: PowerShell salio con {exitCode}: {output}");
                return HelperExit.ScriptFailed;
        }
    }

    public static int Firewall(string operation, string portText, bool allow)
    {
        if (!FirewallRule.TryParsePort(portText, out var port))
        {
            // Como en hosts-sync, lo rechazado no se copia al registro: podria ser cualquier cosa.
            Log.Write($"rechazado: {operation} con un puerto que no vale");
            return HelperExit.BadInput;
        }

        var result = RunPowerShell(operation, allow ? FirewallRule.AllowScript(port) : FirewallRule.RemoveScript(port));
        if (result is null) return HelperExit.ScriptFailed;
        var (exitCode, output) = result.Value;
        if (exitCode != 0)
        {
            Log.Write($"{operation}: PowerShell salio con {exitCode}: {output}");
            return HelperExit.ScriptFailed;
        }
        Log.Write($"{operation}: regla \"{FirewallRule.DisplayName(port)}\" {(allow ? "puesta (TCP, perfil privado, subred local)" : "quitada")}");
        return HelperExit.Ok;
    }

    /// <summary>El codigo de salida del script y lo que escribio (stderr y stdout), o null si no se pudo ejecutar o no termino a tiempo (ya registrado).</summary>
    private static (int ExitCode, string Output)? RunPowerShell(string operation, string script)
    {
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
                Log.Write($"{operation}: PowerShell no termino en 60 s");
                return null;
            }
            return (process.ExitCode, $"{error.Result.Trim()} {output.Result.Trim()}");
        }
        catch (Exception ex)
        {
            Log.Write($"{operation}: no se pudo ejecutar PowerShell ({ex.Message})");
            return null;
        }
    }
}
