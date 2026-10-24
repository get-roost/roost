using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Roost.Core.Sites;

/// <summary>
/// Mantiene los <c>.test</c> de los sitios en el fichero <c>hosts</c>. Leerlo no necesita
/// privilegios; escribir el del sistema sí, así que lo hace <c>roost-helper</c> elevado (UAC),
/// y solo si el bloque cambia de verdad: un <c>site add</c> repetido no pide permiso.
///
/// La variable <c>ROOST_HOSTS_FILE</c> apunta a un hosts de pruebas, que Roost escribe
/// directamente, sin helper ni UAC. El helper elevado no la lee nunca: solo toca el del sistema.
/// </summary>
public sealed class HostsUpdater
{
    public const string HostsFileVariable = "ROOST_HOSTS_FILE";

    /// <summary>El hosts del sistema: en Windows, el de <c>System32\drivers\etc</c>; en Linux y macOS, <c>/etc/hosts</c>.</summary>
    public static string SystemHostsFile =>
        OperatingSystem.IsWindows() ? Path.Combine(Environment.SystemDirectory, "drivers", "etc", "hosts") : "/etc/hosts";

    public HostsUpdater()
    {
        var custom = Environment.GetEnvironmentVariable(HostsFileVariable);
        HostsFile = string.IsNullOrWhiteSpace(custom) ? SystemHostsFile : Path.GetFullPath(custom);
        IsSystemFile = string.IsNullOrWhiteSpace(custom);
    }

    public string HostsFile { get; }

    public bool IsSystemFile { get; }

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
    /// estaba así (no pide nada). Con el hosts del sistema lanza <paramref name="helperExe"/>
    /// elevado, pasándole los nombres en <paramref name="requestFile"/>.
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

        if (!OperatingSystem.IsWindows())
            throw new RoostException("Editar el hosts del sistema solo esta hecho para Windows por ahora.");
        if (!File.Exists(helperExe))
            throw new RoostException($"No se encuentra {helperExe}, que es el que edita el hosts con permisos de administrador.");

        Directory.CreateDirectory(Path.GetDirectoryName(requestFile)!);
        File.WriteAllLines(requestFile, wanted);
        var psi = new ProcessStartInfo(helperExe, $"hosts-sync \"{requestFile}\"")
        {
            UseShellExecute = true,
            Verb = "runas", // UAC
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        try
        {
            using var helper = Process.Start(psi) ?? throw new RoostException("No se pudo lanzar roost-helper.");
            helper.WaitForExit();
            if (helper.ExitCode != 0)
            {
                throw new RoostException(
                    $"roost-helper no pudo actualizar el hosts (codigo {helper.ExitCode}). Detalle en {HelperLog}.");
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED: «No» en el UAC
        {
            throw new RoostException(
                "Has cancelado el permiso de administrador: el hosts no se ha tocado y los .test nuevos no resolveran. "
                + "Repite el comando y acepta el aviso de Windows.");
        }

        if (!InSync(wanted))
            throw new RoostException($"roost-helper termino, pero {HostsFile} no tiene los nombres esperados. Detalle en {HelperLog}.");
        return true;
    }

    /// <summary>Donde deja su registro el helper (ruta fija: el helper no acepta rutas para escribir).</summary>
    public static string HelperLog =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Roost", "roost-helper.log");

    private static List<string> Normalize(IEnumerable<string> hostNames) =>
        hostNames.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
}
