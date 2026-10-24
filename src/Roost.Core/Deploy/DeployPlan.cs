using System.Text.RegularExpressions;
using Roost.Core.Composer;
using Roost.Core.Config;
using Roost.Core.Install;
using Roost.Core.Models;
using Roost.Core.Node;
using Roost.Core.Platform;
using Roost.Core.Shims;
using Roost.Core.Sites;
using Roost.Core.Stack;

namespace Roost.Core.Deploy;

/// <summary>Qué hace un paso de instalación.</summary>
public enum DeployStepKind
{
    /// <summary>Plantilla: <c>composer create-project</c> o descargar WordPress. Solo en Nuevo sitio y <c>roost new</c>.</summary>
    CreateProject,

    /// <summary>Crear la base de datos del sitio (utf8mb4) en la global.</summary>
    Database,

    /// <summary><c>composer install</c>.</summary>
    ComposerInstall,

    /// <summary>El <c>.env</c> de Laravel o el <c>.env.local</c> de Symfony, con la base de datos y la URL de Roost.</summary>
    Env,

    /// <summary><c>php artisan key:generate</c>.</summary>
    AppKey,

    /// <summary><c>php artisan migrate</c> o <c>php bin/console doctrine:migrations:migrate</c>.</summary>
    Migrate,

    /// <summary><c>php artisan storage:link</c>.</summary>
    StorageLink,

    /// <summary>El <c>wp-config.php</c> desde <c>wp-config-sample.php</c>.</summary>
    WpConfig,

    /// <summary>El mu-plugin para que WordPress se fíe de los certificados <c>.test</c>.</summary>
    WpLoopback,

    /// <summary>
    /// <c>npm ci</c> (o <c>npm install</c>, o el pnpm o el yarn del lockfile, con corepack) y, si hay
    /// script <c>build</c>, <c>npm run build</c>, con el Node de Roost que toca en la carpeta.
    /// </summary>
    Npm,
}

/// <summary>Cómo se hace el <c>.env</c> (o el <c>.env.local</c>).</summary>
public enum EnvMode
{
    /// <summary>No hay: se copia el <c>.env.example</c> y se ponen los valores de Roost encima de los del ejemplo.</summary>
    FromExample,

    /// <summary>No hay ni ejemplo: uno mínimo con lo de Roost.</summary>
    New,

    /// <summary>Ya hay: solo se añaden las claves que faltan; lo que ya tiene no se toca.</summary>
    Complete,

    /// <summary>Lo acaba de crear una plantilla (los scripts de Laravel): es de Roost, y lo de Roost manda.</summary>
    Fresh,
}

/// <summary>Cómo está la base de datos del sitio (lo mira <see cref="DeployProbe"/> antes de proponer los pasos).</summary>
public enum DbState
{
    /// <summary>No se ha podido mirar (el cliente no contesta).</summary>
    Unknown,

    /// <summary>El servidor no está en marcha.</summary>
    Stopped,

    /// <summary>No existe todavía (se puede crear).</summary>
    Missing,

    /// <summary>Existe y no tiene tablas.</summary>
    Empty,

    /// <summary>Existe y tiene tablas (<see cref="DeployDatabase.Tables"/>).</summary>
    HasTables,
}

/// <summary>La base de datos del sitio en la global de Roost: dónde está, cómo se entra y cómo está.</summary>
/// <param name="Port">El puerto con el que escucha (el del stack en marcha o el de roost.toml).</param>
/// <param name="Password">La contraseña de root que guarda Roost (null = sin contraseña).</param>
public sealed record DeployDatabase(InstalledDb Db, string IniPath, int Port, string? Password, string Name)
{
    public DbState State { get; init; } = DbState.Unknown;

    /// <summary>Las tablas que tiene, con <see cref="DbState.HasTables"/>.</summary>
    public int Tables { get; init; }

    public bool Running => State != DbState.Stopped;

    /// <summary>«MariaDB 11.4.13».</summary>
    public string Label => Db.Label;

    /// <summary>La versión del servidor para Doctrine (<c>serverVersion</c>): «11.4.13-MariaDB» o «8.4.11».</summary>
    public string ServerVersion => Db.Engine == DbEngine.MariaDb ? $"{Db.Name}-MariaDB" : Db.Name;
}

/// <summary>
/// Dónde y con qué se hacen los pasos: el sitio (o el que va a ser, en una plantilla), su carpeta,
/// su URL, el PHP, la base de datos y lo que haya en el stack (Mailpit, Redis).
/// </summary>
public sealed record DeployTarget
{
    /// <summary>El nombre del sitio (el de <c>&lt;nombre&gt;.test</c>).</summary>
    public required string SiteName { get; init; }

    public required string Folder { get; init; }

    /// <summary>La URL del sitio sin la barra final (<c>https://tienda.test:8445</c>): el <c>APP_URL</c>.</summary>
    public required string Url { get; init; }

    public required InstalledPhp Php { get; init; }

    /// <summary>Por qué ese PHP («el PHP global (8.4)», «el sitio usa PHP 8.3»), para contarlo.</summary>
    public string PhpReason { get; init; } = "";

    /// <summary>La base de datos del sitio; null si no hay ninguna en Roost (o el sitio no la quiere).</summary>
    public DeployDatabase? Database { get; init; }

    /// <summary>El puerto SMTP de Mailpit si va en el stack; null si no.</summary>
    public int? MailSmtpPort { get; init; }

    /// <summary>El puerto de Redis si va en el stack; null si no.</summary>
    public int? RedisPort { get; init; }

    /// <summary>El proyecto lo acaba de crear una plantilla: su <c>.env</c> es de Roost (<see cref="EnvMode.Fresh"/>).</summary>
    public bool Fresh { get; init; }

    /// <summary>El Node de Roost con el que se hace el paso npm (el de la carpeta: <see cref="TerminalNode"/>); null si no hay.</summary>
    public NodeChoice? Node { get; init; }

    /// <summary>Sin <see cref="Node"/>: por qué (sin tildes; no hay ningún Node de Roost, el .nvmrc pide uno que no está).</summary>
    public string? NodeProblem { get; init; }

    /// <summary>Sin <see cref="Node"/>: el Node que habría que instalar («24», o el que pide el .nvmrc), si se sabe.</summary>
    public string? NodeMissing { get; init; }

    public string Host => $"{SiteName}.test";

    /// <summary>
    /// El de un sitio registrado: su PHP (el que usaría la terminal en su carpeta, como Composer), la
    /// base de datos global con la del sitio (<see cref="SiteManager.DatabaseOf"/>) y lo del stack.
    /// <paramref name="dbRunning"/> y <paramref name="dbPort"/> los sabe quien llama (el daemon):
    /// parada, queda como <see cref="DbState.Stopped"/>; en marcha, <see cref="DbState.Unknown"/>
    /// hasta que <see cref="DeployProbe.ProbeAsync"/> la mire.
    /// </summary>
    public static DeployTarget ForSite(RoostPaths paths, Site site, bool dbRunning, int? dbPort = null)
    {
        var config = LoadConfig(paths);
        var php = ComposerTool.PhpFor(paths, site.Folder);
        var node = TerminalNode.TryResolve(paths, site.Folder, out var nodeProblem);
        return new DeployTarget
        {
            SiteName = site.Name,
            Folder = site.Folder,
            Url = StackPlanner.SiteUrl(site, config.HttpsPort).TrimEnd('/'),
            Php = php.Php,
            PhpReason = php.Reason,
            Database = DatabaseFor(paths, config, SiteManager.DatabaseOf(site), dbRunning, dbPort),
            MailSmtpPort = config.MailEnabled ? config.MailSmtpPort : null,
            RedisPort = config.RedisEnabled ? config.RedisPort : null,
            Node = node,
            NodeProblem = nodeProblem,
            NodeMissing = node is null ? TerminalNode.MissingSpec(paths, site.Folder) : null,
        };
    }

    /// <summary>La base de datos global con el nombre <paramref name="name"/>; null si no hay ninguna (o con <paramref name="name"/> null).</summary>
    public static DeployDatabase? DatabaseFor(RoostPaths paths, RoostConfig config, string? name, bool running, int? port)
    {
        if (name is null) return null;
        var planner = new StackPlanner(paths, Scanning.ProcessRunner.Default);
        InstalledDb? db;
        try { db = planner.ResolveDb(config, new DbInstaller(paths).ListInstalled()); }
        catch (RoostException) { return null; }
        if (db is null) return null;
        string? password = null;
        try { password = new DbPasswords(paths).For(db); } catch (RoostException) { /* sin contraseña guardada */ }
        return new DeployDatabase(db, planner.DbIniPath(db), port ?? config.DbPort, password, name)
        {
            State = running ? DbState.Unknown : DbState.Stopped,
        };
    }

    internal static RoostConfig LoadConfig(RoostPaths paths)
    {
        try { return RoostConfig.Load(paths.ConfigFile); }
        catch (RoostException) { return new RoostConfig(); }
    }
}

/// <summary>
/// Un paso de instalación: qué es, si va marcado, si se puede hacer y lo que se cuenta de él. Los
/// textos (<see cref="Label"/>, <see cref="Hint"/>) son los de la CLI, sin tildes; la app pinta los
/// suyos con los datos de abajo.
/// </summary>
public sealed record DeployStep
{
    /// <summary>
    /// Para <c>roost site setup --steps</c>: <c>create</c>, <c>db</c>, <c>composer</c>, <c>env</c>,
    /// <c>key</c>, <c>migrate</c>, <c>storage-link</c>, <c>wp-config</c>, <c>wp-loopback</c> y <c>npm</c>.
    /// </summary>
    public required string Id { get; init; }

    public required DeployStepKind Kind { get; init; }

    /// <summary>«composer install», «Crear .env desde .env.example».</summary>
    public required string Label { get; init; }

    /// <summary>La orden que se enseña (<c>composer install</c>, <c>php artisan migrate</c>); null si es un paso de Roost (un fichero, la base de datos).</summary>
    public string? Command { get; init; }

    /// <summary>Por qué y qué hace, sin tildes (lo enseña la CLI).</summary>
    public required string Hint { get; init; }

    /// <summary>Si va marcado de entrada.</summary>
    public bool Checked { get; init; }

    /// <summary>false si ahora no se puede (la base de datos parada, sin Node de Roost): sale, pero no se marca.</summary>
    public bool Available { get; init; } = true;

    /// <summary>No se puede desmarcar (crear el proyecto, en una plantilla).</summary>
    public bool Required { get; init; }

    /// <summary>Ejecuta código del proyecto (scripts de Composer, artisan, bin/console): por el aviso.</summary>
    public bool RunsProjectCode { get; init; }

    // Los datos para que la app cuente cada paso a su manera.

    /// <summary>Con <see cref="DeployStepKind.Env"/>: cómo se hace.</summary>
    public EnvMode? EnvMode { get; init; }

    /// <summary>El fichero que escribe (<c>.env</c>, <c>.env.local</c>, <c>wp-config.php</c>), si escribe uno.</summary>
    public string? File { get; init; }

    /// <summary>Con <see cref="EnvMode.Complete"/>: las claves que se van a añadir.</summary>
    public IReadOnlyList<string> MissingKeys { get; init; } = [];

    /// <summary>Con <see cref="DeployStepKind.ComposerInstall"/>: si hay composer.lock y si ya hay vendor/.</summary>
    public bool HasLock { get; init; }

    public bool HasVendor { get; init; }

    /// <summary>Con <see cref="DeployStepKind.Migrate"/> y <see cref="DeployStepKind.Database"/>: cómo está la base de datos.</summary>
    public DbState? DbState { get; init; }

    public int Tables { get; init; }

    /// <summary>El .env usa Redis con predis y el proyecto no lo tiene (<c>composer require predis/predis</c>).</summary>
    public bool PredisMissing { get; init; }

    /// <summary>Con <see cref="DeployStepKind.Npm"/>: el gestor (el del lockfile), si ya hay node_modules/ y si hay script build.</summary>
    public NodeManager NodeManager { get; init; }

    public bool HasNodeModules { get; init; }

    public bool HasBuildScript { get; init; }
}

/// <summary>
/// Los pasos que tocan a un proyecto según lo que es y cómo está la carpeta (ROOST.md: git al estilo
/// Plesk, «Pasos tras clonar»; y las plantillas de Nuevo sitio): Composer, el <c>.env</c> con la base
/// de datos de Roost, la clave, las migraciones, <c>storage:link</c>, el <c>wp-config.php</c>… Nada
/// se ejecuta aquí: lo hace <see cref="DeployRunner"/> con los que se marquen. PURO sobre disco (lee
/// el .env para saber qué le falta).
/// </summary>
public static partial class DeployPlan
{
    /// <summary>El texto del aviso: Composer, artisan y bin/console ejecutan código del propio proyecto.</summary>
    public const string ScriptsWarning =
        "composer install, npm, php artisan y bin/console ejecutan scripts del propio proyecto: hazlo solo con repositorios de confianza.";

    /// <summary>Los pasos, en el orden en que se hacen.</summary>
    public static IReadOnlyList<DeployStep> For(ProjectKind kind, DeployTarget target)
    {
        var steps = new List<DeployStep>();
        var db = target.Database;
        // Symfony solo quiere una base de datos si usa Doctrine (el skeleton a secas, no).
        var usesDb = kind.Type is ProjectType.Laravel or ProjectType.WordPress || kind.Type == ProjectType.Symfony && kind.HasDoctrine;

        // Recién creado por una plantilla, create-project ya ha instalado vendor/: no se repite.
        if (kind.HasComposerJson && kind.Type != ProjectType.WordPress && !(target.Fresh && kind.HasVendor))
        {
            steps.Add(new DeployStep
            {
                Id = "composer",
                Kind = DeployStepKind.ComposerInstall,
                Label = "composer install",
                Command = "composer install",
                Hint = kind.HasVendor ? "vendor/ ya esta: marcalo solo si quieres repetirlo."
                    : kind.HasComposerLock ? "Instala las dependencias (hay composer.lock)."
                    : "No hay composer.lock: resuelve las versiones y lo crea.",
                Checked = !kind.HasVendor,
                RunsProjectCode = true,
                HasLock = kind.HasComposerLock,
                HasVendor = kind.HasVendor,
            });
        }

        if (usesDb && db is { State: DbState.Missing })
        {
            steps.Add(new DeployStep
            {
                Id = "db",
                Kind = DeployStepKind.Database,
                Label = $"Crear la base de datos {db.Name}",
                Hint = $"En {db.Label} (utf8mb4).",
                Checked = true,
                DbState = DbState.Missing,
            });
        }

        switch (kind.Type)
        {
            case ProjectType.Laravel:
                AddLaravel(steps, kind, target);
                break;
            case ProjectType.Symfony:
                AddSymfony(steps, kind, target);
                break;
            case ProjectType.WordPress:
                AddWordPress(steps, kind, target);
                break;
        }

        if (kind.HasPackageJson) steps.Add(NpmStep(target));
        return steps;
    }

    /// <summary>
    /// El paso npm: instalar las dependencias como fija el lockfile (<c>npm ci</c>, o <c>npm install</c>
    /// si no hay <c>package-lock.json</c>; pnpm o yarn con corepack si es su lockfile) y, si hay script
    /// <c>build</c>, <c>npm run build</c>. Con el Node de Roost de la carpeta; sin él (o con bun), sale
    /// apagado y dice qué falta. En una plantilla el proyecto aún no existe: npm, y build como trae Laravel.
    /// </summary>
    private static DeployStep NpmStep(DeployTarget target)
    {
        var project = NodeProject.Read(target.Folder);
        var exists = project.HasPackageJson;
        var manager = exists ? project.Manager : NodeManager.Npm;
        var name = NodeTool.Name(manager);
        var install = exists ? NodeTool.Display(manager, NodeTool.InstallArguments(project, clean: true)) : "npm install";
        var build = !exists || project.HasScript("build");
        var label = build ? $"{install} y {name} run build" : install;
        var node = target.Node;
        var why = node is null ? null : NodeTool.WhyNot(manager, node.Node);
        var with = node is null ? "" : manager is NodeManager.Pnpm or NodeManager.Yarn ? $"Con {node.Node.Label} y el {name} de corepack" : $"Con {node.Node.Label}";
        return new DeployStep
        {
            Id = "npm",
            Kind = DeployStepKind.Npm,
            Label = label,
            Command = label,
            Hint = node is null
                ? (target.NodeProblem ?? "No hay ningun Node de Roost.") + (target.NodeMissing is { } missing && target.NodeProblem?.Contains("roost node install", StringComparison.Ordinal) != true ? $" Instalalo: roost node install {missing}" : "")
                : why ?? (project.HasNodeModules ? $"{with}. node_modules/ ya esta: marcalo solo si quieres repetirlo."
                    : !exists ? $"{with}: las dependencias del package.json de la plantilla y los assets de Vite."
                    : project.LockFile is null ? $"{with}. No hay lockfile: npm install resuelve las versiones y crea package-lock.json."
                    : $"{with}: lo que fija {project.LockFile}."),
            Checked = node is not null && why is null && !project.HasNodeModules,
            Available = node is not null && why is null,
            RunsProjectCode = true,
            NodeManager = manager,
            HasNodeModules = project.HasNodeModules,
            HasBuildScript = build,
            HasLock = project.LockFile is not null,
        };
    }

    private static void AddLaravel(List<DeployStep> steps, ProjectKind kind, DeployTarget target)
    {
        var values = LaravelEnv(target);
        var file = Path.Combine(target.Folder, ".env");
        var mode = target.Fresh && kind.HasEnv ? EnvMode.Fresh : kind.HasEnv ? EnvMode.Complete : kind.HasEnvExample ? EnvMode.FromExample : EnvMode.New;
        var missing = mode == EnvMode.Complete ? MissingKeys(file, values) : [];
        var predisMissing = target.RedisPort is not null && !HasPackage(target.Folder, "predis/predis");
        var db = target.Database;
        steps.Add(new DeployStep
        {
            Id = "env",
            Kind = DeployStepKind.Env,
            Label = mode switch
            {
                EnvMode.FromExample => "Crear .env desde .env.example",
                EnvMode.New => "Crear .env",
                EnvMode.Complete => "Completar .env",
                _ => "Poner la base de datos de Roost en .env",
            },
            Hint = mode == EnvMode.Complete && missing.Count == 0
                ? "Ya tiene todo lo de Roost: no hay nada que anadir."
                : mode == EnvMode.Complete
                    ? $"Ya hay .env: se anaden {Keys(missing)}; lo que ya tiene no se toca."
                    : (mode == EnvMode.New ? "No hay .env.example: uno minimo con " : "Con ") + EnvSummary(target, predisMissing),
            Checked = mode != EnvMode.Complete || missing.Count > 0,
            EnvMode = mode,
            File = ".env",
            MissingKeys = missing,
            PredisMissing = predisMissing,
        });

        // La clave: si el .env ya la tiene, nada. Si sale del ejemplo, el ejemplo la trae vacía.
        if (!kind.HasAppKey)
        {
            steps.Add(new DeployStep
            {
                Id = "key",
                Kind = DeployStepKind.AppKey,
                Label = "php artisan key:generate",
                Command = "php artisan key:generate",
                Hint = target.Fresh
                    ? "La clave de la aplicacion (si create-project ya la ha puesto, se deja)."
                    : "La clave de la aplicacion (APP_KEY esta vacia).",
                Checked = true,
                RunsProjectCode = true,
            });
        }

        if (db is not null) steps.Add(MigrateStep("php artisan migrate", db, target.Fresh));

        if (!kind.HasStorageLink)
        {
            steps.Add(new DeployStep
            {
                Id = "storage-link",
                Kind = DeployStepKind.StorageLink,
                Label = "php artisan storage:link",
                Command = "php artisan storage:link",
                Hint = "Enlaza public/storage con storage/app/public (una union de Windows: no pide permisos).",
                Checked = true,
                RunsProjectCode = true,
            });
        }
    }

    private static void AddSymfony(List<DeployStep> steps, ProjectKind kind, DeployTarget target)
    {
        var values = SymfonyEnv(target, kind.HasDoctrine);
        var file = Path.Combine(target.Folder, ".env.local");
        var mode = kind.HasEnvLocal ? EnvMode.Complete : EnvMode.New;
        var missing = mode == EnvMode.Complete ? MissingKeys(file, values) : [];
        steps.Add(new DeployStep
        {
            Id = "env",
            Kind = DeployStepKind.Env,
            Label = mode == EnvMode.Complete ? "Completar .env.local" : target.Database is null || !kind.HasDoctrine ? "Crear .env.local" : "Crear .env.local con DATABASE_URL",
            Hint = mode == EnvMode.Complete && missing.Count == 0
                ? "Ya tiene todo lo de Roost: no hay nada que anadir."
                : mode == EnvMode.Complete
                    ? $"Ya hay .env.local: se anaden {Keys(missing)}; lo que ya tiene no se toca."
                    : SymfonySummary(target, kind.HasDoctrine) + " .env.local no se sube a git.",
            Checked = mode != EnvMode.Complete || missing.Count > 0,
            EnvMode = mode,
            File = ".env.local",
            MissingKeys = missing,
        });

        if (kind.HasDoctrineMigrations && target.Database is { } db)
            steps.Add(MigrateStep("php bin/console doctrine:migrations:migrate", db, target.Fresh));
    }

    private static void AddWordPress(List<DeployStep> steps, ProjectKind kind, DeployTarget target)
    {
        if (!kind.HasWpConfig && kind.HasWpConfigSample)
        {
            var db = target.Database;
            steps.Add(new DeployStep
            {
                Id = "wp-config",
                Kind = DeployStepKind.WpConfig,
                Label = "Crear wp-config.php con la base de datos de Roost y claves nuevas",
                Hint = db is null
                    ? "No hay base de datos en Roost: el instalador de WordPress te la pedira."
                    : $"{db.Name} en {WpConfig.Host(db.Port)}, root {(db.Password is null ? "sin contrasena" : "con la contrasena que guarda Roost")}; las 8 claves y salts, nuevas.",
                Checked = db is not null,
                Available = db is not null,
                File = WpConfig.FileName,
            });
        }

        // En una plantilla la carpeta aún no existe: el mu-plugin va seguro. En uno que ya existe, si no lo tiene.
        var hasPlugin = File.Exists(Path.Combine(target.Folder, "wp-content", "mu-plugins", WpConfig.LoopbackPluginName));
        if (target.Fresh || !hasPlugin && Directory.Exists(Path.Combine(target.Folder, "wp-content")))
        {
            steps.Add(new DeployStep
            {
                Id = "wp-loopback",
                Kind = DeployStepKind.WpLoopback,
                Label = "Que WordPress se fie de los certificados .test",
                Hint = $"Un mu-plugin ({WpConfig.LoopbackPluginName}): la Salud del sitio y WP-Cron usan el bundle de CA de Roost para los .test.",
                Checked = true,
                File = $"wp-content/mu-plugins/{WpConfig.LoopbackPluginName}",
            });
        }
    }

    private static DeployStep MigrateStep(string command, DeployDatabase db, bool fresh)
    {
        // Solo marcado si la base de datos está vacía (o se va a crear): migrar una con datos puede
        // tocar lo que hay, y eso lo decide el usuario.
        var state = db.State;
        return new DeployStep
        {
            Id = "migrate",
            Kind = DeployStepKind.Migrate,
            Label = command,
            Command = command,
            Hint = state switch
            {
                DbState.Stopped => $"La base de datos esta parada: arranca Roost (roost up) para migrar {db.Name}.",
                DbState.Missing => $"En {db.Name}, recien creada.",
                DbState.Empty => $"La base de datos {db.Name} esta vacia.",
                DbState.HasTables => $"{db.Name} ya tiene {db.Tables} tabla(s): marcalo solo si de verdad quieres migrar.",
                _ => $"No se ha podido mirar si {db.Name} esta vacia: marcalo si quieres migrar.",
            },
            Checked = state is DbState.Missing or DbState.Empty || fresh && state != DbState.Stopped,
            Available = state != DbState.Stopped,
            RunsProjectCode = true,
            DbState = state,
            Tables = db.Tables,
        };
    }

    /// <summary>
    /// PURO: lo que Roost pone en el <c>.env</c> de Laravel: <c>APP_URL</c>, la base de datos de Roost
    /// (<c>mysql</c> también para MariaDB: vale en todas las versiones de Laravel) y, si van en el
    /// stack, Mailpit (<see cref="MailConf.EnvLines"/>) y Redis con predis (<see cref="RedisConf.EnvLines"/>).
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> LaravelEnv(DeployTarget target)
    {
        var values = new List<KeyValuePair<string, string>> { new("APP_URL", target.Url) };
        if (target.Database is { } db)
        {
            values.Add(new("DB_CONNECTION", "mysql"));
            values.Add(new("DB_HOST", "127.0.0.1"));
            values.Add(new("DB_PORT", db.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            values.Add(new("DB_DATABASE", db.Name));
            values.Add(new("DB_USERNAME", DbAdmin.RootUser));
            values.Add(new("DB_PASSWORD", db.Password ?? ""));
        }
        if (target.MailSmtpPort is { } smtp) values.AddRange(Lines(MailConf.EnvLines(smtp)));
        if (target.RedisPort is { } redis) values.AddRange(Lines(RedisConf.EnvLines(redis)));
        return values;
    }

    /// <summary>
    /// PURO: lo que Roost pone en el <c>.env.local</c> de Symfony: <c>DEFAULT_URI</c> (la URL para
    /// generar enlaces desde la consola; el .env de la receta trae <c>http://localhost</c>),
    /// <c>DATABASE_URL</c> (con la versión del servidor, que Doctrine quiere saber) y, con Mailpit,
    /// <c>MAILER_DSN</c>.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> SymfonyEnv(DeployTarget target, bool doctrine = true)
    {
        var values = new List<KeyValuePair<string, string>> { new("DEFAULT_URI", target.Url) };
        if (doctrine && target.Database is { } db) values.Add(new("DATABASE_URL", DatabaseUrl(db)));
        if (target.MailSmtpPort is { } smtp) values.Add(new("MAILER_DSN", $"smtp://127.0.0.1:{smtp}"));
        return values;
    }

    /// <summary>PURO: <c>mysql://root:…@127.0.0.1:3409/api?serverVersion=11.4.13-MariaDB&amp;charset=utf8mb4</c> (usuario y contraseña escapados).</summary>
    public static string DatabaseUrl(DeployDatabase db)
    {
        var user = Uri.EscapeDataString(DbAdmin.RootUser);
        var auth = db.Password is { Length: > 0 } password ? $"{user}:{Uri.EscapeDataString(password)}" : user;
        return $"mysql://{auth}@127.0.0.1:{db.Port}/{db.Name}?serverVersion={db.ServerVersion}&charset=utf8mb4";
    }

    /// <summary>PURO: las parejas de unas líneas <c>CLAVE=valor</c> (las de <see cref="MailConf.EnvLines"/> y compañía).</summary>
    public static IEnumerable<KeyValuePair<string, string>> Lines(string lines) =>
        lines.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Split('=', 2))
            .Where(p => p.Length == 2)
            .Select(p => new KeyValuePair<string, string>(p[0], p[1]));

    /// <summary>Las claves de <paramref name="values"/> que no están (o solo están comentadas) en el fichero.</summary>
    private static IReadOnlyList<string> MissingKeys(string file, IReadOnlyList<KeyValuePair<string, string>> values)
    {
        EnvFile env;
        try { env = File.Exists(file) ? EnvFile.Load(file).Env : EnvFile.Parse(""); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
        return values.Select(v => v.Key).Where(k => !env.Has(k)).ToList();
    }

    private static string Keys(IReadOnlyList<string> keys) =>
        keys.Count <= 3 ? string.Join(", ", keys) : $"{string.Join(", ", keys.Take(2))} y {keys.Count - 2} mas";

    /// <summary>«la base de datos de Roost: 127.0.0.1:3409, root sin contrasena, tienda; APP_URL=https://tienda.test:8445; correo a Mailpit; Redis con predis».</summary>
    private static string EnvSummary(DeployTarget target, bool predisMissing)
    {
        var parts = new List<string>();
        if (target.Database is { } db)
            parts.Add($"la base de datos de Roost: 127.0.0.1:{db.Port}, root {(db.Password is null ? "sin contrasena" : "con la contrasena que guarda Roost")}, {db.Name}");
        parts.Add($"APP_URL={target.Url}");
        if (target.MailSmtpPort is not null) parts.Add("el correo a Mailpit");
        if (target.RedisPort is not null) parts.Add(predisMissing ? "Redis con predis (composer require predis/predis: el proyecto no lo tiene)" : "Redis con predis");
        return string.Join("; ", parts) + ".";
    }

    private static string SymfonySummary(DeployTarget target, bool doctrine)
    {
        var parts = new List<string> { $"DEFAULT_URI={target.Url}" };
        if (doctrine && target.Database is { } db) parts.Add($"DATABASE_URL=mysql://root@127.0.0.1:{db.Port}/{db.Name} ({db.Label})");
        if (target.MailSmtpPort is { } smtp) parts.Add($"MAILER_DSN=smtp://127.0.0.1:{smtp} (Mailpit)");
        return string.Join("; ", parts) + ".";
    }

    /// <summary>
    /// PURO: si <paramref name="version"/> cumple la restricción de PHP de un composer.json
    /// (<c>^8.2</c>, <c>&gt;=8.1</c>, <c>~8.3.0</c>, <c>8.2.*</c>, <c>^8.2|^8.3</c>). Solo mira el mínimo de
    /// cada alternativa y, con <c>^</c> o <c>~</c>, la versión mayor; null si no se entiende.
    /// </summary>
    public static bool? PhpFits(string? constraint, ComponentVersion? version)
    {
        if (string.IsNullOrWhiteSpace(constraint) || version is not { } v) return null;
        bool? any = null;
        foreach (var raw in Regex.Split(constraint, @"\|\|?"))
        {
            var part = raw.Trim();
            var m = Regex.Match(part, @"(\d+)(?:\.(\d+))?(?:\.(\d+))?");
            if (!m.Success) continue;
            var major = int.Parse(m.Groups[1].Value);
            var minor = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0;
            var meets = v.Major > major || v.Major == major && v.Minor >= minor;
            if ((part.StartsWith('^') || part.StartsWith('~') || part.Contains(".*")) && v.Major != major) meets = false;
            // «<8.0» y compañía: la versión tiene que quedar por debajo.
            if (part.StartsWith('<')) meets = v.Major < major || v.Major == major && v.Minor < minor;
            any = (any ?? false) || meets;
        }
        return any;
    }

    /// <summary>true si el composer.json (o el composer.lock) del proyecto lleva el paquete.</summary>
    private static bool HasPackage(string folder, string package)
    {
        try
        {
            foreach (var file in new[] { "composer.json", "composer.lock" })
            {
                var path = Path.Combine(folder, file);
                if (File.Exists(path) && File.ReadAllText(path).Contains($"\"{package}\"", StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return false;
    }
}
