using System.Text;
using Roost.Cli.Commands;
using Roost.Cli.Commands.Db;
using Roost.Cli.Commands.Git;
using Roost.Cli.Commands.Php;
using Roost.Cli.Commands.Shims;
using Roost.Cli.Commands.Sites;
using Roost.Cli.Commands.Stack;
using Spectre.Console.Cli;

// roost — CLI de Roost: inventario (scan), auditoria (audit), PHP (php) y el stack local
// (up / down / status). `roost daemon` es el mismo ejecutable haciendo de roostd.
// UTF-8 en la salida para que el semaforo (●) y las tablas se vean en Windows.
try { Console.OutputEncoding = Encoding.UTF8; } catch { /* consola redirigida: da igual */ }

// Las extensiones de bin/extensions (ROOST.md §12.4): una que no carga no impide usar Roost.
var extensions = CliExtensions.Load();

var app = new CommandApp();
app.Configure(config =>
{
    config.SetApplicationName("roost");
    // roost --version: la de Directory.Build.props.
    config.SetApplicationVersion(Roost.Core.Updates.RoostVersion.Current);
    config.UseStrictParsing();

    config.AddCommand<ScanCommand>("scan")
        .WithDescription("Inventaria los componentes del entorno (PHP, MySQL, MariaDB...), agrupados por tipo.")
        .WithExample("scan")
        .WithExample("scan", "--json");

    config.AddCommand<AuditCommand>("audit")
        .WithDescription("Audita cada componente contra los avisos (el feed firmado de roost-catalog): PARCHEADA / SOPORTADA / VULNERABLE / EOL / DESCONOCIDA.")
        .WithExample("audit")
        .WithExample("audit", "--json")
        .WithExample("audit", "--offline")
        .WithExample("audit", "--only", "php");

    config.AddBranch("catalog", catalog =>
    {
        catalog.SetDescription("El feed firmado de avisos que usa la auditoria (roost-catalog): se actualiza solo una vez al dia.");

        catalog.AddCommand<CatalogStatusCommand>("status")
            .WithDescription("Que feed hay en la cache, cual va empaquetado y cual se usa.")
            .WithExample("catalog", "status");

        catalog.AddCommand<CatalogUpdateCommand>("update")
            .WithDescription("Baja ya el feed nuevo, aunque el de la cache sea de hoy.")
            .WithExample("catalog", "update");
    });

    config.AddBranch("php", php =>
    {
        php.SetDescription("Instala y quita PHP oficiales en la raiz de Roost (de momento, solo en Windows).");

        php.AddCommand<PhpListCommand>("list")
            .WithDescription("Lista los PHP instalados por Roost y los disponibles en windows.php.net, con su estado de seguridad.")
            .WithExample("php", "list")
            .WithExample("php", "list", "--json");

        php.AddCommand<PhpInstallCommand>("install")
            .WithDescription("Descarga un PHP oficial, verifica su SHA-256 y lo instala en su propia carpeta.")
            .WithExample("php", "install", "8.4")
            .WithExample("php", "install", "8.3.36", "--ts");

        php.AddCommand<PhpRemoveCommand>("remove")
            .WithDescription("Quita un PHP instalado por Roost (nunca toca los de Laragon, XAMPP...).")
            .WithExample("php", "remove", "8.3.36-nts-x64");

        php.AddCommand<PhpUseCommand>("use")
            .WithDescription("Fija el PHP global (el que sirve http://localhost/), o con --here el de la carpeta actual (.php-version).")
            .WithExample("php", "use", "8.4")
            .WithExample("php", "use", "7.4", "--here");

        php.AddCommand<PhpWhichCommand>("which")
            .WithDescription("Que PHP usa la terminal en esta carpeta, y por que (.php-version, el sitio, el global).")
            .WithExample("php", "which")
            .WithExample("php", "which", "--dir", @"C:\roost\www\legacy");
    });

    config.AddBranch("shims", shims =>
    {
        shims.SetDescription("php y composer en la terminal con el PHP de la carpeta del proyecto (de momento, solo en Windows).");

        shims.AddCommand<ShimsInstallCommand>("install")
            .WithDescription("Pone los shims de php y composer y su carpeta delante en el PATH de tu usuario (sin administrador).")
            .WithExample("shims", "install");

        shims.AddCommand<ShimsRemoveCommand>("remove")
            .WithDescription("Quita los shims y su carpeta del PATH de tu usuario.")
            .WithExample("shims", "remove");

        shims.AddCommand<ShimsStatusCommand>("status")
            .WithDescription("Si estan puestos y en el PATH, y si otro php o composer (el del PATH del sistema) les gana.")
            .WithExample("shims", "status");
    });

    config.AddBranch("db", db =>
    {
        db.SetDescription("Instala y elige MariaDB o MySQL (de momento, solo en Windows).");

        db.AddCommand<DbListCommand>("list")
            .WithDescription("Lista las bases de datos instaladas por Roost y las disponibles (MariaDB y MySQL), con su estado de seguridad.")
            .WithExample("db", "list");

        db.AddCommand<DbInstallCommand>("install")
            .WithDescription("Descarga una MariaDB o MySQL oficial, verifica su SHA-256 y la instala en su propia carpeta.")
            .WithExample("db", "install", "mariadb@11.4")
            .WithExample("db", "install", "mysql@8.4")
            .WithExample("db", "install", "mysql@5.7");

        db.AddCommand<DbRemoveCommand>("remove")
            .WithDescription("Quita los binarios de una base de datos de Roost. Sus datos se conservan.")
            .WithExample("db", "remove", "mysql@5.7");

        db.AddCommand<DbUseCommand>("use")
            .WithDescription("Elige la base de datos que arranca roost up (o none). Si el stack esta en marcha, lo aplica ya.")
            .WithExample("db", "use", "mariadb@11.4")
            .WithExample("db", "use", "none");

        db.AddCommand<DbCreateCommand>("create")
            .WithDescription("Crea una base de datos (utf8mb4) en la base de datos global, que tiene que estar en marcha.")
            .WithExample("db", "create", "tienda");

        db.AddCommand<DbDropCommand>("drop")
            .WithDescription("Borra una base de datos y todas sus tablas (nunca las del sistema). Pregunta antes, salvo con --yes.")
            .WithExample("db", "drop", "tienda");

        db.AddCommand<DbPasswordCommand>("password")
            .WithDescription("Pone o cambia la contrasena de root (vacia = sin contrasena) y la guarda para los comandos de Roost. --save-only si ya la cambiaste por fuera.")
            .WithExample("db", "password")
            .WithExample("db", "password", "--save-only");

        db.AddBranch("user", user =>
        {
            user.SetDescription("Usuarios de la base de datos global: listar, crear, borrar y cambiar la contrasena.");

            user.AddCommand<DbUserListCommand>("list")
                .WithDescription("Lista los usuarios (sin root ni los del sistema) y a que bases de datos llegan.")
                .WithExample("db", "user", "list");

            user.AddCommand<DbUserAddCommand>("add")
                .WithDescription("Crea 'usuario'@'%' con todos los privilegios sobre una base de datos (--db) o sobre todas, y pide su contrasena.")
                .WithExample("db", "user", "add", "tienda", "--db", "tienda");

            user.AddCommand<DbUserRemoveCommand>("remove")
                .WithDescription("Borra la cuenta de un usuario. Sus bases de datos se quedan.")
                .WithExample("db", "user", "remove", "tienda");

            user.AddCommand<DbUserPasswordCommand>("password")
                .WithDescription("Cambia la contrasena de un usuario.")
                .WithExample("db", "user", "password", "tienda");
        });
    });

    config.AddBranch("site", site =>
    {
        site.SetDescription("Sitios https://<nombre>.test/ con su propio vhost, certificado y, si quieres, su propio PHP.");

        site.AddCommand<SiteAddCommand>("add")
            .WithDescription("Registra un sitio (o lo actualiza): vhost del servidor web, entrada en el hosts y certificado.")
            .WithExample("site", "add", "tienda")
            .WithExample("site", "add", "legacy", "--path", "C:\\proyectos\\legacy", "--php", "7.4");

        site.AddCommand<SiteRemoveCommand>("remove")
            .WithDescription("Quita un sitio. La carpeta del proyecto no se toca.")
            .WithExample("site", "remove", "tienda");

        site.AddCommand<SiteListCommand>("list")
            .WithDescription("Lista los sitios con su carpeta, su PHP, su certificado y si estan en el hosts.")
            .WithExample("site", "list");

        site.AddCommand<SiteRootCommand>("root")
            .WithDescription("Ensena o cambia la raiz web de un sitio: la subcarpeta del proyecto que se sirve (public, web, dist...). Con . vuelve a la carpeta del proyecto.")
            .WithExample("site", "root", "tienda")
            .WithExample("site", "root", "tienda", "public")
            .WithExample("site", "root", "tienda", ".");

        site.AddCommand<SiteCloneCommand>("clone")
            .WithDescription("Crea un sitio clonando un repositorio git (publico o local; se clona en una carpeta temporal y solo al acabar pasa a ser el sitio).")
            .WithExample("site", "clone", "https://github.com/acme/tienda")
            .WithExample("site", "clone", "https://github.com/acme/tienda.git", "--name", "tienda", "--branch", "develop", "--php", "8.3");

        site.AddCommand<SitePullCommand>("pull")
            .WithDescription("Trae lo nuevo del remoto a un sitio: solo fast-forward y solo si no hay cambios locales.")
            .WithExample("site", "pull", "tienda");

        site.AddCommand<SiteBranchCommand>("branch")
            .WithDescription("Lista las ramas del remoto de un sitio o cambia a una (solo sin cambios locales).")
            .WithExample("site", "branch", "tienda")
            .WithExample("site", "branch", "tienda", "feature/checkout");

        site.AddCommand<SiteGitCommand>("git")
            .WithDescription("El repositorio de un sitio: rama, si va por detras, cambios locales, ultimo commit e historial. Con --adopt, adopta un .git que ya existe.")
            .WithExample("site", "git", "tienda")
            .WithExample("site", "git", "tienda", "--check")
            .WithExample("site", "git", "legacy", "--adopt");

        site.AddCommand<SiteComposerCommand>("composer")
            .WithDescription("Composer en la carpeta de un sitio, con su PHP y el php.ini de Roost: install, update o require <paquete> (la primera vez instala Composer).")
            .WithExample("site", "composer", "tienda", "install")
            .WithExample("site", "composer", "tienda", "require", "monolog/monolog:^3")
            .WithExample("site", "composer", "tienda", "require", "phpunit/phpunit", "--dev");
    });

    config.AddBranch("dns", dns =>
    {
        dns.SetDescription("El DNS local para los .test: crear un sitio deja de pedir permiso de administrador (de momento, solo en Windows).");

        dns.AddCommand<DnsInstallCommand>("install")
            .WithDescription("Pone la regla de DNS de .test (un solo permiso de administrador) y deja de usar el hosts.")
            .WithExample("dns", "install");

        dns.AddCommand<DnsRemoveCommand>("remove")
            .WithDescription("Quita la regla de DNS y vuelve al fichero hosts.")
            .WithExample("dns", "remove");

        dns.AddCommand<DnsStatusCommand>("status")
            .WithDescription("Como se resuelven los .test, si la regla esta puesta y si el DNS local contesta.")
            .WithExample("dns", "status");
    });

    config.AddBranch("git", git =>
    {
        git.SetDescription("El git con el que Roost clona y actualiza los sitios: un MinGit verificado en bin/git, o el que diga [git] exe en roost.toml.");

        git.AddCommand<GitInstallCommand>("install")
            .WithDescription("Descarga MinGit, verifica su SHA-256 y lo instala en bin/git (roost site clone lo hace solo la primera vez).")
            .WithExample("git", "install");

        git.AddCommand<GitStatusCommand>("status")
            .WithDescription("Que git usa Roost, su version y donde esta su configuracion aislada.")
            .WithExample("git", "status")
            .WithExample("git", "status", "--json");
    });

    config.AddBranch("composer", composer =>
    {
        composer.SetDescription("El Composer de Roost: un composer.phar verificado en bin/composer, que se ejecuta con el PHP de cada carpeta (en la terminal, con el shim composer).");

        composer.AddCommand<Roost.Cli.Commands.Composer.ComposerInstallCommand>("install")
            .WithDescription("Descarga composer.phar de getcomposer.org, verifica su SHA-256 y lo instala en bin/composer (la app lo hace sola la primera vez).")
            .WithExample("composer", "install");

        composer.AddCommand<Roost.Cli.Commands.Composer.ComposerStatusCommand>("status")
            .WithDescription("Que Composer usa Roost, con que PHP en esta carpeta, y si el shim composer esta puesto y otro le gana.")
            .WithExample("composer", "status")
            .WithExample("composer", "status", "--json");
    });

    config.AddBranch("cert", cert =>
    {
        cert.SetDescription("La CA local que firma los certificados de los sitios .test.");

        cert.AddCommand<CertTrustCommand>("trust")
            .WithDescription("Anade la CA de Roost a los certificados de confianza de tu usuario (Windows pide confirmacion).")
            .WithExample("cert", "trust");

        cert.AddCommand<CertUntrustCommand>("untrust")
            .WithDescription("Quita la CA de Roost de los certificados de confianza de tu usuario, y lo de Firefox (Windows pide confirmacion).")
            .WithExample("cert", "untrust");

        cert.AddCommand<CertRenewCommand>("renew")
            .WithDescription("Vuelve a emitir el certificado de todos los sitios (roost up ya los renueva solo antes de caducar).")
            .WithExample("cert", "renew");

        cert.AddCommand<CertPathCommand>("path")
            .WithDescription("Las rutas del certificado y de la clave de un sitio (y de la CA), para servir en HTTPS el servidor de desarrollo de Vite u otro.")
            .WithExample("cert", "path", "tienda")
            .WithExample("cert", "path", "tienda", "--json");
    });

    config.AddCommand<UpCommand>("up")
        .WithDescription("Arranca el servidor web (nginx o Apache) y el pool de php-cgi del PHP global en http://localhost/ (la primera vez lo instala).")
        .WithExample("up");

    config.AddCommand<DownCommand>("down")
        .WithDescription("Para el servidor web, los php-cgi y el daemon.")
        .WithExample("down");

    config.AddBranch("web", web =>
    {
        web.SetDescription("El servidor web que arranca roost up: nginx o Apache (de momento, solo en Windows).");

        web.AddCommand<WebUseCommand>("use")
            .WithDescription("Elige nginx o Apache (lee los .htaccess). Lo instala si falta y rearranca el stack si esta en marcha.")
            .WithExample("web", "use", "apache")
            .WithExample("web", "use", "nginx");
    });

    config.AddCommand<RestartCommand>("restart")
        .WithDescription("Reinicia un servicio sin tocar el resto: el servidor web (nginx o apache), el pool de un PHP, la base de datos o el DNS local (dns). Sin servicio, reinicia todo el stack.")
        .WithExample("restart", "nginx")
        .WithExample("restart", "apache")
        .WithExample("restart", "php-8.4")
        .WithExample("restart", "mariadb-11.4")
        .WithExample("restart");

    config.AddCommand<StopServiceCommand>("stop")
        .WithDescription("Para un servicio sin tocar el resto (la base de datos, limpia); queda parado hasta roost start o el siguiente up.")
        .WithExample("stop", "mariadb-11.4")
        .WithExample("stop", "php-8.4");

    config.AddCommand<StartServiceCommand>("start")
        .WithDescription("Vuelve a arrancar un servicio parado con roost stop.")
        .WithExample("start", "mariadb-11.4")
        .WithExample("start", "php-8.4");

    config.AddCommand<StatusCommand>("status")
        .WithDescription("Estado de los servicios: en marcha, puertos, reinicios.")
        .WithExample("status")
        .WithExample("status", "--json");

    config.AddCommand<ExtensionsCommand>("extensions")
        .WithDescription("Las extensiones instaladas en bin/extensions: cuales cargan, que aportan y por que no las que no.")
        .WithExample("extensions");

    config.AddCommand<UpdateCommand>("update")
        .WithDescription("Comprueba si hay una Roost nueva en GitHub y dice como actualizar. Con --install (Windows, instalada con el instalador) la descarga, la verifica y lanza el instalador.")
        .WithExample("update")
        .WithExample("update", "--install")
        .WithExample("update", "--json");

    // roostd: lo lanza la CLI cuando hace falta, no el usuario.
    config.AddCommand<DaemonCommand>("daemon").IsHidden();

    // Lo lanza el desinstalador (ya elevado): quita lo que Roost puso fuera de su carpeta.
    config.AddCommand<UninstallCleanupCommand>("uninstall-cleanup").IsHidden();

    // Lo ultimo: los comandos de las extensiones no pueden pisar los de Roost.
    extensions.AddCommands(config);
});

return app.Run(args);
