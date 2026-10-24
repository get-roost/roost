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

// El servidor web: nginx o Apache, los .conf de los sitios, las escuchas de compartir y su -t.
public sealed partial class StackPlanner
{
    /// <summary>
    /// Aparta como <c>.conf.removed</c> los <c>.conf</c> de <paramref name="sitesDir"/> cuyo sitio ya
    /// no está registrado: los que dejó una versión anterior al quitar un sitio con el otro
    /// servidor activo. Con <c>include sites/*.conf</c>, uno huérfano apunta a un certificado ya
    /// borrado y el <c>-t</c> fallaría por un sitio que no existe.
    /// </summary>
    private static void RetireOrphanConfs(string sitesDir, List<SitePlan> sitePlans)
    {
        foreach (var file in Directory.EnumerateFiles(sitesDir, "*.conf"))
        {
            // El patrón de Windows también casa con nombres más largos: se filtra a mano.
            if (!file.EndsWith(".conf", StringComparison.OrdinalIgnoreCase)) continue;
            var name = Path.GetFileNameWithoutExtension(file);
            if (sitePlans.Any(p => string.Equals(p.Site.Name, name, StringComparison.OrdinalIgnoreCase))) continue;
            File.Move(file, file + ".removed", overwrite: true);
        }
    }

    /// <summary>nginx.conf, un .conf por sitio, <c>nginx -t</c> y su servicio.</summary>
    private ServiceDefinition PrepareNginx(RoostConfig config, InstalledNginx nginx, List<NginxUpstream> upstreams, List<SitePlan> sitePlans, LocalCa? ca)
    {
        foreach (var dir in new[] { Path.Combine(paths.Etc, "nginx"), paths.NginxSites, Path.Combine(paths.Run, "nginx-temp"), SiteLogs.Directory(paths) })
            Directory.CreateDirectory(dir);
        RetireOrphanConfs(paths.NginxSites, sitePlans);

        // Un .conf por sitio: nuevo la primera vez; después solo se regenera el bloque de Roost.
        foreach (var plan in sitePlans)
        {
            var (certFile, keyFile) = ca!.EnsureSiteCert(plan.Site);
            var input = new SiteConf.Input(
                plan.Site, PhpPool.UpstreamName(plan.Php.Version!.Value), config.HttpPort, config.HttpsPort,
                certFile, keyFile, nginx.FastCgiParams, Loopback.HasIpv6) { LogsDirectory = SiteLogs.Directory(paths) };
            var file = Path.Combine(paths.NginxSites, plan.Site.Name + ".conf");
            File.WriteAllText(file, File.Exists(file) ? SiteConf.Merge(File.ReadAllText(file), input, file) : SiteConf.RenderNew(input));
        }

        // Compartir (oleada 6): las escuchas de lo que se está compartiendo, en su carpeta.
        WriteShareConfs(paths.NginxShares, sitePlans, ca, nginx.FastCgiParams, ShareConf.RenderNginx);

        var conf = NginxConfPath;
        File.WriteAllText(conf, NginxConf.Render(new NginxConfInput(
            nginx.MimeTypes,
            nginx.FastCgiParams,
            paths.Run,
            paths.Logs,
            paths.Www,
            config.HttpPort,
            upstreams,
            upstreams[0].Name,
            paths.NginxSites,
            Loopback.HasIpv6) { AdminerDirectory = AdminerAlias(), SharesDirectory = paths.NginxShares }));

        var test = TestNginx(nginx);
        if (test is null || !test.Contains("test is successful", StringComparison.OrdinalIgnoreCase))
            throw new RoostException($"nginx no acepta la configuracion generada ({conf}):\n{test?.Trim() ?? "nginx -t no respondio"}");

        return new ServiceDefinition
        {
            Name = "nginx",
            Executable = nginx.Executable,
            Arguments = NginxArguments,
            WorkingDirectory = nginx.Directory,
            LogFile = Path.Combine(paths.Logs, "nginx-stdout.log"),
            Port = config.HttpPort,
        };
    }

    /// <summary>
    /// httpd.conf, un .conf por sitio, <c>httpd -t</c> y su servicio. El PHP va por los mismos
    /// pools de php-cgi que nginx (<see cref="ApacheConf"/>).
    /// </summary>
    private ServiceDefinition PrepareApache(RoostConfig config, InstalledApache apache, List<NginxUpstream> pools, List<SitePlan> sitePlans, LocalCa? ca)
    {
        foreach (var dir in new[] { Path.Combine(paths.Etc, "apache"), paths.ApacheSites, SiteLogs.Directory(paths) })
            Directory.CreateDirectory(dir);
        RetireOrphanConfs(paths.ApacheSites, sitePlans);

        foreach (var plan in sitePlans)
        {
            var (certFile, keyFile) = ca!.EnsureSiteCert(plan.Site);
            var input = new ApacheSiteConf.Input(
                plan.Site, PhpPool.UpstreamName(plan.Php.Version!.Value), config.HttpPort, config.HttpsPort, certFile, keyFile, Loopback.HasIpv6)
            { LogsDirectory = SiteLogs.Directory(paths) };
            var file = Path.Combine(paths.ApacheSites, plan.Site.Name + ".conf");
            File.WriteAllText(file, File.Exists(file) ? ApacheSiteConf.Merge(File.ReadAllText(file), input, file) : ApacheSiteConf.RenderNew(input));
        }

        // Compartir (oleada 6): las escuchas de lo que se está compartiendo, con su propio Listen.
        WriteShareConfs(paths.ApacheShares, sitePlans, ca, null, ShareConf.RenderApache);

        var conf = ApacheConfPath;
        File.WriteAllText(conf, ApacheConf.Render(new ApacheConfInput(
            apache.Directory,
            paths.Run,
            paths.Logs,
            paths.Www,
            config.HttpPort,
            sitePlans.Count > 0 ? config.HttpsPort : null,
            pools,
            pools[0].Name,
            paths.ApacheSites,
            Loopback.HasIpv6) { AdminerDirectory = AdminerAlias(), SharesDirectory = paths.ApacheShares }));

        var test = TestApache(apache);
        if (test is null || !test.Contains("Syntax OK", StringComparison.OrdinalIgnoreCase))
            throw new RoostException($"Apache no acepta la configuracion generada ({conf}):\n{test?.Trim() ?? "httpd -t no respondio"}");

        // Apache se para matando su árbol de procesos y el pidfile se queda: sin borrarlo, cada
        // arranque avisaría en el log de un «Unclean shutdown» que no es tal.
        var pidFile = Path.Combine(paths.Run, "apache.pid");
        if (File.Exists(pidFile)) File.Delete(pidFile);

        return new ServiceDefinition
        {
            Name = "apache",
            Executable = apache.Executable,
            Arguments = ApacheArguments,
            WorkingDirectory = apache.Directory,
            LogFile = Path.Combine(paths.Logs, "apache-stdout.log"),
            Port = config.HttpPort,
        };
    }

    // Compartir (oleada 6)

    /// <summary>Las escuchas de <see cref="Shares"/> cuyo sitio sigue en el plan (las de un sitio quitado no se escriben).</summary>
    private IReadOnlyList<ShareListener> SharesOf(List<SitePlan> sitePlans) =>
        Shares.Where(s => sitePlans.Any(p => string.Equals(p.Site.Name, s.Site, StringComparison.OrdinalIgnoreCase))).ToList();

    /// <summary>
    /// Escribe en <paramref name="directory"/> un vhost por escucha de <see cref="Shares"/> (la de la red
    /// local, con el certificado del sitio que lleva su IP) y borra lo demás de esa carpeta: lo que se
    /// dejó de compartir no vuelve a escucharse. La carpeta existe siempre (el <c>include</c> la pide).
    /// </summary>
    private void WriteShareConfs(string directory, List<SitePlan> sitePlans, LocalCa? ca, string? fastCgiParams, Func<ShareConf.Input, string> render)
    {
        Directory.CreateDirectory(directory);
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var listener in SharesOf(sitePlans))
        {
            var plan = sitePlans.First(p => string.Equals(p.Site.Name, listener.Site, StringComparison.OrdinalIgnoreCase));
            string? cert = null, key = null, caFile = null;
            if (listener.Kind == ShareKind.Lan)
            {
                if (!IPAddress.TryParse(listener.Address, out var address))
                    throw new RoostException($"La direccion de la red local de {plan.Site.Host} no vale: {listener.Address}");
                (cert, key) = ca!.EnsureLanCert(plan.Site, address);
                caFile = ca.CaCertFile;
            }
            var file = Path.Combine(directory, listener.FileName);
            File.WriteAllText(file, render(new ShareConf.Input(
                listener, plan.Site, PhpPool.UpstreamName(plan.Php.Version!.Value), fastCgiParams, cert, key, caFile)));
            wanted.Add(file);
        }
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            if (!wanted.Contains(file)) File.Delete(file);
        }
    }

    /// <summary>El httpd.conf que genera cada <c>up</c> con Apache.</summary>
    public string ApacheConfPath => Path.Combine(paths.Etc, "apache", "httpd.conf");

    /// <summary>Los argumentos con los que se arranca (y se valida) Apache.</summary>
    public string ApacheArguments => $"-f \"{ApacheConf.Path(ApacheConfPath)}\"";

    /// <summary><c>httpd -t</c> sobre la configuración generada: su salida, o null si no responde.</summary>
    public string? TestApache(InstalledApache apache) => runner.Run(apache.Executable, "-t " + ApacheArguments, TimeSpan.FromSeconds(15));

    /// <summary>El nginx.conf que genera cada <c>up</c>.</summary>
    public string NginxConfPath => Path.Combine(paths.Etc, "nginx", "nginx.conf");

    /// <summary>
    /// Los argumentos con los que se arranca (y se valida) nginx. El prefijo es la raíz de Roost:
    /// nginx abre logs/error.log relativo a él antes de leer la configuración.
    /// </summary>
    public string NginxArguments =>
        $"-p \"{NginxConf.Nginx(paths.Root).TrimEnd('/')}/\" -c \"{NginxConf.Nginx(NginxConfPath)}\"";

    /// <summary><c>nginx -t</c> sobre la configuración generada: su salida, o null si no responde.</summary>
    public string? TestNginx(InstalledNginx nginx) => runner.Run(nginx.Executable, "-t " + NginxArguments, TimeSpan.FromSeconds(15));
}
