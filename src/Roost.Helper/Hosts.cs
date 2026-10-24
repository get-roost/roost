using System.Text;
using Roost.Core.Platform;
using Roost.Core.Sites;

namespace Roost.Helper;

/// <summary>
/// <c>hosts-sync</c>: el bloque de Roost en el hosts del sistema. La peticion (un nombre por linea)
/// llega en Windows en un fichero y en Linux por stdin; en los dos casos, 64 KB y 1000 nombres como
/// mucho, y cada nombre tiene que pasar <c>HostsBlock.IsValidHost</c> (lo mismo que decide que se
/// escribe). El hosts es de ruta fija y solo se toca su bloque <c># roost:begin/end</c>
/// (<c>HostsBlock.Apply</c>, puro y probado), en Latin-1 para conservar byte a byte lo demas.
/// </summary>
internal static class Hosts
{
    public static string HostsFile => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.SystemDirectory, "drivers", "etc", "hosts")
        : "/etc/hosts";

    private const int MaxBytes = 64 * 1024;
    private const int MaxNames = 1000;

    /// <summary>Windows: los nombres del fichero de peticion; null si no vale (ya apuntado en el registro).</summary>
    public static List<string>? ReadRequestFile(string requestFile)
    {
        try
        {
            var request = new FileInfo(requestFile);
            if (!request.Exists || request.Length > MaxBytes)
            {
                Log.Write("rechazado: la peticion no existe o es demasiado grande");
                return null;
            }
            return Lines(File.ReadAllText(request.FullName));
        }
        catch (Exception ex)
        {
            Log.Write($"rechazado: no se pudo leer la peticion ({ex.GetType().Name})");
            return null;
        }
    }

    /// <summary>Linux: los nombres de stdin. Se lee hasta 64 KB y un byte: si hay mas, se rechaza entero.</summary>
    public static List<string>? ReadRequestStdin()
    {
        try
        {
            using var stdin = Console.OpenStandardInput();
            var buffer = new byte[MaxBytes + 1];
            var total = 0;
            int read;
            while (total < buffer.Length && (read = stdin.Read(buffer, total, buffer.Length - total)) > 0) total += read;
            if (total > MaxBytes)
            {
                Log.Write("rechazado: la peticion es demasiado grande");
                return null;
            }
            // Los nombres validos son ASCII; lo que no lo sea cae en IsValidHost.
            return Lines(Encoding.UTF8.GetString(buffer, 0, total));
        }
        catch (Exception ex)
        {
            Log.Write($"rechazado: no se pudo leer la peticion ({ex.GetType().Name})");
            return null;
        }
    }

    private static List<string> Lines(string text) =>
        text.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

    /// <summary>Deja en el bloque de Roost exactamente <paramref name="names"/> (ninguno = sin bloque).</summary>
    public static int Sync(List<string>? names)
    {
        if (names is null) return HelperExit.BadInput;
        if (names.Count > MaxNames || names.Any(n => !HostsBlock.IsValidHost(n)))
        {
            // No se registra el contenido rechazado: podria ser cualquier cosa.
            Log.Write($"rechazado: {names.Count} lineas, y no todas son un nombre .test valido");
            return HelperExit.BadInput;
        }

        var hostsFile = HostsFile;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var current = File.Exists(hostsFile) ? File.ReadAllText(hostsFile, Encoding.Latin1) : "";
                var updated = HostsBlock.Apply(current, names);
                // En Linux el hosts va con \n: HostsBlock solo pone \r\n si el fichero ya los tenia (o esta vacio).
                if (!OperatingSystem.IsWindows() && !current.Contains("\r\n", StringComparison.Ordinal))
                    updated = updated.Replace("\r\n", "\n", StringComparison.Ordinal);
                if (updated != current)
                {
                    if (OperatingSystem.IsWindows()) File.WriteAllText(hostsFile, updated, Encoding.Latin1);
                    else WriteAtomic(hostsFile, updated);
                }
                Log.Write($"hosts-sync: {names.Count} nombres ({string.Join(", ", names)})");
                return HelperExit.Ok;
            }
            catch (InvalidDataException ex)
            {
                Log.Write("rechazado: " + ex.Message);
                return HelperExit.BadInput;
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(200 * attempt); // un antivirus puede tener el hosts abierto un momento
            }
            catch (Exception ex)
            {
                Log.Write("error al escribir el hosts: " + ex.Message);
                return HelperExit.IoError;
            }
        }
    }

    /// <summary>Linux: un temporal en el mismo /etc (0644, root) y <c>rename</c> encima: el hosts nunca queda a medias.</summary>
    private static void WriteAtomic(string path, string text)
    {
        var temp = path + ".roost-tmp";
        File.WriteAllText(temp, text, Encoding.Latin1);
        File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        File.Move(temp, path, overwrite: true);
    }
}
