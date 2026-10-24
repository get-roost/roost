using System.Diagnostics;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Roost.Core.Dns;
using Roost.Core.Platform;
using Roost.Core.Share;
using Roost.Core.Sites;

namespace Roost.Helper;

/// <summary>
/// Las operaciones de Linux del helper (como root). Cada una valida lo que le llega antes de tocar
/// nada y solo escribe en rutas fijas; las ordenes externas van por su ruta, con un entorno limpio y
/// un PATH fijo. Lo que se rechaza se apunta en el registro sin copiar el contenido.
/// </summary>
internal static class LinuxOps
{
    // Las ordenes que ejecuta el helper, por su ruta: nada del PATH (con usrmerge, /bin y /sbin son /usr/bin y /usr/sbin).
    private const string Systemctl = "/usr/bin/systemctl";
    private const string Setcap = "/usr/sbin/setcap";
    private const string UpdateCaCertificates = "/usr/sbin/update-ca-certificates";

    private const string WwwPath = "/var/www/roost";
    private const string SystemCaDirectory = "/usr/local/share/ca-certificates";

    // ---------------------------------------------------------------------------------------------
    // dns-enable / dns-disable: el drop-in de systemd-resolved.
    // ---------------------------------------------------------------------------------------------

    public static int DnsEnable()
    {
        if (WhyNoResolved() is { } why)
        {
            Log.Write("dns-enable: " + why);
            return HelperExit.NoResolved;
        }

        // 1) Si otro fichero de resolved (el principal u otro drop-in) ya enruta "test", no se pisa: otra
        //    herramienta resuelve esos nombres. El de Roost se reconoce por su primera linea.
        var foreign = ResolvedDropIn.ReadFiles().FirstOrDefault(e => !e.IsRoost && e.RoutesTest);
        if (foreign is not null)
        {
            Log.Write($"dns-enable: {foreign.File} ya enruta test y no es de Roost; no se toca");
            return HelperExit.ForeignRule;
        }

        // 2) El drop-in, con su contenido fijo, escrito en un temporal de la misma carpeta y renombrado.
        try
        {
            Directory.CreateDirectory(ResolvedDropIn.Directory);
            LinuxFiles.WriteAtomic(ResolvedDropIn.FilePath, ResolvedDropIn.Content);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write($"dns-enable: no se pudo escribir {ResolvedDropIn.FilePath}: {ex.Message}");
            return HelperExit.IoError;
        }

        // 3) Que resolved lo lea.
        return RestartResolved("dns-enable", $"drop-in {ResolvedDropIn.FilePath} puesto ({ResolvedDropIn.Server}, ~test)");
    }

    public static int DnsDisable()
    {
        // Se quita solo el fichero de Roost (ruta fija); lo demas de resolved no se toca.
        var existed = File.Exists(ResolvedDropIn.FilePath);
        if (existed)
        {
            try
            {
                File.Delete(ResolvedDropIn.FilePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Write($"dns-disable: no se pudo borrar {ResolvedDropIn.FilePath}: {ex.Message}");
                return HelperExit.IoError;
            }
        }
        if (WhyNoResolved() is not null || !existed)
        {
            Log.Write(existed ? "dns-disable: drop-in quitado (sin systemd-resolved que reiniciar)" : "dns-disable: no habia drop-in de Roost");
            return HelperExit.Ok;
        }
        return RestartResolved("dns-disable", "drop-in quitado");
    }

    /// <summary>null si hay systemd-resolved en marcha; si no, por que no (Debian 12 de serie, un WSL sin systemd, un contenedor).</summary>
    private static string? WhyNoResolved()
    {
        if (!Directory.Exists("/run/systemd/system")) return "este sistema no arranca con systemd";
        if (!File.Exists(Systemctl)) return $"no hay {Systemctl}";
        if (!Directory.Exists("/run/systemd/resolve")) return "systemd-resolved no esta en marcha";
        return null;
    }

    private static int RestartResolved(string operation, string done)
    {
        var (exitCode, output) = Run(Systemctl, ["restart", "systemd-resolved.service"]);
        if (exitCode == 0)
        {
            Log.Write($"{operation}: {done}; systemd-resolved reiniciado");
            return HelperExit.Ok;
        }
        Log.Write($"{operation}: systemctl salio con {exitCode}: {output}");
        return exitCode == 5 ? HelperExit.NoResolved : HelperExit.ScriptFailed;   // 5: la unidad no existe
    }

    // ---------------------------------------------------------------------------------------------
    // setcap-nginx <ruta>: que el nginx de Roost escuche en el 80 y el 443 sin ser root.
    // ---------------------------------------------------------------------------------------------

    public static int SetcapNginx(string path)
    {
        // 1) La ruta tal cual: absoluta, limpia (sin //, ., .. ni caracteres de control) y que se llame
        //    nginx. Nada se normaliza ni se adivina: lo raro se rechaza.
        if (!HelperChecks.IsCleanAbsolutePath(path) || !HelperChecks.IsNamedNginx(path))
            return Rejected("setcap-nginx: la ruta no es absoluta y limpia, o no se llama nginx");

        // 2) Quien lo pide y su carpeta personal, por getpwuid (nunca por $HOME, que lo pone quien invoca),
        //    y esa carpeta en su forma canonica, para compararla con la ruta real del fichero.
        var user = Posix.UserOf(Posix.InvokerUid());
        if (user is null) return Rejected("setcap-nginx: no se sabe que usuario lo pide");
        var home = Posix.RealPath(user.Home);
        if (home is null || home == "/") return Rejected($"setcap-nginx: {user.Name} no tiene una carpeta personal que valga");

        if (!File.Exists(Setcap))
        {
            Log.Write($"setcap-nginx: no hay {Setcap} (instala libcap2-bin)");
            return HelperExit.ScriptFailed;
        }

        // 3) Se abre SIN seguir un enlace en el ultimo componente. Desde aqui todo se mira sobre el
        //    descriptor abierto, no sobre la ruta: nadie puede cambiarnos el fichero entre la comprobacion
        //    y el setcap, porque el setcap se aplica a ese mismo descriptor.
        var fd = Posix.OpenNoFollow(path);
        if (fd < 0) return Rejected($"setcap-nginx: no se puede abrir {path} (errno {Posix.Errno}: no existe, es un enlace o no hay permiso)");
        try
        {
            // 4) La ruta real de lo abierto tiene que ser exactamente la pedida (si no, habia un enlace en
            //    el camino) y colgar de la carpeta personal del usuario.
            var real = Posix.RealPathOf(fd);
            if (real != path) return Rejected("setcap-nginx: hay un enlace en el camino (la ruta real no es la pedida)");
            if (!HelperChecks.IsUnder(home, real)) return Rejected($"setcap-nginx: {path} no esta dentro de la carpeta personal de {user.Name}");

            // 5) Un fichero normal (ni carpeta, ni dispositivo, ni socket) y del usuario que lo pide.
            var status = Posix.FStat(fd);
            if (status is null || !status.IsRegular) return Rejected("setcap-nginx: no es un fichero normal");
            if (status.Uid != user.Uid) return Rejected($"setcap-nginx: el fichero no es de {user.Name} (es del uid {status.Uid})");

            // 6) Un ejecutable ELF (no un script ni un fichero vacio).
            using (var stream = new FileStream(new SafeFileHandle((IntPtr)fd, ownsHandle: false), FileAccess.Read, 1, isAsync: false))
            {
                var head = new byte[4];
                var read = stream.ReadAtLeast(head, 4, throwOnEndOfStream: false);
                if (!HelperChecks.IsElf(head.AsSpan(0, read))) return Rejected("setcap-nginx: no es un ejecutable ELF");
            }

            // 7) setcap sobre la ruta (setcap no admite /proc/<pid>/fd/N: «Invalid file for capability
            //    operation») y, despues, comprobar que lo que hay en la ruta sigue siendo el fichero que se
            //    abrio y se comprobo: mismo dispositivo e inodo, fichero normal y del mismo usuario. Si alguien
            //    lo cambio en medio (un enlace o otro fichero colado entre la comprobacion y el setcap), se le
            //    quita la capability a lo que haya ahora y se rechaza.
            var (exitCode, output) = Run(Setcap, ["cap_net_bind_service=+ep", path]);
            if (exitCode != 0)
            {
                Log.Write($"setcap-nginx: setcap salio con {exitCode}: {output}");
                return HelperExit.ScriptFailed;
            }
            var after = Posix.LStat(path);
            if (after is null || !after.IsRegular || after.Uid != user.Uid || !after.SameFile(status))
            {
                Run(Setcap, ["-r", path]);
                return Rejected($"setcap-nginx: {path} cambio durante la operacion; se ha deshecho");
            }
            Log.Write($"setcap-nginx: cap_net_bind_service=+ep en {path} (de {user.Name})");
            return HelperExit.Ok;
        }
        finally
        {
            Posix.Close(fd);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // www-init: la carpeta de proyectos compartida, /var/www/roost, del usuario que lo pide.
    // ---------------------------------------------------------------------------------------------

    public static int WwwInit()
    {
        var user = Posix.UserOf(Posix.InvokerUid());
        if (user is null) return Rejected("www-init: no se sabe que usuario lo pide");

        // Si ya existe (sin seguir enlaces): suya y carpeta, nada que hacer; de otro, o un enlace o un fichero, no se toca.
        var existing = Posix.LStat(WwwPath);
        if (existing is not null)
        {
            if (existing.IsDirectory && existing.Uid == user.Uid)
            {
                Log.Write($"www-init: {WwwPath} ya existia y es de {user.Name}");
                return HelperExit.Ok;
            }
            Log.Write($"www-init: {WwwPath} ya existe y {(existing.IsDirectory ? $"es de otro usuario (uid {existing.Uid})" : "no es una carpeta")}; no se toca");
            return HelperExit.Exists;
        }

        try
        {
            // /var/www puede no existir (sin apache2 ni nginx de la distro): de root, como la pondria un paquete.
            var parent = Path.GetDirectoryName(WwwPath)!;
            if (!Directory.Exists(parent))
            {
                Directory.CreateDirectory(parent);
                LinuxFiles.SetMode(parent, LinuxFiles.Mode755);
            }
            Directory.CreateDirectory(WwwPath);
            LinuxFiles.SetMode(WwwPath, LinuxFiles.Mode755);
            if (!Posix.Chown(WwwPath, user.Uid, user.Gid))
            {
                Log.Write($"www-init: chown de {WwwPath} fallo (errno {Posix.Errno})");
                return HelperExit.IoError;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write($"www-init: no se pudo crear {WwwPath}: {ex.Message}");
            return HelperExit.IoError;
        }
        Log.Write($"www-init: {WwwPath} creada (0755), dueno {user.Name}");
        return HelperExit.Ok;
    }

    // ---------------------------------------------------------------------------------------------
    // ca-trust / ca-untrust: la CA de Roost como CA del sistema (opcional).
    // ---------------------------------------------------------------------------------------------

    public static int CaTrust()
    {
        // 1) El PEM por stdin, acotado. 2) Tiene que ser una CA local de Roost (CaCertRules, puro y probado).
        var pem = Stdin.ReadAll(CaCertRules.MaxPemLength);
        if (pem is null) return Rejected("ca-trust: no se pudo leer el PEM o es demasiado grande");
        using var cert = CaCertRules.Parse(pem, out var problem);
        if (cert is null) return Rejected("ca-trust: " + problem);

        var user = Posix.UserOf(Posix.InvokerUid());
        if (user is null) return Rejected("ca-trust: no se sabe que usuario lo pide");
        var file = SystemCaFile(user.Uid);

        // 3) Un fichero por usuario, de nombre fijo, con el PEM ya reserializado (no los bytes que llegaron).
        try
        {
            Directory.CreateDirectory(SystemCaDirectory);
            LinuxFiles.WriteAtomic(file, cert.ExportCertificatePem() + "\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write($"ca-trust: no se pudo escribir {file}: {ex.Message}");
            return HelperExit.IoError;
        }

        // 4) Que el sistema lo incorpore (/etc/ssl/certs y el bundle).
        if (!File.Exists(UpdateCaCertificates))
        {
            Log.Write($"ca-trust: no hay {UpdateCaCertificates} (paquete ca-certificates)");
            return HelperExit.ScriptFailed;
        }
        var (exitCode, output) = Run(UpdateCaCertificates, []);
        if (exitCode != 0)
        {
            Log.Write($"ca-trust: update-ca-certificates salio con {exitCode}: {output}");
            return HelperExit.ScriptFailed;
        }
        Log.Write($"ca-trust: {cert.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false)} ({cert.Thumbprint}) en {file}");
        return HelperExit.Ok;
    }

    public static int CaUntrust()
    {
        var user = Posix.UserOf(Posix.InvokerUid());
        if (user is null) return Rejected("ca-untrust: no se sabe que usuario lo pide");
        var file = SystemCaFile(user.Uid);
        if (!File.Exists(file))
        {
            Log.Write($"ca-untrust: no habia {file}");
            return HelperExit.Ok;
        }
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write($"ca-untrust: no se pudo borrar {file}: {ex.Message}");
            return HelperExit.IoError;
        }
        if (File.Exists(UpdateCaCertificates))
        {
            // Con --fresh: sin el, update-ca-certificates deja colgando en /etc/ssl/certs los enlaces de un
            // .crt que ya no esta (probado en Ubuntu 24.04); --fresh rehace los enlaces y el bundle desde cero.
            var (exitCode, output) = Run(UpdateCaCertificates, ["--fresh"]);
            if (exitCode != 0)
            {
                Log.Write($"ca-untrust: update-ca-certificates salio con {exitCode}: {output}");
                return HelperExit.ScriptFailed;
            }
        }
        Log.Write($"ca-untrust: {file} quitado");
        return HelperExit.Ok;
    }

    private static string SystemCaFile(uint uid) => $"{SystemCaDirectory}/roost-{uid}.crt";

    // ---------------------------------------------------------------------------------------------
    // firewall-allow / firewall-remove <puerto>: ufw o firewalld, solo si estan activos.
    // ---------------------------------------------------------------------------------------------

    public static int Firewall(string operation, string portText, bool allow)
    {
        // Lo unico que llega de fuera es el puerto: 80, 443 o de 1024 a 65535, sin los de administracion
        // remota ni los de bases de datos (FirewallRule.TryParsePort). Lo rechazado no se copia al registro.
        if (!FirewallRule.TryParsePort(portText, out var port))
            return Rejected($"{operation} con un puerto que no vale");

        var done = new List<string>();
        if (UfwActive())
        {
            foreach (var network in FirewallRule.PrivateNetworks)
            {
                var (exitCode, output) = Run(FirewallRule.UfwPath, FirewallRule.UfwArguments(port, network, allow));
                // Al quitar, una regla que ya no esta no es un fallo.
                if (exitCode != 0 && allow)
                {
                    Log.Write($"{operation}: ufw salio con {exitCode}: {output}");
                    return HelperExit.ScriptFailed;
                }
            }
            done.Add("ufw");
        }

        if (FirewalldActive())
        {
            foreach (var network in FirewallRule.PrivateNetworks)
            {
                foreach (var permanent in new[] { false, true })
                {
                    var (exitCode, output) = Run(FirewallRule.FirewalldPath, FirewallRule.FirewalldArguments(port, network, allow, permanent));
                    if (exitCode != 0 && allow)
                    {
                        Log.Write($"{operation}: firewall-cmd salio con {exitCode}: {output}");
                        return HelperExit.ScriptFailed;
                    }
                }
            }
            done.Add("firewalld");
        }

        if (done.Count == 0)
        {
            Log.Write($"{operation}: ningun cortafuegos activo (ufw, firewalld): nada que {(allow ? "abrir" : "cerrar")}");
            return HelperExit.Ok;
        }
        Log.Write($"{operation}: regla \"{FirewallRule.DisplayName(port)}\" {(allow ? "puesta" : "quitada")} en {string.Join(" y ", done)} (TCP, solo desde redes privadas)");
        return HelperExit.Ok;
    }

    private static bool UfwActive()
    {
        if (!File.Exists(FirewallRule.UfwPath)) return false;
        var (exitCode, output) = Run(FirewallRule.UfwPath, ["status"]);
        return exitCode == 0 && output.TrimStart().StartsWith("Status: active", StringComparison.Ordinal);
    }

    private static bool FirewalldActive() =>
        File.Exists(FirewallRule.FirewalldPath) && Run(FirewallRule.FirewalldPath, ["--state"]).ExitCode == 0;

    // ---------------------------------------------------------------------------------------------

    private static int Rejected(string why)
    {
        Log.Write("rechazado: " + why);
        return HelperExit.BadInput;
    }

    /// <summary>
    /// Ejecuta una orden por su ruta, con el entorno vacio salvo un PATH fijo (update-ca-certificates y
    /// ufw son scripts que llaman a otras cosas) y <c>LC_ALL=C</c>, y 60 s como mucho. Devuelve su codigo
    /// (70 si no se pudo lanzar o no acabo a tiempo) y lo que escribio.
    /// </summary>
    private static (int ExitCode, string Output) Run(string exe, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment.Clear();
        start.Environment["PATH"] = "/usr/sbin:/usr/bin:/sbin:/bin";
        start.Environment["LC_ALL"] = "C";
        try
        {
            using var process = Process.Start(start)!;
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(60_000))
            {
                process.Kill(entireProcessTree: true);
                return (HelperExit.ScriptFailed, $"{Path.GetFileName(exe)} no termino en 60 s");
            }
            return (process.ExitCode, $"{error.Result.Trim()} {output.Result.Trim()}".Trim());
        }
        catch (Exception ex)
        {
            return (HelperExit.ScriptFailed, $"no se pudo ejecutar {exe} ({ex.Message})");
        }
    }
}

/// <summary>Leer la peticion de stdin acotada: si trae mas de <c>maxBytes</c>, se rechaza entera (null).</summary>
internal static class Stdin
{
    public static string? ReadAll(int maxBytes)
    {
        try
        {
            using var stdin = Console.OpenStandardInput();
            var buffer = new byte[maxBytes + 1];
            var total = 0;
            int read;
            while (total < buffer.Length && (read = stdin.Read(buffer, total, buffer.Length - total)) > 0) total += read;
            return total > maxBytes ? null : Encoding.UTF8.GetString(buffer, 0, total);
        }
        catch (IOException)
        {
            return null;
        }
    }
}

/// <summary>Escribir ficheros del sistema sin dejarlos a medias: un temporal en la misma carpeta y <c>rename</c> encima.</summary>
internal static class LinuxFiles
{
    public const UnixFileMode Mode644 = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
    public const UnixFileMode Mode755 = Mode644 | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

    public static void WriteAtomic(string path, string text, UnixFileMode mode = Mode644)
    {
        var temp = path + ".roost-tmp";
        File.WriteAllText(temp, text);
        SetMode(temp, mode);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Los permisos POSIX de un fichero. Solo se llama desde las operaciones de Linux (Program.cs despacha por sistema), de ahí que no haga falta la guarda del analizador.</summary>
    public static void SetMode(string path, UnixFileMode mode)
    {
#pragma warning disable CA1416 // solo en Linux
        File.SetUnixFileMode(path, mode);
#pragma warning restore CA1416
    }
}
