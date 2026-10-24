namespace Roost.Core.Platform;

/// <summary>
/// Las comprobaciones PURAS que hace <c>roost-helper</c> en Linux antes de tocar nada (el <c>setcap</c>
/// de nginx, la carpeta de proyectos, el uid que invoca): sobre rutas, bytes y textos, sin disco, para
/// que se prueben con entradas hostiles. Se compila también dentro del helper (enlazado como fuente),
/// así que no puede depender de nada más de Roost.
/// </summary>
internal static class HelperChecks
{
    /// <summary>Lo más larga que puede ser una ruta en Linux (<c>PATH_MAX</c>).</summary>
    public const int MaxPathLength = 4096;

    /// <summary>
    /// PURO: una ruta absoluta y limpia: empieza por <c>/</c>, no acaba en <c>/</c>, sin componentes
    /// vacíos (<c>//</c>), ni <c>.</c>, ni <c>..</c>, ni caracteres de control, y de 4096 como mucho.
    /// Lo que no sea así no se interpreta ni se normaliza: se rechaza.
    /// </summary>
    public static bool IsCleanAbsolutePath(string path)
    {
        if (path.Length is 0 or > MaxPathLength || path[0] != '/' || path.Length > 1 && path[^1] == '/') return false;
        if (path.Any(c => c < ' ' || c == '\x7f')) return false;
        return path.Split('/').Skip(1).All(part => part.Length > 0 && part != "." && part != "..");
    }

    /// <summary>PURO: la ruta cuelga de <paramref name="home"/> (las dos absolutas y limpias; la home no es <c>/</c>).</summary>
    public static bool IsUnder(string home, string path) =>
        home.Length > 1 && path.StartsWith(home.TrimEnd('/') + "/", StringComparison.Ordinal);

    /// <summary>PURO: el fichero se llama <c>nginx</c>, ni más ni menos (no <c>nginx.sh</c>, ni <c>Nginx</c>).</summary>
    public static bool IsNamedNginx(string path) => path.EndsWith("/nginx", StringComparison.Ordinal);

    /// <summary>PURO: empieza por la firma de un ejecutable ELF (<c>0x7F 'E' 'L' 'F'</c>): ni un script, ni un fichero vacío.</summary>
    public static bool IsElf(ReadOnlySpan<byte> head) =>
        head.Length >= 4 && head[0] == 0x7F && head[1] == (byte)'E' && head[2] == (byte)'L' && head[3] == (byte)'F';

    /// <summary>
    /// PURO: el uid que llega en <c>PKEXEC_UID</c> o <c>SUDO_UID</c>: de 1 a 10 cifras ASCII, sin signo,
    /// sin espacios y sin cero delante (salvo el propio 0), y que quepa en un <c>uid_t</c>.
    /// </summary>
    public static bool TryParseUid(string? text, out uint uid)
    {
        uid = 0;
        if (text is null || text.Length is 0 or > 10 || text.Length > 1 && text[0] == '0') return false;
        ulong value = 0;
        foreach (var c in text)
        {
            if (c is < '0' or > '9') return false;
            value = value * 10 + (ulong)(c - '0');
        }
        if (value > uint.MaxValue) return false;
        uid = (uint)value;
        return true;
    }

    /// <summary>El uid de este proceso en Linux (la primera columna de <c>Uid:</c> en <c>/proc/self/status</c>); null si no se sabe (o no es Linux).</summary>
    public static uint? CurrentUid()
    {
        if (!OperatingSystem.IsLinux()) return null;
        try
        {
            foreach (var line in File.ReadLines("/proc/self/status"))
            {
                if (!line.StartsWith("Uid:", StringComparison.Ordinal)) continue;
                var first = line[4..].Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                return TryParseUid(first, out var uid) ? uid : null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // sin /proc no hay respuesta
        }
        return null;
    }
}

/// <summary>
/// Los códigos de salida de <c>roost-helper</c>, los mismos en Windows y en Linux (sysexits donde
/// encajan). Roost traduce a texto los que le importan; el helper los documenta en su cabecera.
/// </summary>
internal static class HelperExit
{
    /// <summary>Bien.</summary>
    public const int Ok = 0;

    /// <summary>Uso: una operación que no está en la lista, o con los argumentos que no son.</summary>
    public const int Usage = 64;

    /// <summary>La petición no vale (un nombre que no es <c>.test</c>, una ruta que no cumple las reglas, un PEM que no es la CA de Roost).</summary>
    public const int BadInput = 65;

    /// <summary>Ya hay una regla de <c>.test</c> que no es de Roost (NRPT en Windows, otro drop-in de resolved en Linux): no se pisa.</summary>
    public const int ForeignRule = 69;

    /// <summary>La orden que ejecuta el helper (PowerShell, systemctl, setcap, update-ca-certificates, ufw) falló.</summary>
    public const int ScriptFailed = 70;

    /// <summary>Este sistema no tiene systemd-resolved: el DNS local no está disponible en esta distro.</summary>
    public const int NoResolved = 71;

    /// <summary>Lo que se iba a crear ya existe y es de otro (<c>/var/www/roost</c> de otro usuario).</summary>
    public const int Exists = 73;

    /// <summary>Error de E/S (por ejemplo, no está elevado y no puede escribir el hosts).</summary>
    public const int IoError = 74;

    /// <summary>En Linux, el helper no corre como root (hay que lanzarlo con pkexec o sudo).</summary>
    public const int NotRoot = 77;
}
