using System.Text;
using Roost.Core.Scanning;

namespace Roost.Core.Platform;

/// <summary>
/// Hablar con Windows desde un Linux de WSL (<see cref="Wsl.IsWsl"/>) por interop, es decir,
/// ejecutando <c>.exe</c> de Windows desde Linux. Ahí el navegador está en Windows, así que el
/// <c>hosts</c> y la CA que importan son los de Windows: el hosts lo edita el <c>roost-helper.exe</c>
/// de un Roost de Windows (lanzado con UAC desde PowerShell) y la CA entra en el almacén del usuario
/// de Windows con <c>certutil.exe -user -addstore Root</c>. Todo pasa por un <see cref="ICommandRunner"/>
/// y lo demás es puro: se prueba con dobles, nunca contra el Windows de verdad.
/// </summary>
public static class WslInterop
{
    /// <summary>Donde instala el instalador de Windows (Program Files). Scoop y winget van por usuario y desde aquí no se sabe cuál.</summary>
    public const string WindowsRoostDir = "/mnt/c/Program Files/Roost";

    public const string PowerShell = "/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe";
    public const string Certutil = "/mnt/c/Windows/System32/certutil.exe";

    /// <summary>La variable con el nombre de la distro, que WSL pone en todos los procesos.</summary>
    public const string DistroVariable = "WSL_DISTRO_NAME";

    /// <summary>El nombre de esta distro, con el que Windows ve sus ficheros (<c>\\wsl.localhost\&lt;distro&gt;\…</c>); null fuera de WSL.</summary>
    public static string? Distro => Environment.GetEnvironmentVariable(DistroVariable) is { Length: > 0 } name ? name : null;

    /// <summary>El <c>roost-helper.exe</c> de un Roost de Windows instalado con su instalador; null si no lo hay.</summary>
    public static string? WindowsHelper(Func<string, bool>? exists = null)
    {
        var helper = WindowsRoostDir + "/roost-helper.exe";
        return (exists ?? File.Exists)(helper) ? helper : null;
    }

    /// <summary>
    /// PURO: cómo ve Windows una ruta de este Linux: <c>/mnt/c/x/y</c> es <c>C:\x\y</c>, y lo que no está
    /// en una unidad montada se ve como <c>\\wsl.localhost\&lt;distro&gt;\ruta</c>. null si no se sabe la distro.
    /// </summary>
    public static string? ToWindowsPath(string linuxPath, string? distro)
    {
        if (linuxPath.Length >= 7 && linuxPath.StartsWith("/mnt/", StringComparison.Ordinal) && char.IsAsciiLetterLower(linuxPath[5]) && linuxPath[6] == '/')
            return char.ToUpperInvariant(linuxPath[5]) + ":" + linuxPath[6..].Replace('/', '\\');
        if (string.IsNullOrEmpty(distro) || !linuxPath.StartsWith('/')) return null;
        return @"\\wsl.localhost\" + distro + linuxPath.Replace('/', '\\');
    }

    /// <summary>PURO: la ruta de este Linux de una de Windows con letra de unidad (<c>C:\x</c> → <c>/mnt/c/x</c>); null si no la tiene.</summary>
    public static string? ToLinuxPath(string windowsPath)
    {
        if (windowsPath.Length < 3 || !char.IsAsciiLetter(windowsPath[0]) || windowsPath[1] != ':' || windowsPath[2] is not ('\\' or '/')) return null;
        return "/mnt/" + char.ToLowerInvariant(windowsPath[0]) + windowsPath[2..].Replace('\\', '/');
    }

    /// <summary>
    /// PURO: el script de PowerShell que lanza un exe de Windows elevado (el UAC, como <c>Verb = runas</c>)
    /// y sale con su código. 126 si el usuario dice que no al UAC (el error 1223, como pkexec) y 127 si
    /// no se pudo lanzar. Los argumentos con espacios van entre comillas dobles: <c>Start-Process</c>
    /// los une con espacios tal cual.
    /// </summary>
    public static string RunAsScript(string exe, IReadOnlyList<string> arguments)
    {
        var list = arguments.Count == 0 ? "" : $" -ArgumentList @({string.Join(", ", arguments.Select(a => PsQuote(ArgQuote(a))))})";
        return $$"""
            $ErrorActionPreference = 'Stop'
            try {
              $p = Start-Process -FilePath {{PsQuote(exe)}}{{list}} -Verb RunAs -Wait -PassThru -WindowStyle Hidden
              exit $p.ExitCode
            } catch {
              $inner = $_.Exception.InnerException
              if ($inner -is [System.ComponentModel.Win32Exception] -and $inner.NativeErrorCode -eq 1223) { exit 126 }
              [Console]::Error.WriteLine($_.Exception.Message)
              exit 127
            }
            """;
    }

    /// <summary>La orden: el PowerShell de Windows por su ruta con el script en <c>-EncodedCommand</c> (UTF-16LE en base64, sin comillas que escapar).</summary>
    public static CommandSpec RunAs(string exe, IReadOnlyList<string> arguments) => new(
        PowerShell,
        ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(RunAsScript(exe, arguments)))],
        Timeout: TimeSpan.FromMinutes(5));

    /// <summary>Una cadena de PowerShell entre comillas simples (la única que se escapa es la propia comilla, doblándola).</summary>
    public static string PsQuote(string value) => "'" + value.Replace("'", "''") + "'";

    private static string ArgQuote(string argument) =>
        argument.Length > 0 && !argument.Any(char.IsWhiteSpace) ? argument : "\"" + argument.Replace("\"", "\\\"") + "\"";
}
