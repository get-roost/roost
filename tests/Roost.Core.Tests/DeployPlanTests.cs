using Roost.Core.Deploy;
using Roost.Core.Install;
using Roost.Core.Models;
using Roost.Core.Platform;
using Roost.Core.Scanning;
using Roost.Core.Sites;

namespace Roost.Core.Tests;

/// <summary>
/// Una raíz de Roost de pega para los pasos de instalación: PHP 8.4 y 7.4 (ficheros vacíos), MariaDB
/// 11.4.13, un roost.toml con puertos de prueba y proyectos hechos a mano. Nada se ejecuta.
/// </summary>
internal sealed class DeployRoot : IDisposable
{
    public DeployRoot(bool services = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "roost-deploy-" + Guid.NewGuid().ToString("N"));
        Paths = new RoostPaths(root, Path.Combine(root, "cache"), WwwDir: Path.Combine(root, "www"));
        foreach (var php in new[] { "8.4.27-nts-x64", "7.4.33-nts-x64" })
        {
            var dir = Path.Combine(Paths.PhpBin, php);
            Directory.CreateDirectory(Path.Combine(dir, "ext"));
            File.WriteAllText(Path.Combine(dir, "php.exe"), "");
            foreach (var ext in new[] { "curl", "mbstring", "openssl", "pdo_mysql", "zip" })
                File.WriteAllText(Path.Combine(dir, "ext", $"php_{ext}.dll"), "");
        }
        Directory.CreateDirectory(Path.Combine(Paths.DbBin("mariadb"), "11.4.13", "bin"));
        File.WriteAllText(Path.Combine(Paths.DbBin("mariadb"), "11.4.13", "bin", "mariadbd.exe"), "");
        Directory.CreateDirectory(Paths.Root);
        File.WriteAllText(Paths.ConfigFile,
            "[php]\nversion = \"8.4\"\n\n[web]\nport = 8767\nhttps_port = 8445\n\n[db]\nengine = \"mariadb\"\nversion = \"11.4\"\nport = 3409\n"
            + (services ? "\n[redis]\nenabled = true\nport = 6382\n\n[mail]\nenabled = true\nsmtp_port = 1028\nweb_port = 8028\n" : ""));
    }

    public RoostPaths Paths { get; }

    public InstalledPhp Php84 => new PhpInstaller(Paths).ListInstalled().Single(p => p.Name.StartsWith("8.4", StringComparison.Ordinal));

    public InstalledPhp Php74 => new PhpInstaller(Paths).ListInstalled().Single(p => p.Name.StartsWith("7.4", StringComparison.Ordinal));

    /// <summary>Una carpeta en la www con estos ficheros (los que acaban en / son carpetas; el resto, con el texto que se dé o vacíos).</summary>
    public string Project(string name, params string[] files) => Project(name, files.ToDictionary(f => f, _ => ""));

    public string Project(string name, IReadOnlyDictionary<string, string> files)
    {
        var dir = Path.Combine(Paths.Www, name);
        Directory.CreateDirectory(dir);
        foreach (var (file, text) in files)
        {
            var path = Path.Combine(dir, file.TrimEnd('/').Replace('/', Path.DirectorySeparatorChar));
            if (file.EndsWith('/')) { Directory.CreateDirectory(path); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        return dir;
    }

    /// <summary>El sitio registrado sobre esa carpeta.</summary>
    public Site Site(string name, string folder) => new SiteManager(Paths).Add(name, folder).Site;

    /// <summary>El destino de los pasos con la base de datos en este estado.</summary>
    public DeployTarget Target(Site site, DbState state = DbState.Empty, int tables = 0)
    {
        var target = DeployTarget.ForSite(Paths, site, dbRunning: state != DbState.Stopped);
        return target.Database is { } db ? target with { Database = db with { State = state, Tables = tables } } : target;
    }

    public void Dispose()
    {
        try { Directory.Delete(Paths.Root, recursive: true); } catch { /* best effort */ }
    }
}

/// <summary>Qué pasos se proponen según el proyecto y cómo están la carpeta y la base de datos.</summary>
public class DeployPlanTests
{
    private const string LaravelComposer = """{ "require": { "php": "^8.2", "laravel/framework": "^13.0" } }""";

    private static IReadOnlyList<string> Ids(IEnumerable<DeployStep> steps) => steps.Select(s => s.Id).ToList();

    [Fact]
    public void Laravel_RecienClonado_ProponeTodo_EnOrden_YMarcado()
    {
        using var root = new DeployRoot();
        var folder = root.Project("tienda", new Dictionary<string, string>
        {
            ["artisan"] = "", ["composer.json"] = LaravelComposer, ["composer.lock"] = "{}", ["public/"] = "", ["package.json"] = "{}",
            [".env.example"] = "APP_KEY=\nDB_CONNECTION=sqlite\n",
        });
        var site = root.Site("tienda", folder);

        var steps = DeployPlan.For(ProjectKind.Detect(folder), root.Target(site, DbState.Missing));

        Assert.Equal(["composer", "db", "env", "key", "migrate", "storage-link", "npm"], Ids(steps));
        Assert.All(steps.Where(s => s.Id != "npm"), s => Assert.True(s.Checked, s.Id));
        var env = steps.Single(s => s.Id == "env");
        Assert.Equal(EnvMode.FromExample, env.EnvMode);
        Assert.Equal("Crear .env desde .env.example", env.Label);
        Assert.Equal("Con la base de datos de Roost: 127.0.0.1:3409, root sin contrasena, tienda; APP_URL=https://tienda.test:8445.", env.Hint);
        Assert.Equal("Crear la base de datos tienda", steps.Single(s => s.Id == "db").Label);
        Assert.Equal("En tienda, recien creada.", steps.Single(s => s.Id == "migrate").Hint);
        Assert.Equal("Instala las dependencias (hay composer.lock).", steps[0].Hint);
        Assert.True(steps[0].RunsProjectCode);

        // npm sale, pero apagado y sin poder marcarse: llega con Node.
        var npm = steps[^1];
        Assert.False(npm.Available);
        Assert.False(npm.Checked);
        Assert.Equal("Llega con Node en Roost.", npm.Hint);
    }

    [Fact]
    public void Laravel_ConLaBaseDeDatosConTablas_MigrateVaApagado()
    {
        using var root = new DeployRoot();
        var folder = root.Project("tienda", "artisan", "composer.json", "vendor/", ".env.example", "public/");
        var steps = DeployPlan.For(ProjectKind.Detect(folder), root.Target(root.Site("tienda", folder), DbState.HasTables, tables: 12));

        var migrate = steps.Single(s => s.Id == "migrate");
        Assert.False(migrate.Checked);
        Assert.True(migrate.Available);
        Assert.Equal(12, migrate.Tables);
        Assert.Contains("ya tiene 12 tabla(s)", migrate.Hint);
        Assert.DoesNotContain("db", Ids(steps));   // ya existe
        Assert.False(steps.Single(s => s.Id == "composer").Checked);   // con vendor/
        Assert.Equal("vendor/ ya esta: marcalo solo si quieres repetirlo.", steps.Single(s => s.Id == "composer").Hint);
    }

    [Theory]
    [InlineData(DbState.Unknown, true, false)]
    [InlineData(DbState.Empty, true, true)]
    [InlineData(DbState.Stopped, false, false)]
    public void Laravel_MigrateSegunComoEsteLaBaseDeDatos(DbState state, bool available, bool isChecked)
    {
        using var root = new DeployRoot();
        var folder = root.Project("tienda", "artisan", "composer.json", ".env.example");
        var steps = DeployPlan.For(ProjectKind.Detect(folder), root.Target(root.Site("tienda", folder), state));

        var migrate = steps.Single(s => s.Id == "migrate");
        Assert.Equal(available, migrate.Available);
        Assert.Equal(isChecked, migrate.Checked);
        Assert.DoesNotContain("db", Ids(steps));   // parada o sin saber: no se propone crearla
    }

    [Fact]
    public void Laravel_ConSuEnv_SoloAnadeLoQueFalta_YSinClaveNiEnlaceSiYaLosTiene()
    {
        using var root = new DeployRoot();
        var folder = root.Project("tienda", new Dictionary<string, string>
        {
            ["artisan"] = "", ["composer.json"] = LaravelComposer, ["vendor/"] = "", ["public/storage/"] = "",
            [".env"] = "APP_KEY=base64:abc\nAPP_URL=http://mi.test\nDB_CONNECTION=mysql\nDB_HOST=127.0.0.1\n# DB_PORT=3306\n",
        });
        var steps = DeployPlan.For(ProjectKind.Detect(folder), root.Target(root.Site("tienda", folder)));

        Assert.Equal(["composer", "env", "migrate"], Ids(steps));
        var env = steps.Single(s => s.Id == "env");
        Assert.Equal(EnvMode.Complete, env.EnvMode);
        Assert.Equal("Completar .env", env.Label);
        Assert.Equal(["DB_PORT", "DB_DATABASE", "DB_USERNAME", "DB_PASSWORD"], env.MissingKeys);
        Assert.Equal("Ya hay .env: se anaden DB_PORT, DB_DATABASE y 2 mas; lo que ya tiene no se toca.", env.Hint);
        Assert.True(env.Checked);
    }

    [Fact]
    public void Laravel_ConUnEnvCompleto_ElPasoSaleApagado()
    {
        using var root = new DeployRoot();
        var env = "APP_KEY=base64:abc\nAPP_URL=x\nDB_CONNECTION=mysql\nDB_HOST=h\nDB_PORT=1\nDB_DATABASE=d\nDB_USERNAME=u\nDB_PASSWORD=p\n";
        var folder = root.Project("tienda", new Dictionary<string, string> { ["artisan"] = "", [".env"] = env });
        var step = DeployPlan.For(ProjectKind.Detect(folder), root.Target(root.Site("tienda", folder))).Single(s => s.Id == "env");

        Assert.False(step.Checked);
        Assert.Empty(step.MissingKeys);
        Assert.Equal("Ya tiene todo lo de Roost: no hay nada que anadir.", step.Hint);
    }

    [Fact]
    public void Laravel_SinEjemplo_CreaUnoMinimo_YConMailpitYRedisLosPone()
    {
        using var root = new DeployRoot(services: true);
        var folder = root.Project("tienda", "artisan", "composer.json");
        var target = root.Target(root.Site("tienda", folder));
        var env = DeployPlan.For(ProjectKind.Detect(folder), target).Single(s => s.Id == "env");

        Assert.Equal(EnvMode.New, env.EnvMode);
        Assert.Equal("Crear .env", env.Label);
        Assert.StartsWith("No hay .env.example: uno minimo con la base de datos de Roost", env.Hint);
        Assert.Contains("el correo a Mailpit", env.Hint);
        Assert.Contains("Redis con predis (composer require predis/predis: el proyecto no lo tiene)", env.Hint);
        Assert.True(env.PredisMissing);

        var values = DeployPlan.LaravelEnv(target).ToDictionary(v => v.Key, v => v.Value);
        Assert.Equal("https://tienda.test:8445", values["APP_URL"]);
        Assert.Equal("mysql", values["DB_CONNECTION"]);
        Assert.Equal("3409", values["DB_PORT"]);
        Assert.Equal("", values["DB_PASSWORD"]);
        Assert.Equal("1028", values["MAIL_PORT"]);
        Assert.Equal("smtp", values["MAIL_MAILER"]);
        Assert.Equal("predis", values["REDIS_CLIENT"]);
        Assert.Equal("6382", values["REDIS_PORT"]);
    }

    [Fact]
    public void Symfony_EnvLocalConDatabaseUrl_YMigracionesSoloConDoctrine()
    {
        using var root = new DeployRoot(services: true);
        var composer = """{ "require": { "php": ">=8.2", "symfony/framework-bundle": "7.4.*", "doctrine/doctrine-migrations-bundle": "^3.4" }, "extra": { "symfony": { "require": "7.4.*" } } }""";
        var folder = root.Project("api", new Dictionary<string, string> { ["bin/console"] = "", ["symfony.lock"] = "{}", ["composer.json"] = composer, ["composer.lock"] = "{}", ["public/"] = "" });
        var target = root.Target(root.Site("api", folder));
        var kind = ProjectKind.Detect(folder);
        var steps = DeployPlan.For(kind, target);

        Assert.Equal("Symfony 7.4", kind.FrameworkLabel);
        Assert.Equal(["composer", "env", "migrate"], Ids(steps));
        var env = steps.Single(s => s.Id == "env");
        Assert.Equal(".env.local", env.File);
        Assert.Equal("Crear .env.local con DATABASE_URL", env.Label);
        Assert.Contains("DATABASE_URL=mysql://root@127.0.0.1:3409/api (MariaDB 11.4.13)", env.Hint);
        Assert.Contains("MAILER_DSN=smtp://127.0.0.1:1028", env.Hint);
        Assert.Equal("php bin/console doctrine:migrations:migrate", steps.Single(s => s.Id == "migrate").Command);

        var values = DeployPlan.SymfonyEnv(target).ToDictionary(v => v.Key, v => v.Value);
        Assert.Equal("mysql://root@127.0.0.1:3409/api?serverVersion=11.4.13-MariaDB&charset=utf8mb4", values["DATABASE_URL"]);

        // Sin Doctrine (el skeleton a secas): sin migraciones.
        var bare = root.Project("mini", "bin/console", "symfony.lock", "composer.json");
        Assert.DoesNotContain("migrate", Ids(DeployPlan.For(ProjectKind.Detect(bare), root.Target(root.Site("mini", bare)))));
    }

    [Fact]
    public void DatabaseUrl_EscapaLaContrasena()
    {
        var db = new DeployDatabase(new InstalledDb(DbEngine.MySql, "8.4.11", @"C:\x"), "my.ini", 3307, "p@ss:w/rd#1", "api");
        Assert.Equal("mysql://root:p%40ss%3Aw%2Frd%231@127.0.0.1:3307/api?serverVersion=8.4.11&charset=utf8mb4", DeployPlan.DatabaseUrl(db));
    }

    [Fact]
    public void WordPress_WpConfigConLaBaseDeDatos_YElMuPlugin()
    {
        using var root = new DeployRoot();
        var folder = root.Project("blog", "wp-load.php", "wp-config-sample.php", "wp-content/", "wp-includes/");
        var site = root.Site("blog", folder);
        var steps = DeployPlan.For(ProjectKind.Detect(folder), root.Target(site, DbState.Missing));

        Assert.Equal(["db", "wp-config", "wp-loopback"], Ids(steps));
        Assert.Equal("blog en 127.0.0.1:3409, root sin contrasena; las 8 claves y salts, nuevas.", steps[1].Hint);
        Assert.All(steps, s => Assert.True(s.Checked));
        Assert.All(steps, s => Assert.False(s.RunsProjectCode));

        // Con su wp-config.php y el mu-plugin, nada.
        File.WriteAllText(Path.Combine(folder, "wp-config.php"), "<?php");
        Directory.CreateDirectory(Path.Combine(folder, "wp-content", "mu-plugins"));
        File.WriteAllText(Path.Combine(folder, "wp-content", "mu-plugins", WpConfig.LoopbackPluginName), "<?php");
        Assert.Empty(DeployPlan.For(ProjectKind.Detect(folder), root.Target(site)));
    }

    [Fact]
    public void ProyectoComposer_SoloComposer_YUnaCarpetaASecas_Nada()
    {
        using var root = new DeployRoot();
        var composer = root.Project("lib", "composer.json", "composer.lock");
        Assert.Equal(["composer"], Ids(DeployPlan.For(ProjectKind.Detect(composer), root.Target(root.Site("lib", composer)))));

        var plain = root.Project("web", "index.php");
        Assert.Empty(DeployPlan.For(ProjectKind.Detect(plain), root.Target(root.Site("web", plain))));
    }

    [Fact]
    public void SinBaseDeDatosEnRoost_ElEnvSoloLlevaLaUrl_YNoHayMigraciones()
    {
        using var root = new DeployRoot();
        File.WriteAllText(root.Paths.ConfigFile, "[php]\nversion = \"8.4\"\n\n[web]\nhttps_port = 8445\n\n[db]\nengine = \"none\"\n");
        var folder = root.Project("tienda", "artisan", ".env.example");
        var target = root.Target(root.Site("tienda", folder));
        Assert.Null(target.Database);

        var steps = DeployPlan.For(ProjectKind.Detect(folder), target);
        Assert.Equal(["env", "key", "storage-link"], Ids(steps));
        Assert.Equal([new KeyValuePair<string, string>("APP_URL", "https://tienda.test:8445")], DeployPlan.LaravelEnv(target));
    }

    [Theory]
    [InlineData("^8.2", "8.4.27", true)]
    [InlineData("^8.2", "8.1.31", false)]
    [InlineData("^8.2", "9.0.0", false)]
    [InlineData(">=7.4", "8.4.27", true)]
    [InlineData("~8.3.0", "8.4.27", true)]
    [InlineData("8.2.*", "7.4.33", false)]
    [InlineData("^7.4|^8.0", "8.4.27", true)]
    [InlineData("^7.4 || ^8.0", "7.3.0", false)]
    public void PhpFits_LoQuePideElComposerJson(string constraint, string version, bool fits)
    {
        ComponentVersion.TryParse(version, out var v);
        Assert.Equal(fits, DeployPlan.PhpFits(constraint, v));
    }

    [Fact]
    public void PhpFits_SinRestriccion_NoSeSabe()
    {
        ComponentVersion.TryParse("8.4.27", out var v);
        Assert.Null(DeployPlan.PhpFits(null, v));
        Assert.Null(DeployPlan.PhpFits("^8.2", null));
        Assert.Null(DeployPlan.PhpFits("dev-main", v));
    }

    [Fact]
    public void ForSite_UsaElPhpDelSitio_YLaBaseDeDatosQueTieneFijada()
    {
        using var root = new DeployRoot();
        var folder = root.Project("legacy", "index.php");
        var sites = new SiteManager(root.Paths);
        sites.Add("legacy", folder, php: "7.4");
        var site = sites.SetDatabase("legacy", "vieja");

        var target = DeployTarget.ForSite(root.Paths, site, dbRunning: false);
        Assert.Equal(root.Php74, target.Php);
        Assert.Contains("7.4", target.PhpReason);
        Assert.Equal("vieja", target.Database!.Name);
        Assert.Equal(DbState.Stopped, target.Database.State);
        Assert.False(target.Database.Running);
        Assert.Equal(3409, target.Database.Port);
        Assert.Equal("https://legacy.test:8445", target.Url);
        Assert.Null(target.MailSmtpPort);
    }

    [Fact]
    public void DeployProbe_LeeSiExisteYCuantasTablas()
    {
        Assert.Equal((true, 12), DeployProbe.Parse("1\n12\nroost-ok\n"));
        Assert.Equal((false, 0), DeployProbe.Parse("0\r\n0\r\nroost-ok\r\n"));
        Assert.Null(DeployProbe.Parse("ERROR 1045 (28000): Access denied for user 'root'@'localhost'"));
        Assert.Null(DeployProbe.Parse(null));
    }

    [Fact]
    public void DeployProbe_PreguntaConElClienteYSuMyIni_YNoLanza()
    {
        var db = new DeployDatabase(new InstalledDb(DbEngine.MariaDb, "11.4.13", @"C:\roost\bin\mariadb\11.4.13"), @"C:\roost\etc\mariadb\11.4\my.ini", 3409, null, "tienda");
        var client = new Client("1\n0\nroost-ok\n");
        var probed = DeployProbe.Probe(db, client);

        Assert.Equal(DbState.Empty, probed.State);
        var call = Assert.Single(client.Calls);
        Assert.StartsWith(@"mariadb.exe --defaults-file=""C:\roost\etc\mariadb\11.4\my.ini"" -N -e", call);
        Assert.Contains("table_schema = 'tienda'", call);

        Assert.Equal(DbState.Missing, DeployProbe.Probe(db, new Client("0\n0\nroost-ok")).State);
        Assert.Equal(DbState.HasTables, DeployProbe.Probe(db, new Client("1\n3\nroost-ok")).State);
        Assert.Equal(DbState.Unknown, DeployProbe.Probe(db, new Client(null)).State);

        var stopped = db with { State = DbState.Stopped };
        var none = new Client(null);
        Assert.Equal(DbState.Stopped, DeployProbe.Probe(stopped, none).State);
        Assert.Empty(none.Calls);   // parada: ni se pregunta
    }

    private sealed class Client(string? output) : IProcessRunner
    {
        public List<string> Calls { get; } = [];

        public string? Run(string executablePath, string arguments, TimeSpan timeout)
        {
            Calls.Add($"{Path.GetFileName(executablePath)} {arguments}");
            return output;
        }
    }
}
