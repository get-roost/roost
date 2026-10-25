using System.Text;
using Roost.Core.Sites;

namespace Roost.Core.Stack;

/// <summary>Lo que necesita <see cref="ApacheConf.Render"/>; todas las rutas, absolutas.</summary>
/// <param name="Ipv6">Escuchar también en <c>[::1]</c> (false en una máquina sin IPv6 en loopback).</param>
public sealed record ApacheConfInput(
    string ServerRoot,
    string RunDirectory,
    string LogsDirectory,
    string Www,
    int HttpPort,
    int? HttpsPort,
    IReadOnlyList<NginxUpstream> Pools,
    string DefaultPool,
    string SitesDirectory,
    bool Ipv6 = true)
{
    /// <summary>
    /// La carpeta de Adminer (<c>bin/adminer/&lt;versión&gt;</c>) que se sirve en <c>/adminer</c> con el
    /// PHP global; null = sin Adminer (el usuario tiene su propia <c>www/adminer</c>).
    /// </summary>
    public string? AdminerDirectory { get; init; }

    /// <summary>
    /// Compartir (oleada 6): la carpeta de las escuchas de lo que se está compartiendo
    /// (<c>etc/apache/share</c>, <c>Share/ShareConf</c>), que se incluye después de los sitios; null = sin ella.
    /// </summary>
    public string? SharesDirectory { get; init; }

    /// <summary>
    /// Linux: la carpeta de los módulos de la distro (<c>/usr/lib/apache2/modules</c>), con la que cada
    /// <c>LoadModule</c> va con su ruta absoluta; null = Windows (<c>modules/</c> bajo el ServerRoot).
    /// </summary>
    public string? ModulesDirectory { get; init; }

    /// <summary>Linux: el fichero de tipos MIME (<c>/etc/mime.types</c>); null = <c>conf/mime.types</c> del ServerRoot.</summary>
    public string? MimeTypesFile { get; init; }

    /// <summary>
    /// Linux: los módulos que se cargan (<see cref="ApacheConf.ModulesPresent"/>: los de <see cref="ApacheConf.LinuxModules"/>
    /// cuyo <c>.so</c> existe; los que la distro compila dentro, como <c>unixd</c> y <c>log_config</c> en Debian, no
    /// tienen <c>.so</c> y no se cargan). null = la lista de Windows (<see cref="ApacheConf.Modules"/>).
    /// </summary>
    public IReadOnlyList<string>? ModulesToLoad { get; init; }

    /// <summary>true si es el Apache de la distro en Linux (<c>mpm_event</c>, rutas absolutas, sin <c>User</c>/<c>Group</c>).</summary>
    public bool IsUnix => ModulesDirectory is not null;
}

/// <summary>
/// El httpd.conf que genera Roost para Apache. El PHP va por los mismos pools de php-cgi que
/// nginx (<c>mod_proxy_fcgi</c> con un balanceador por pool), no por mod_php: así sirve el PHP NTS
/// de siempre y cada sitio puede tener el suyo, que con mod_php sería uno solo para todo Apache.
/// Lee los <c>.htaccess</c> (<c>AllowOverride All</c> y mod_rewrite): es por lo que se elige
/// Apache. Escucha solo en loopback (127.0.0.1 y, si lo hay, [::1]), como nginx.
/// </summary>
public static class ApacheConf
{
    /// <summary>Las directivas <c>Listen</c> de un puerto: 127.0.0.1 y, con IPv6, también [::1].</summary>
    public static string Listen(int port, bool ipv6) =>
        $"Listen 127.0.0.1:{port}\n" + (ipv6 ? $"Listen [::1]:{port}\n" : "");

    /// <summary>Las direcciones de un <c>&lt;VirtualHost&gt;</c> de un puerto.</summary>
    public static string VirtualHost(int port, bool ipv6) =>
        $"127.0.0.1:{port}" + (ipv6 ? $" [::1]:{port}" : "");

    /// <summary>
    /// Los módulos que se cargan: lo justo para servir PHP y los <c>.htaccess</c> habituales
    /// (WordPress, Laravel, los de Apache 2.2 con <c>Order allow,deny</c>), más TLS y HTTP/2.
    /// </summary>
    public static readonly IReadOnlyList<string> Modules =
    [
        "access_compat", "alias", "auth_basic", "authn_core", "authn_file", "authz_core", "authz_groupfile",
        "authz_host", "authz_user", "autoindex", "deflate", "dir", "env", "expires", "filter", "headers",
        "http2", "lbmethod_byrequests", "log_config", "mime", "negotiation", "proxy", "proxy_balancer",
        "proxy_fcgi", "rewrite", "setenvif", "slotmem_shm", "socache_shmcb", "ssl",
    ];

    /// <summary>
    /// Linux: los mismos más el MPM (<c>mpm_event</c>, explícito: en Unix no va compilado dentro como el
    /// <c>mpm_winnt</c> de Windows) y <c>unixd</c> (obligatorio en Unix; sin <c>User</c>/<c>Group</c> no cambia
    /// de usuario). El orden importa: el MPM y unixd, los primeros.
    /// </summary>
    public static readonly IReadOnlyList<string> LinuxModules = ["mpm_event", "unixd", .. Modules];

    /// <summary>Linux: sin estos no hay PHP, ni .htaccess, ni HTTPS: si falta su <c>.so</c>, no se arranca.</summary>
    public static readonly IReadOnlyList<string> RequiredModules = ["mpm_event", "proxy", "proxy_fcgi", "proxy_balancer", "rewrite", "ssl"];

    /// <summary>
    /// PURO: de <see cref="LinuxModules"/>, los que tienen su <c>mod_&lt;nombre&gt;.so</c> en <paramref name="modulesDirectory"/>.
    /// Los que la distro compila dentro del binario (Debian: <c>unixd</c>, <c>log_config</c>) no tienen <c>.so</c>
    /// y no hay que cargarlos; cargar uno que no existe es un error al arrancar.
    /// </summary>
    public static IReadOnlyList<string> ModulesPresent(string modulesDirectory, Func<string, bool> fileExists) =>
        LinuxModules.Where(m => fileExists(System.IO.Path.Combine(modulesDirectory, $"mod_{m}.so"))).ToList();

    /// <summary>PURO: los de <see cref="RequiredModules"/> que no están en <paramref name="present"/> (vacío = se puede arrancar).</summary>
    public static IReadOnlyList<string> MissingRequired(IReadOnlyList<string> present) =>
        RequiredModules.Where(m => !present.Contains(m)).ToList();

    /// <summary>PURO: genera el texto del httpd.conf.</summary>
    public static string Render(ApacheConfInput input)
    {
        var run = Path(input.RunDirectory);
        var logs = Path(input.LogsDirectory);
        var www = Path(input.Www);

        var modules = new StringBuilder();
        if (input.IsUnix)
        {
            var dir = Path(input.ModulesDirectory!);
            foreach (var module in input.ModulesToLoad ?? LinuxModules)
                modules.Append($"LoadModule {module}_module \"{dir}/mod_{module}.so\"\n");
        }
        else
        {
            foreach (var module in input.ModulesToLoad ?? Modules)
                modules.Append($"LoadModule {module}_module modules/mod_{module}.so\n");
        }

        // El MPM: en Windows, mpm_winnt (un hijo con sus hilos); en Linux, mpm_event con lo de serie de
        // Apache, que para un equipo de desarrollo sobra. Los AcceptFilter a none son por el AcceptEx de Windows.
        var mpm = input.IsUnix
            ? """
              StartServers 2
              MinSpareThreads 25
              MaxSpareThreads 75
              ThreadsPerChild 25
              MaxRequestWorkers 150
              MaxConnectionsPerChild 0
              Mutex default
              """
            : """
              ThreadsPerChild 64
              MaxConnectionsPerChild 0
              AcceptFilter http none
              AcceptFilter https none
              """;
        var mimeTypes = input.MimeTypesFile is { } mime ? $"\"{Path(mime)}\"" : "conf/mime.types";

        // retry=0: un php-cgi que se reinicia no deja su miembro «en error» 60 s (lo de serie). Con
        // eso, al reiniciar el pool uno a uno Apache acababa con todos en error y daba 503; así la
        // petición pasa al otro y el que vuelve se usa en el acto (probado en real con carga).
        var pools = new StringBuilder();
        foreach (var pool in input.Pools)
        {
            pools.Append($"<Proxy \"balancer://{pool.Name}\">\n");
            foreach (var port in pool.Ports)
                pools.Append($"    BalancerMember \"fcgi://127.0.0.1:{port}\" retry=0\n");
            pools.Append("</Proxy>\n");
        }

        var https = input.HttpsPort is { } port443 ? Listen(port443, input.Ipv6) : "";
        var defaultPort = FcgiPortOf(input.Pools, input.DefaultPool, input.IsUnix);
        // En Windows, a php-cgi le llega la ruta real del script, como con nginx ($document_root$fastcgi_script_name):
        // la que arma mod_proxy_fcgi en Windows lleva una '/' delante (/C:/...) y los espacios en %20. En Linux, con
        // fcgi:// directo, la arma bien el propio modulo (y con esta regla SCRIPT_NAME llega vacio: no vale).
        var scriptFilename = input.IsUnix ? "" : """

            # A php-cgi le llega la ruta real del script, como con nginx ($document_root$fastcgi_script_name):
            # la que arma mod_proxy_fcgi en Windows lleva una '/' delante (/C:/...) y los espacios en %20.
            ProxyFCGISetEnvIf "true" SCRIPT_FILENAME "%{DOCUMENT_ROOT}%{reqenv:SCRIPT_NAME}"

            """;

        return $$"""
            # Generado por Roost: NO lo edites, se regenera en cada `roost up`.
            # Lo tuyo va en sites\<sitio>.conf (fuera de las marcas) o en un .htaccess.
            ServerRoot "{{Path(input.ServerRoot)}}"
            {{Listen(input.HttpPort, input.Ipv6)}}{{https}}ServerName localhost:{{input.HttpPort}}

            {{modules}}
            {{mpm}}
            Timeout 600
            ProxyTimeout 600
            PidFile "{{run}}/apache.pid"
            DefaultRuntimeDir "{{run}}"
            ErrorLog "{{logs}}/apache-error.log"
            LogLevel warn
            LogFormat "%h %l %u %t \"%r\" %>s %b \"%{Referer}i\" \"%{User-Agent}i\"" combined
            CustomLog "{{logs}}/apache-access.log" combined
            TypesConfig {{mimeTypes}}
            AddDefaultCharset UTF-8
            DirectoryIndex index.php index.html index.htm
            Protocols h2 http/1.1
            SSLSessionCache "shmcb:{{run}}/apache-ssl-cache(512000)"
            SSLProtocol all -SSLv3 -TLSv1 -TLSv1.1

            <Directory />
                AllowOverride None
                Require all denied
            </Directory>
            <Files ".ht*">
                Require all denied
            </Files>

            # Un balanceador por pool de php-cgi (los mismos puertos que usaría nginx).
            {{pools}}{{scriptFilename}}
            # http://localhost/: la carpeta www, con el PHP global. Es el primer VirtualHost de
            # su puerto, así que también atiende lo que no sea de ningún sitio.
            <VirtualHost {{VirtualHost(input.HttpPort, input.Ipv6)}}>
                ServerName localhost
                DocumentRoot "{{www}}"
                <Directory "{{www}}">
                    Options Indexes FollowSymLinks
                    AllowOverride All
                    Require all granted
                </Directory>
            {{PhpHandler(input.DefaultPool, defaultPort)}}{{Adminer(input)}}
            </VirtualHost>

            # Un fichero por sitio *.test, editable fuera de las marcas de Roost.
            IncludeOptional "{{Path(input.SitesDirectory)}}/*.conf"
            {{Shares(input.SharesDirectory)}}
            """;
    }

    /// <summary>Compartir (oleada 6): el <c>IncludeOptional</c> de las escuchas de <c>roost share</c>, con su propio <c>Listen</c> cada una.</summary>
    private static string Shares(string? directory) => directory is null ? "" : $$"""

        # Lo que estas compartiendo (roost share), solo mientras dura: lo escribe y lo borra Roost.
        IncludeOptional "{{Path(directory)}}/*.conf"

        """;

    /// <summary>
    /// Los .php, al pool de php-cgi. La barra final del balancer hace falta: Apache pega detrás
    /// la ruta del fichero, y en Windows empieza por la unidad (<c>C:/...</c>); sin la barra, el
    /// nombre del balancer quedaría <c>php-8.4C:</c> y no se encontraría. En Linux
    /// (<paramref name="fcgiPort"/>), directo al php-fpm del pool (<c>proxy:fcgi://127.0.0.1:9840</c>):
    /// con el balancer, mod_proxy_fcgi deja <c>SCRIPT_NAME</c> vacío y <c>PATH_INFO</c> con la ruta del
    /// fichero (probado en Ubuntu 24.04), y con <c>fcgi://</c> arma bien <c>SCRIPT_FILENAME</c> y <c>SCRIPT_NAME</c>;
    /// en Linux cada pool es un solo php-fpm, así que no se pierde nada.
    /// </summary>
    public static string PhpHandler(string pool, int? fcgiPort = null) => $$"""
            <FilesMatch "\.php$">
                SetHandler "{{(fcgiPort is { } port ? $"proxy:fcgi://127.0.0.1:{port}" : $"proxy:balancer://{pool}/")}}"
            </FilesMatch>
        """;

    /// <summary>Linux: el puerto del php-fpm de un pool (el primero: el único); null en Windows (el balancer).</summary>
    public static int? FcgiPortOf(IEnumerable<NginxUpstream> pools, string pool, bool unix) =>
        unix ? pools.FirstOrDefault(p => p.Name == pool)?.Ports.FirstOrDefault() : null;

    /// <summary>
    /// PURO: <c>/adminer</c> en el VirtualHost de localhost: un alias a la carpeta de Adminer, que solo
    /// ejecuta la entrada de Roost (<c>index.php</c>). <c>adminer.php</c> no se sirve suelto, y el
    /// SCRIPT_FILENAME se fija a mano: el <c>ProxyFCGISetEnvIf</c> global lo arma con la www
    /// (<c>DOCUMENT_ROOT</c>), que aquí no es la carpeta del script.
    /// </summary>
    public static string Adminer(ApacheConfInput input)
    {
        if (input.AdminerDirectory is not { } dir) return "";
        var folder = Path(dir);
        return $$"""


                # Adminer de Roost (bin\adminer), con el PHP global. Solo atiende a localhost.
                Alias "/adminer" "{{folder}}"
                <Directory "{{folder}}">
                    Options None
                    AllowOverride None
                    Require all granted
                    DirectoryIndex index.php
                    <Files "adminer.php">
                        Require all denied
                    </Files>
                    ProxyFCGISetEnvIf "true" SCRIPT_FILENAME "{{folder}}/index.php"
                </Directory>
            """;
    }

    /// <summary>Apache en Windows acepta '/' y así no hay que escapar las '\' dentro de comillas.</summary>
    public static string Path(string path) => path.Replace('\\', '/');
}

/// <summary>
/// El <c>sites/&lt;nombre&gt;.conf</c> de Apache de cada sitio: sus dos VirtualHost (HTTP y
/// HTTPS) entre las marcas <c># roost:begin</c> / <c># roost:end</c>, que Roost regenera en cada
/// <c>roost up</c>; lo de fuera es del usuario y se respeta. Lo propio de cada proyecto va mejor
/// en su <c>.htaccess</c>, que Apache lee (<c>AllowOverride All</c>).
/// </summary>
public static class ApacheSiteConf
{
    /// <summary>Lo que necesita el bloque de Roost; todas las rutas, absolutas.</summary>
    /// <param name="Ipv6">Escuchar también en <c>[::1]</c> (false en una máquina sin IPv6 en loopback).</param>
    public sealed record Input(Site Site, string Pool, int HttpPort, int HttpsPort, string CertFile, string KeyFile, bool Ipv6 = true)
    {
        /// <summary>
        /// Sitios avanzados (C5): la carpeta de los registros por sitio (<c>logs/sites</c>); null = los
        /// del servidor (<c>apache-access.log</c>), como antes.
        /// </summary>
        public string? LogsDirectory { get; init; }

        /// <summary>Linux: el puerto del php-fpm del pool (<see cref="ApacheConf.PhpHandler"/>); null = el balancer de Windows.</summary>
        public int? FcgiPort { get; init; }
    }

    /// <summary>Los registros del sitio (C5), en los dos VirtualHost; nada sin carpeta. El formato es el <c>combined</c> de httpd.conf.</summary>
    private static string Logs(Input input) => input.LogsDirectory is not { } dir ? "" : $$"""
            ErrorLog "{{ApacheConf.Path(dir)}}/{{input.Site.Name}}-error.log"
            CustomLog "{{ApacheConf.Path(dir)}}/{{input.Site.Name}}-access.log" combined

        """;

    /// <summary>PURO: el bloque que regenera Roost, marcas incluidas.</summary>
    public static string RenderBlock(Input input)
    {
        var names = SiteAliases.ServerNames(input.Site);
        var root = ApacheConf.Path(input.Site.DocumentRoot);
        var common = $$"""
                ServerName {{names[0]}}
                ServerAlias {{string.Join(' ', names.Skip(1))}}
            {{Logs(input)}}    DocumentRoot "{{root}}"
                <Directory "{{root}}">
                    Options Indexes FollowSymLinks
                    AllowOverride All
                    Require all granted
                </Directory>
            {{ApacheConf.PhpHandler(input.Pool, input.FcgiPort)}}
            """;
        return $$"""
            {{SiteConf.Begin}}  (lo regenera Roost en cada roost up; edita fuera de las marcas)
            <VirtualHost {{ApacheConf.VirtualHost(input.HttpPort, input.Ipv6)}}>
            {{common}}
            </VirtualHost>
            <VirtualHost {{ApacheConf.VirtualHost(input.HttpsPort, input.Ipv6)}}>
            {{common}}
                SSLEngine on
                SSLCertificateFile "{{ApacheConf.Path(input.CertFile)}}"
                SSLCertificateKeyFile "{{ApacheConf.Path(input.KeyFile)}}"
            </VirtualHost>
            {{SiteConf.End}}
            """;
    }

    /// <summary>PURO: el fichero entero, la primera vez.</summary>
    public static string RenderNew(Input input) => $$"""
        # Sitio {{input.Site.Name}} de Roost: https://{{input.Site.Host}}/
        # Lo de entre las marcas roost:begin / roost:end lo regenera Roost; fuera puedes poner lo
        # tuyo. Para reglas del proyecto (rewrites, cabeceras...), mejor su .htaccess.
        {{RenderBlock(input)}}

        """;

    /// <summary>
    /// PURO: <paramref name="existing"/> con el bloque de Roost sustituido por el actual. Si el
    /// usuario ha borrado las marcas, no se adivina: se lanza <see cref="RoostException"/>.
    /// </summary>
    public static string Merge(string existing, Input input, string path)
    {
        var begin = existing.IndexOf(SiteConf.Begin, StringComparison.Ordinal);
        var end = begin < 0 ? -1 : existing.IndexOf(SiteConf.End, begin, StringComparison.Ordinal);
        if (begin < 0 || end < 0)
        {
            throw new RoostException(
                $"{path} ya no tiene las marcas {SiteConf.Begin} / {SiteConf.End}, asi que Roost no sabe que parte regenerar. "
                + "Vuelve a ponerlas, o borra el fichero y Roost lo creara de nuevo.");
        }
        var lineStart = existing.LastIndexOf('\n', begin) + 1;
        var lineEnd = end + SiteConf.End.Length;
        return existing[..lineStart] + RenderBlock(input) + existing[lineEnd..];
    }
}
