using System.Net;
using System.Net.Sockets;
using Roost.Core.Config;
using Roost.Core.Install;
using Roost.Core.Platform;
using Roost.Core.Scanning;
using Roost.Core.Services;
using Roost.Core.Share;
using Roost.Core.Sites;

namespace Roost.Core.Stack;

// La base de datos: cuál es la global, su my.ini y su datadir, y su servicio.
public sealed partial class StackPlanner
{
    /// <summary>
    /// La base de datos global: null con <c>[db] engine = "none"</c> o si Roost no tiene ninguna
    /// instalada; sin <c>[db]</c>, la más nueva (MariaDB antes que MySQL); con <c>[db]</c>, la más
    /// nueva que case, o un error si no hay ninguna.
    /// </summary>
    public InstalledDb? ResolveDb(RoostConfig config, IReadOnlyList<InstalledDb> installed)
    {
        if (config.DbEngine == RoostConfig.NoDb) return null;
        if (config.DbEngine is null && config.DbVersion is null) return installed.FirstOrDefault();

        DbEngine? engine = DbEngines.TryParse(config.DbEngine, out var e) ? e : null;
        var spec = new DbSpec(engine, config.DbVersion);
        return DbInstaller.Match(installed, spec).OrderByDescending(db => db.Version).FirstOrDefault()
            ?? throw new RoostException(
                $"{paths.ConfigFile} pide la base de datos {spec}, pero Roost no tiene ninguna instalada que case. "
                + $"Instalala (roost db install {spec}) o elige otra (roost db use <motor@version>, o roost db use none).");
    }

    /// <summary>my.ini de la base de datos de una rama: <c>etc/&lt;motor&gt;/&lt;rama&gt;/my.ini</c>.</summary>
    public string DbIniPath(InstalledDb db) => Path.Combine(paths.Etc, db.Engine.Key(), db.Branch!, "my.ini");

    /// <summary>
    /// Genera el my.ini de la base de datos (con la contraseña de root que guarda Roost en su
    /// <c>[client]</c> y el <c>usuario.cnf</c> del usuario incluido), crea su datadir la primera
    /// vez y devuelve su servicio. Se para limpio: se le pide <c>shutdown</c> con el admin y se espera.
    /// </summary>
    private ServiceDefinition PrepareDb(InstalledDb db, int port)
    {
        if (db.Version is null || db.Branch is not { } branch)
            throw new RoostException($"No se reconoce la version de la instalacion {db.Label}.");

        var ini = DbIni.Write(paths, db, port, new DbPasswords(paths).For(db));
        var datadir = DbIni.DataDir(paths, db);
        // Linux: el libaio que pide MySQL, enlazado antes de que arranque nada (también en una instalación de antes).
        DbInstaller.EnsureLibAio(db);
        EnsureDataDir(db, ini, datadir);

        var name = $"{db.Engine.Key()}-{branch}";
        var log = Path.Combine(paths.Logs, $"{name}.log");
        return new ServiceDefinition
        {
            Name = name,
            Executable = db.Server,
            // --console (solo Windows): el log del servidor va a stderr, que el supervisor guarda en el log. En
            // Linux va a stderr de serie mientras no se ponga log-error.
            Arguments = $"--defaults-file=\"{ini}\"" + (OperatingSystem.IsWindows() ? " --console" : ""),
            WorkingDirectory = db.Directory,
            LogFile = log,
            Port = port,
            StopCommand = new ServiceDefinition
            {
                Name = name + "-shutdown",
                Executable = db.Admin,
                Arguments = $"--defaults-file=\"{ini}\" shutdown",
                WorkingDirectory = db.Directory,
                LogFile = log,
            },
            StopTimeout = TimeSpan.FromSeconds(30),
        };
    }

    /// <summary>
    /// PURO: la orden con la que se crea el datadir. MariaDB en Windows: su <c>mariadb-install-db.exe</c>
    /// con el datadir. MariaDB en Linux: el script del bintar, con el fichero de opciones de Roost el
    /// primero (si no leería el <c>/etc/my.cnf</c> de la distro), <c>--basedir</c> (sin él, el script no
    /// encuentra sus ficheros fuera de la carpeta de instalación), root con contraseña normal (sin
    /// <c>normal</c>, root nace con <c>unix_socket</c> y solo entra el usuario de Linux que lo creó) y sin
    /// la base de datos <c>test</c>. MySQL, igual en los dos: <c>--initialize-insecure</c>.
    /// </summary>
    public static (string Executable, string Arguments) InitCommand(InstalledDb db, string ini, string datadir)
    {
        if (db.Engine == DbEngine.MySql)
            return (db.Server, $"--defaults-file=\"{ini}\" --initialize-insecure --datadir=\"{DbIni.Slashes(datadir)}\"");   // --defaults-file tiene que ir el primero; --datadir manda sobre el del fichero
        if (OperatingSystem.IsWindows())
            return (Path.Combine(db.Directory, "bin", OsNames.Exe("mariadb-install-db")), $"--datadir=\"{DbIni.Slashes(datadir)}\"");
        var script = new[] { Path.Combine(db.Directory, "scripts", "mariadb-install-db"), Path.Combine(db.Directory, "bin", "mariadb-install-db"), Path.Combine(db.Directory, "scripts", "mysql_install_db") }
            .FirstOrDefault(File.Exists) ?? Path.Combine(db.Directory, "scripts", "mariadb-install-db");
        return (script, $"--defaults-file=\"{ini}\" --basedir=\"{DbIni.Slashes(db.Directory)}\" --datadir=\"{DbIni.Slashes(datadir)}\" --auth-root-authentication-method=normal --skip-test-db");
    }

    /// <summary>
    /// Crea el datadir de la rama la primera vez (root sin contraseña), en una carpeta temporal
    /// que se renombra al final. Un datadir que ya existe no se toca nunca; si existe pero no
    /// tiene pinta de datadir, se para: Roost no toca datos que no reconoce.
    /// </summary>
    private void EnsureDataDir(InstalledDb db, string ini, string datadir)
    {
        if (Directory.Exists(Path.Combine(datadir, "mysql"))) return;
        if (Directory.Exists(datadir) && Directory.EnumerateFileSystemEntries(datadir).Any())
        {
            throw new RoostException(
                $"{datadir} existe pero no parece un datadir de {db.Engine.DisplayName()} (no tiene la carpeta mysql). "
                + "Revisalo a mano: Roost no toca datos que no reconoce.");
        }

        var parent = Path.GetDirectoryName(datadir)!;
        Directory.CreateDirectory(parent);
        var temp = Path.Combine(parent, InstallFiles.TempPrefix + Guid.NewGuid().ToString("N"));
        try
        {
            var (executable, arguments) = InitCommand(db, ini, temp);
            var output = runner.Run(executable, arguments, TimeSpan.FromMinutes(2));

            if (!Directory.Exists(Path.Combine(temp, "mysql")))
            {
                var tail = string.Join("\n  ", (output ?? "no respondio").Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).TakeLast(8));
                // Linux: si es una libreria que falta (libaio, libnuma), decir el paquete y no solo el error.
                var hint = LinuxDeps.MissingLibraryHint(output, LinuxDeps.OsRelease()) is { } missing ? $"\n  {missing}" : "";
                throw new RoostException($"No se pudo crear el datadir de {db.Label} en {datadir}:\n  {tail}{hint}");
            }

            if (Directory.Exists(datadir)) Directory.Delete(datadir); // estaba vacío
            Directory.Move(temp, datadir);
        }
        finally
        {
            InstallFiles.TryDeleteDirectory(temp);
        }
    }
}
