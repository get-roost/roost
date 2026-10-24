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

// Los servicios del stack: Redis, Mailpit y la carpeta de Adminer.
public sealed partial class StackPlanner
{
    // Servicios (oleada 3): Redis, Mailpit y la carpeta de Adminer.

    /// <summary>
    /// Genera el redis.conf (con <c>conf.d/99-usuario.conf</c> la primera vez y <c>data/redis</c>, que
    /// nunca se borra) y añade el servicio <c>redis</c>. Arranca desde <c>etc/redis</c> con la ruta
    /// relativa al conf (ver <see cref="RedisConf"/>) y se para limpio con <c>redis-cli shutdown</c>,
    /// que vuelca el RDB y cierra el AOF; con la contraseña del usuario si ha puesto <c>requirepass</c>.
    /// </summary>
    private InstalledRedis PrepareRedis(RoostConfig config, List<ServiceDefinition> services)
    {
        var redis = new RedisInstaller(paths).Locate()
            ?? throw new RoostException(
                $"Redis va en el stack ([redis] enabled en {paths.ConfigFile}), pero no esta instalado. "
                + "Instalalo con: roost redis install (o quitalo del stack con: roost redis disable)");

        RedisConf.Write(paths, redis.Version, config.RedisPort);
        var log = Path.Combine(paths.Logs, "redis.log");
        var password = RedisConf.UserPassword(paths);
        services.Add(new ServiceDefinition
        {
            Name = "redis",
            Executable = redis.Server,
            Arguments = RedisConf.FileName,
            WorkingDirectory = RedisConf.Directory(paths),
            LogFile = log,
            Port = config.RedisPort,
            StopCommand = new ServiceDefinition
            {
                Name = "redis-shutdown",
                Executable = redis.Cli,
                Arguments = $"-h 127.0.0.1 -p {config.RedisPort} shutdown",
                WorkingDirectory = redis.Directory,
                // Por el entorno y no por -a: así la contraseña no sale en la línea de órdenes.
                Environment = password is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["REDISCLI_AUTH"] = password },
                LogFile = log,
            },
            StopTimeout = TimeSpan.FromSeconds(15),
        });
        return redis;
    }

    /// <summary>
    /// Añade el servicio <c>mailpit</c>: SMTP y bandeja web en 127.0.0.1, con la bandeja en
    /// <c>data/mailpit</c> (persistente; nunca se borra). Se para matándolo: guarda en SQLite y cada
    /// correo queda escrito al recibirlo.
    /// </summary>
    private InstalledMailpit PrepareMail(RoostConfig config, List<ServiceDefinition> services)
    {
        var mailpit = new MailpitInstaller(paths).Locate()
            ?? throw new RoostException(
                $"Mailpit va en el stack ([mail] enabled en {paths.ConfigFile}), pero no esta instalado. "
                + "Instalalo con: roost mail install (o quitalo del stack con: roost mail disable)");

        Directory.CreateDirectory(paths.MailpitData);
        services.Add(new ServiceDefinition
        {
            Name = "mailpit",
            Executable = mailpit.Executable,
            Arguments = MailConf.Arguments(config.MailSmtpPort, config.MailWebPort, MailConf.Database(paths)),
            WorkingDirectory = paths.MailpitData,
            LogFile = Path.Combine(paths.Logs, "mailpit.log"),
            Port = config.MailSmtpPort,
        });
        return mailpit;
    }

    /// <summary>
    /// La carpeta que el servidor web sirve en <c>http://localhost/adminer</c>: la de la versión de
    /// Adminer de esta Roost, aunque aún no esté instalada (así instalarlo desde la app no pide
    /// rearrancar el servidor web; mientras, PHP contesta que no hay fichero). Si está instalada, deja
    /// su entrada al día. null si el usuario tiene su propia <c>www/adminer</c>: esa manda.
    /// </summary>
    private string? AdminerAlias()
    {
        if (Directory.Exists(Path.Combine(paths.Www, "adminer"))) return null;
        var installer = new AdminerInstaller(paths);
        if (installer.Find(AdminerCatalog.Current) is { } adminer) AdminerInstaller.EnsureEntry(adminer);
        return installer.InstallDirectory(AdminerCatalog.Current);
    }
}
