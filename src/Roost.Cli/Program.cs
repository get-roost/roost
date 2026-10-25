using System.Text;
using Roost.Cli.Commands;
using Roost.Cli.Commands.Db;
using Roost.Cli.Commands.Git;
using Roost.Cli.Commands.Node;
using Roost.Cli.Commands.Php;
using Roost.Cli.Commands.Shims;
using Roost.Cli.Commands.Sites;
using Roost.Cli.Commands.Stack;
using Spectre.Console.Cli;

// roost — CLI de Roost: inventario (scan), auditoria (audit), PHP (php) y el stack local
// (up / down / status). `roost daemon` es el mismo ejecutable haciendo de roostd.
// UTF-8 en la salida para que el semaforo (●) y las tablas se vean en Windows.
try { Console.OutputEncoding = Encoding.UTF8; } catch { /* consola redirigida: da igual */ }

// El helper de credenciales de git (oculto): lo lanza git con el token por medio, asi que va
// directo, sin Spectre ni extensiones, y solo habla por stdin y stdout.
if (args is ["git-credential", var credentialAction]) return GitCredentialCommand.Run(credentialAction);

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
        php.SetDescription("Instala y quita PHP en la raiz de Roost: los oficiales de windows.php.net en Windows, los de get-roost/roost-builds (8.1 a 8.5) en Linux.");

        php.AddCommand<PhpListCommand>("list")
            .WithDescription("Lista los PHP instalados por Roost y los disponibles en el catalogo (windows.php.net o roost-builds), con su estado de seguridad.")
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

        php.AddCommand<PhpIniCommand>("ini")
            .WithDescription("Donde estan el php.ini generado y las conf.d de una instalacion, y sus ajustes rapidos (memory_limit, upload_max_filesize, post_max_size, max_execution_time), que --set cambia.")
            .WithExample("php", "ini")
            .WithExample("php", "ini", "8.4", "--set", "memory_limit=1G", "--set", "upload_max_filesize=256M", "--set", "post_max_size=256M")
            .WithExample("php", "ini", "7.4", "--reset");

        php.AddBranch("xdebug", xdebug =>
        {
            xdebug.SetDescription("Xdebug con un clic: la DLL de xdebug.org que toca a cada rama, verificada, en el php.ini de una instalacion.");

            xdebug.AddCommand<PhpXdebugOnCommand>("on")
                .WithDescription("Baja la DLL de Xdebug de esa rama (la primera vez, con su SHA-256 comprobado), la activa y reinicia solo su pool si el stack esta en marcha.")
                .WithExample("php", "xdebug", "on")
                .WithExample("php", "xdebug", "on", "7.4");

            xdebug.AddCommand<PhpXdebugOffCommand>("off")
                .WithDescription("Quita Xdebug del php.ini de esa instalacion y reinicia solo su pool.")
                .WithExample("php", "xdebug", "off", "8.4");

            xdebug.AddCommand<PhpXdebugStatusCommand>("status")
                .WithDescription("En que PHP esta encendido Xdebug, que version y si carga.")
                .WithExample("php", "xdebug", "status")
                .WithExample("php", "xdebug", "status", "8.4", "--json");
        });

        php.AddBranch("ext", ext =>
        {
            ext.SetDescription("Las extensiones de una instalacion (las DLL de su ext\\): las mismas que activa la app.");

            ext.AddCommand<PhpExtListCommand>("list")
                .WithDescription("Lista las extensiones de una instalacion, si se cargan y si van de serie.")
                .WithExample("php", "ext", "list")
                .WithExample("php", "ext", "list", "--php", "7.4", "--json");

            ext.AddCommand<PhpExtEnableCommand>("enable")
                .WithDescription("Activa una extension (comprueba con php -v que carga; si no, la deja como estaba) y reinicia solo su pool.")
                .WithExample("php", "ext", "enable", "soap")
                .WithExample("php", "ext", "enable", "pdo_pgsql", "--php", "8.3");

            ext.AddCommand<PhpExtDisableCommand>("disable")
                .WithDescription("Quita una extension (tambien una de serie, como opcache) y reinicia solo su pool.")
                .WithExample("php", "ext", "disable", "opcache", "--php", "8.4");
        });
    });

    config.AddBranch("shims", shims =>
    {
        shims.SetDescription("php y composer en la terminal con el PHP de la carpeta del proyecto, y node, npm y npx con su Node (de momento, solo en Windows).");

        shims.AddCommand<ShimsInstallCommand>("install")
            .WithDescription("Pone los shims de php y composer (y node, npm y npx si hay un Node de Roost) y su carpeta delante en el PATH de tu usuario (sin administrador).")
            .WithExample("shims", "install");

        shims.AddCommand<ShimsRemoveCommand>("remove")
            .WithDescription("Quita los shims y su carpeta del PATH de tu usuario.")
            .WithExample("shims", "remove");

        shims.AddCommand<ShimsStatusCommand>("status")
            .WithDescription("Si estan puestos y en el PATH, y si otro php, composer o node (el del PATH del sistema, nvm-windows, Volta, fnm) les gana.")
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

        db.AddCommand<DbExportCommand>("export")
            .WithDescription("Vuelca una base de datos a un .sql (o .sql.gz, comprimido) con el mariadb-dump/mysqldump de la instalacion: rutinas, triggers y eventos incluidos.")
            .WithExample("db", "export", "tienda")
            .WithExample("db", "export", "tienda", "--out", "tienda.sql.gz");

        db.AddCommand<DbImportCommand>("import")
            .WithDescription("Importa un .sql, .sql.gz o .zip en una base de datos, en streaming y con su progreso. --create la crea; si ya tiene tablas, pregunta.")
            .WithExample("db", "import", "tienda", "tienda.sql.gz", "--create")
            .WithExample("db", "import", "blog", "blog-mysql8.sql", "--compat");

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
            .WithDescription("Quita un sitio. La carpeta del proyecto no se toca. Ofrece quitar tambien su clave SSH de Roost y su token (--keep-access para dejarlos).")
            .WithExample("site", "remove", "tienda")
            .WithExample("site", "remove", "tienda", "--keep-access");

        site.AddCommand<SiteListCommand>("list")
            .WithDescription("Lista los sitios con su carpeta, su PHP, su certificado y si estan en el hosts.")
            .WithExample("site", "list");

        site.AddCommand<SiteRootCommand>("root")
            .WithDescription("Ensena o cambia la raiz web de un sitio: la subcarpeta del proyecto que se sirve (public, web, dist...). Con . vuelve a la carpeta del proyecto.")
            .WithExample("site", "root", "tienda")
            .WithExample("site", "root", "tienda", "public")
            .WithExample("site", "root", "tienda", ".");

        site.AddCommand<SiteCloneCommand>("clone")
            .WithDescription("Crea un sitio clonando un repositorio git, publico o privado (token HTTPS, clave SSH de Roost, un fichero de clave o la tuya); se clona en una carpeta temporal y solo al acabar pasa a ser el sitio.")
            .WithExample("site", "clone", "https://github.com/acme/tienda")
            .WithExample("site", "clone", "https://github.com/acme/tienda.git", "--name", "tienda", "--branch", "develop", "--php", "8.3")
            .WithExample("site", "clone", "https://github.com/acme/privado", "--token")
            .WithExample("site", "clone", "git@github.com:acme/privado.git", "--ssh")
            .WithExample("site", "clone", "git@git.acme.es:web/tienda.git", "--ssh-key", "C:\\claves\\deploy_tienda")
            .WithExample("site", "clone", "git@github.com:acme/privado.git", "--own-ssh");

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

        // Pasos y plantillas (oleada 4): los pasos de instalación de un sitio ya registrado.
        site.AddCommand<SiteSetupCommand>("setup")
            .WithDescription("Los pasos de instalacion de un sitio (tras clonarlo, o cuando quieras): composer install, el .env con la base de datos de Roost, la clave, las migraciones, storage:link, el .env.local de Symfony o el wp-config.php de WordPress. Con terminal deja elegir; con --yes hace los propuestos.")
            .WithExample("site", "setup", "tienda")
            .WithExample("site", "setup", "tienda", "--yes")
            .WithExample("site", "setup", "tienda", "--steps", "env,key", "--yes")
            .WithExample("site", "setup", "tienda", "--json");

        // Sitios avanzados (oleada 6): renombrar, mover, alias y el hosts.
        site.AddCommand<SiteRenameCommand>("rename")
            .WithDescription("Cambia el nombre de un sitio (vhost, certificado, hosts, subdominios, git y registros). Su base de datos se queda, o con --db copy se copia al nombre nuevo (la de antes no se borra).")
            .WithExample("site", "rename", "tienda", "shop")
            .WithExample("site", "rename", "tienda", "shop", "--db", "copy");

        site.AddCommand<SiteMoveCommand>("move")
            .WithDescription("El sitio pasa a servirse desde otra carpeta: solo apunta a una que ya existe o, con --move-files, mueve alli la del proyecto.")
            .WithExample("site", "move", "tienda", "C:\\proyectos\\tienda")
            .WithExample("site", "move", "tienda", "D:\\web\\tienda", "--move-files");

        site.AddCommand<SiteAliasCommand>("alias")
            .WithDescription("Los alias de un sitio (otros nombres .test) y sus subdominios con nombre: en el vhost, el certificado y el hosts. Sin accion, los lista.")
            .WithExample("site", "alias", "tienda")
            .WithExample("site", "alias", "tienda", "add", "tienda-alias.test")
            .WithExample("site", "alias", "tienda", "add", "api.tienda.test")
            .WithExample("site", "alias", "tienda", "remove", "api.tienda.test");

        site.AddCommand<SiteHostsCommand>("hosts")
            .WithDescription("Deja en el hosts los nombres de todos los sitios (pide permiso de administrador una vez, y solo si cambia).")
            .WithExample("site", "hosts");
    });

    // Sitios avanzados (oleada 6): carpetas aparcadas, como el valet park de Valet.
    config.AddCommand<ParkCommand>("park")
        .WithDescription("Aparca una carpeta: cada subcarpeta es un sitio <subcarpeta>.test (con su .roost.toml, si tiene). Con la app o el stack en marcha, las nuevas entran solas y las borradas se quitan.")
        .WithExample("park", "C:\\proyectos");
    config.AddCommand<UnparkCommand>("unpark")
        .WithDescription("Deja de aparcar una carpeta y quita sus sitios. Las carpetas no se tocan.")
        .WithExample("unpark", "C:\\proyectos");
    config.AddCommand<ParkedCommand>("parked")
        .WithDescription("Las carpetas aparcadas y sus sitios.")
        .WithExample("parked");

    // Pasos y plantillas (oleada 4): un proyecto nuevo con una plantilla.
    config.AddCommand<NewCommand>("new")
        .WithDescription("Un proyecto nuevo con una plantilla (laravel, wordpress o symfony), servido en https://<nombre>.test con su base de datos y su .env o su wp-config.php. Si algo falla, no queda nada a medias.")
        .WithExample("new", "laravel", "tienda")
        .WithExample("new", "wordpress", "blog", "--php", "8.3")
        .WithExample("new", "symfony", "api", "--webapp")
        .WithExample("new", "laravel", "tienda", "--path", "C:\\proyectos\\tienda", "--yes", "--json");

    config.AddBranch("dns", dns =>
    {
        dns.SetDescription("El DNS local para los .test: crear un sitio deja de pedir permiso de administrador (en Windows con una regla NRPT; en Linux, con systemd-resolved).");

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

    // Linux (L1, Privilegios): la carpeta de proyectos compartida, /var/www/roost, la crea una vez roost-helper.
    config.AddBranch("www", www =>
    {
        www.SetDescription("La carpeta de proyectos compartida de Linux (/var/www/roost).");
        www.AddCommand<WwwInitCommand>("init")
            .WithDescription("Crea /var/www/roost con tu usuario como dueno (pide permiso de administrador una vez). En Windows no hace falta.")
            .WithExample("www", "init");
    });

    config.AddBranch("git", git =>
    {
        git.SetDescription("El git con el que Roost clona y actualiza los sitios: un MinGit verificado en bin/git, o el que diga [[git]] exe en roost.toml.");

        git.AddCommand<GitInstallCommand>("install")
            .WithDescription("Descarga MinGit, verifica su SHA-256 y lo instala en bin/git (roost site clone lo hace solo la primera vez).")
            .WithExample("git", "install");

        git.AddCommand<GitStatusCommand>("status")
            .WithDescription("Que git usa Roost, su version y donde esta su configuracion aislada.")
            .WithExample("git", "status")
            .WithExample("git", "status", "--json");

        git.AddBranch("key", key =>
        {
            key.SetDescription("La clave SSH de cada sitio (una deploy key de solo lectura): ed25519, sin contrasena y solo legible por tu usuario, en state/git/keys.");

            key.AddCommand<GitKeyNewCommand>("new")
                .WithDescription("Crea la clave SSH de un sitio y ensena la publica para pegarla en las deploy keys del repositorio.")
                .WithExample("git", "key", "new", "tienda")
                .WithExample("git", "key", "new", "tienda", "--repo", "git@github.com:acme/tienda.git");

            key.AddCommand<GitKeyShowCommand>("show")
                .WithDescription("Ensena la clave publica de un sitio para copiarla, con el enlace a las deploy keys de su repositorio.")
                .WithExample("git", "key", "show", "tienda");

            key.AddCommand<GitKeyRemoveCommand>("remove")
                .WithDescription("Borra la clave SSH de un sitio de este equipo (quita tambien la publica en el proveedor).")
                .WithExample("git", "key", "remove", "tienda");
        });

        git.AddBranch("hosts", hosts =>
        {
            hosts.SetDescription("Los servidores SSH en los que confia Roost: los de GitHub, GitLab y Bitbucket de serie (sus huellas publicadas) y los que aceptes tras ver su huella.");

            hosts.AddCommand<GitHostsListCommand>("list")
                .WithDescription("Los servidores conocidos, con su huella y de donde sale.")
                .WithExample("git", "hosts", "list");

            hosts.AddCommand<GitHostsAddCommand>("add")
                .WithDescription("Le pregunta a un servidor por su clave, ensena su huella y la recuerda si dices que si (o si coincide con --fingerprint).")
                .WithExample("git", "hosts", "add", "git.acme.es")
                .WithExample("git", "hosts", "add", "git.acme.es:2222", "--fingerprint", "SHA256:x7Qp...");

            hosts.AddCommand<GitHostsRemoveCommand>("remove")
                .WithDescription("Olvida la huella de un servidor (uno de serie vuelve a tener la publicada).")
                .WithExample("git", "hosts", "remove", "git.acme.es");
        });

        git.AddBranch("token", token =>
        {
            token.SetDescription("Los tokens HTTPS de los repositorios privados, en el Administrador de credenciales de Windows (nunca en la direccion, los argumentos ni los registros).");

            token.AddCommand<GitTokenSetCommand>("set")
                .WithDescription("Guarda el token de un repositorio (lo pide sin eco o lo lee de stdin).")
                .WithExample("git", "token", "set", "github.com/acme/tienda")
                .WithExample("git", "token", "set", "gitlab.com/acme", "--user", "gitlab+deploy-token-7");

            token.AddCommand<GitTokenRemoveCommand>("remove")
                .WithDescription("Borra un token guardado.")
                .WithExample("git", "token", "remove", "github.com/acme/tienda");

            token.AddCommand<GitTokenListCommand>("list")
                .WithDescription("Los tokens guardados: donde valen, con que usuario y que sitios los usan (nunca el token).")
                .WithExample("git", "token", "list");
        });
    });

    // El helper de credenciales que lanza git (Program lo atiende antes, directamente).
    config.AddCommand<GitCredentialCommand>("git-credential").IsHidden();

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

    // Node (oleada 5): las LTS de nodejs.org verificadas, en bin/node, y los shims node, npm y npx.
    config.AddBranch("node", node =>
    {
        node.SetDescription("Node de nodejs.org en bin/node (las LTS fijadas en Roost, verificadas con su SHA-256), con el Node de cada carpeta en la terminal: .nvmrc, .node-version, engines.node o el global.");

        node.AddCommand<NodeListCommand>("list")
            .WithDescription("Los Node de Roost instalados y las LTS que ofrece, con su estado (LTS, mantenimiento, EOL).")
            .WithExample("node", "list")
            .WithExample("node", "list", "--json");

        node.AddCommand<NodeInstallCommand>("install")
            .WithDescription("Descarga un Node de nodejs.org, verifica su SHA-256 y lo instala en bin/node (sin version, la LTS mas nueva).")
            .WithExample("node", "install", "24")
            .WithExample("node", "install", "lts/jod")
            .WithExample("node", "install", "22.11.0");

        node.AddCommand<NodeRemoveCommand>("remove")
            .WithDescription("Quita un Node de Roost (nunca el de nvm, Volta o el instalador de Node).")
            .WithExample("node", "remove", "22.23.4");

        node.AddCommand<NodeUseCommand>("use")
            .WithDescription("Fija el Node global ([[node]] version en roost.toml), o con --here el de la carpeta actual (.nvmrc).")
            .WithExample("node", "use", "24")
            .WithExample("node", "use", "22", "--here");

        node.AddCommand<NodeWhichCommand>("which")
            .WithDescription("Que Node usa la terminal en esta carpeta, y por que (.nvmrc, .node-version, engines.node, el global).")
            .WithExample("node", "which")
            .WithExample("node", "which", "--dir", @"C:\roost\www\tienda");

        node.AddCommand<NodeStatusCommand>("status")
            .WithDescription("Que Node toca aqui y por que, el global, los shims node, npm y npx y si otro node les gana (nvm-windows, Volta, fnm, el instalador de Node).")
            .WithExample("node", "status")
            .WithExample("node", "status", "--json");
    });

    // Servicios (oleada 3): Redis y el correo de pruebas (Mailpit).
    config.AddBranch("redis", redis =>
    {
        redis.SetDescription("Redis en el stack (cache, colas, sesiones): la build MSYS2 de redis-windows, verificada, solo en 127.0.0.1 (de momento, solo en Windows).");

        redis.AddCommand<Roost.Cli.Commands.Services.RedisInstallCommand>("install")
            .WithDescription("Descarga Redis, verifica su SHA-256, lo instala en bin/redis y lo pone en el stack. Si esta en marcha, lo aplica ya.")
            .WithExample("redis", "install");

        redis.AddCommand<Roost.Cli.Commands.Services.RedisRemoveCommand>("remove")
            .WithDescription("Saca Redis del stack y quita sus binarios. Sus datos (data/redis) se conservan.")
            .WithExample("redis", "remove");

        redis.AddCommand<Roost.Cli.Commands.Services.RedisEnableCommand>("enable")
            .WithDescription("Pone Redis en el stack (lo instala si falta).")
            .WithExample("redis", "enable");

        redis.AddCommand<Roost.Cli.Commands.Services.RedisEnableCommand>("disable")
            .WithDescription("Saca Redis del stack sin quitar sus binarios ni sus datos.")
            .WithExample("redis", "disable");

        redis.AddCommand<Roost.Cli.Commands.Services.RedisPortCommand>("port")
            .WithDescription("Cambia el puerto de Redis ([[redis]] port, 6379 por defecto) y lo aplica si el stack esta en marcha.")
            .WithExample("redis", "port", "6380");

        redis.AddCommand<Roost.Cli.Commands.Services.RedisStatusCommand>("status")
            .WithDescription("Que Redis hay, si va en el stack, si contesta al PING y donde estan su configuracion y sus datos.")
            .WithExample("redis", "status")
            .WithExample("redis", "status", "--json");
    });

    config.AddBranch("mail", mail =>
    {
        mail.SetDescription("Mailpit en el stack: el correo de PHP (mail(), Laravel, WordPress) se queda en una bandeja local en vez de salir del equipo (de momento, solo en Windows).");

        mail.AddCommand<Roost.Cli.Commands.Services.MailInstallCommand>("install")
            .WithDescription("Descarga Mailpit, verifica su SHA-256, lo instala en bin/mailpit, lo pone en el stack y lleva a el el mail() de PHP.")
            .WithExample("mail", "install");

        mail.AddCommand<Roost.Cli.Commands.Services.MailRemoveCommand>("remove")
            .WithDescription("Saca Mailpit del stack, quita el mail() de PHP hacia el y sus binarios. La bandeja (data/mailpit) se conserva.")
            .WithExample("mail", "remove");

        mail.AddCommand<Roost.Cli.Commands.Services.MailEnableCommand>("enable")
            .WithDescription("Pone Mailpit en el stack (lo instala si falta).")
            .WithExample("mail", "enable");

        mail.AddCommand<Roost.Cli.Commands.Services.MailEnableCommand>("disable")
            .WithDescription("Saca Mailpit del stack sin quitar sus binarios ni la bandeja.")
            .WithExample("mail", "disable");

        mail.AddCommand<Roost.Cli.Commands.Services.MailPortCommand>("port")
            .WithDescription("Cambia los puertos de Mailpit ([[mail]] smtp_port y web_port) y los aplica si el stack esta en marcha.")
            .WithExample("mail", "port", "--smtp", "1026", "--web", "8026");

        mail.AddCommand<Roost.Cli.Commands.Services.MailOpenCommand>("open")
            .WithDescription("Abre la bandeja de Mailpit en el navegador.")
            .WithExample("mail", "open");

        mail.AddCommand<Roost.Cli.Commands.Services.MailStatusCommand>("status")
            .WithDescription("Que Mailpit hay, si va en el stack, sus puertos y si el mail() de PHP va a el.")
            .WithExample("mail", "status")
            .WithExample("mail", "status", "--json");
    });

    // Compartir (oleada 6): un sitio por internet con un tunel rapido de Cloudflare, o en la red local.
    config.AddBranch("share", share =>
    {
        share.SetDescription("Ensena un sitio fuera de tu equipo mientras dura: por internet con un tunel rapido de Cloudflare (sin cuenta) o, con --lan, en tu red local (solo en Windows). Nada se comparte solo, y Ctrl+C o roost down lo paran.");
        share.SetDefaultCommand<Roost.Cli.Commands.Share.ShareCommand>();
        share.AddExample("share", "tienda");
        share.AddExample("share", "tienda", "--lan");
        share.AddExample("share", "tienda", "--lan", "--ip", "192.168.1.20");
        share.AddExample("share", "tienda", "--detach", "--yes", "--json");

        share.AddCommand<Roost.Cli.Commands.Share.ShareStopCommand>("stop")
            .WithDescription("Deja de compartir un sitio (sin sitio, todo); con lo de la red local quita tambien la regla del cortafuegos.")
            .WithExample("share", "stop", "tienda")
            .WithExample("share", "stop", "--lan");

        share.AddCommand<Roost.Cli.Commands.Share.ShareStatusCommand>("status")
            .WithDescription("Lo que se esta compartiendo, cloudflared y las reglas del cortafuegos de Roost.")
            .WithExample("share", "status")
            .WithExample("share", "status", "--json");

        share.AddCommand<Roost.Cli.Commands.Share.ShareInstallCommand>("install")
            .WithDescription("Descarga cloudflared, verifica su SHA-256 y lo instala en bin/cloudflared (roost share lo hace solo la primera vez).")
            .WithExample("share", "install");
    });

    config.AddBranch("cert", cert =>
    {
        cert.SetDescription("La CA local que firma los certificados de los sitios .test.");

        cert.AddCommand<CertTrustCommand>("trust")
            .WithDescription("Anade la CA de Roost a los certificados de confianza de tu usuario (Windows pide confirmacion; en Linux, Chrome y Firefox con certutil, y --system la del sistema).")
            .WithExample("cert", "trust");

        cert.AddCommand<CertUntrustCommand>("untrust")
            .WithDescription("Quita la CA de Roost de los certificados de confianza de tu usuario, y lo de Firefox (Windows pide confirmacion; en Linux, tambien la del sistema si estaba).")
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
        web.SetDescription("El servidor web que arranca roost up: nginx o Apache (en Linux, de momento solo nginx).");

        web.AddCommand<WebUseCommand>("use")
            .WithDescription("Elige nginx o Apache (lee los .htaccess). Lo instala si falta y, si el stack esta en marcha, cambia solo el servidor web.")
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
    // Ajustes y sistema (E4): arrancar el stack al iniciar sesion, sin la app.
    config.AddBranch("autostart", autostart =>
    {
        autostart.SetDescription("Arrancar el stack al iniciar sesion sin abrir la app: una tarea programada de tu usuario, sin permisos de administrador (solo en Windows).");

        autostart.AddCommand<AutostartOnCommand>("on")
            .WithDescription("Crea la tarea: al iniciar sesion lanza roost up (el daemon y el stack), sin ventanas.")
            .WithExample("autostart", "on");

        autostart.AddCommand<AutostartOffCommand>("off")
            .WithDescription("Quita la tarea.")
            .WithExample("autostart", "off");

        autostart.AddCommand<AutostartStatusCommand>("status")
            .WithDescription("Si la tarea esta puesta y que lanza (solo lee).")
            .WithExample("autostart", "status")
            .WithExample("autostart", "status", "--json");
    });

    config.AddCommand<UninstallCleanupCommand>("uninstall-cleanup").IsHidden();

    // El panel web (decision 42 de ROOST.md): la misma interfaz que la app, en el navegador, donde la app no sirve.
    config.AddBranch("panel", panel =>
    {
        panel.SetDescription("El panel web local: la misma interfaz que la app, en el navegador de este equipo (127.0.0.1), para Linux sin escritorio, servidores, WSL o un equipo remoto por un tunel ssh.");
        panel.AddCommand<Roost.Cli.Commands.WebPanel.PanelUpCommand>("up")
            .WithDescription("Arranca el panel en 127.0.0.1:<puerto> (5556 por defecto) y da la direccion con su codigo de acceso, nuevo en cada arranque. Nunca escucha fuera del equipo.")
            .WithExample("panel", "up")
            .WithExample("cp", "up", "5556");
        panel.AddCommand<Roost.Cli.Commands.WebPanel.PanelDownCommand>("down")
            .WithDescription("Para el panel (el stack sigue como estaba).")
            .WithExample("panel", "down");
        panel.AddCommand<Roost.Cli.Commands.WebPanel.PanelStatusCommand>("status")
            .WithDescription("Si el panel esta en marcha, su pid y su puerto.")
            .WithExample("panel", "status")
            .WithExample("panel", "status", "--json");
        panel.AddCommand<Roost.Cli.Commands.WebPanel.PanelUrlCommand>("url")
            .WithDescription("La direccion del panel con su codigo de acceso (para abrirla o copiarla).")
            .WithExample("panel", "url");
        panel.AddCommand<Roost.Cli.Commands.WebPanel.PanelServeCommand>("serve").IsHidden();
    }).WithAlias("cp");

    // Lo ultimo: los comandos de las extensiones no pueden pisar los de Roost.
    extensions.AddCommands(config);
});

return app.Run(args);
