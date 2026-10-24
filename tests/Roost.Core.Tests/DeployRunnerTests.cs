using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Roost.Core.Composer;
using Roost.Core.Deploy;
using Roost.Core.Install;
using Roost.Core.Scanning;
using Roost.Core.Sites;

namespace Roost.Core.Tests;

/// <summary>
/// Los pasos de instalación ejecutados sobre un runner guionizado (nada llega a PHP ni a Composer),
/// un cliente de base de datos falso y zips en memoria: el orden, las órdenes que se arman, lo que se
/// escribe en el .env y el wp-config.php, que se para en el primero que falla y que una plantilla que
/// no se acaba no deja nada.
/// </summary>
public class DeployRunnerTests
{
    private sealed class SyncLines : IProgress<string>
    {
        public List<string> Lines { get; } = [];

        public void Report(string value)
        {
            lock (Lines) Lines.Add(value);
        }
    }

    /// <summary>El cliente de la base de datos: lista lo que tenga y apunta lo que se le pide.</summary>
    private sealed class DbClient : IProcessRunner
    {
        public List<string> Databases { get; } = [];

        public List<string> Calls { get; } = [];

        public string? Run(string executablePath, string arguments, TimeSpan timeout)
        {
            Calls.Add(arguments);
            if (arguments.Contains("SHOW DATABASES", StringComparison.Ordinal)) return string.Join('\n', Databases) + "\nroost-ok\n";
            var create = System.Text.RegularExpressions.Regex.Match(arguments, "CREATE DATABASE IF NOT EXISTS `(\\w+)`");
            if (create.Success) Databases.Add(create.Groups[1].Value);
            var drop = System.Text.RegularExpressions.Regex.Match(arguments, "DROP DATABASE `(\\w+)`");
            if (drop.Success) Databases.Remove(drop.Groups[1].Value);
            return "roost-ok\n";
        }
    }

    private static InstalledComposer FakeComposer(DeployRoot root)
    {
        var dir = Path.Combine(root.Paths.ComposerBin, ComposerCatalog.Stable.Version);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, InstalledComposer.PharName), "");
        return new InstalledComposer(ComposerCatalog.Stable.Version, dir);
    }

    private const string Example = "APP_NAME=Laravel\nAPP_KEY=\nAPP_URL=http://localhost\n\nDB_CONNECTION=sqlite\n# DB_HOST=127.0.0.1\n# DB_PORT=3306\n# DB_DATABASE=laravel\n# DB_USERNAME=root\n# DB_PASSWORD=\n";

    [Fact]
    public async Task Laravel_RecienClonado_TodosLosPasosEnOrden_ConElPhpDelSitioYElPhpIniDeRoost()
    {
        using var root = new DeployRoot();
        var folder = root.Project("tienda", new Dictionary<string, string>
        {
            ["artisan"] = "", ["composer.json"] = "{}", ["composer.lock"] = "{}", ["public/"] = "", [".env.example"] = Example,
        });
        var target = root.Target(root.Site("tienda", folder), DbState.Missing);
        var composer = FakeComposer(root);
        var db = new DbClient();
        var commands = new FakeCommandRunner()
            .On(composer.Phar + " install", stdout: "Installing dependencies from lock file\nGenerating optimized autoload files\n",
                onRun: s => Directory.CreateDirectory(Path.Combine(s.WorkingDirectory!, "vendor")))
            .On("artisan key:generate", stdout: "INFO  Application key set successfully.\n",
                onRun: s => File.WriteAllText(Path.Combine(s.WorkingDirectory!, ".env"), File.ReadAllText(Path.Combine(s.WorkingDirectory!, ".env")).Replace("APP_KEY=\n", "APP_KEY=base64:nueva\n")))
            .On("artisan migrate", stdout: "INFO  Running migrations.\n")
            .On("artisan storage:link", stdout: "INFO  The [public/storage] link has been connected to [storage/app/public].\n",
                onRun: s => Directory.CreateDirectory(Path.Combine(s.WorkingDirectory!, "public", "storage")));
        var runner = new DeployRunner(root.Paths, commands, db) { Composer = composer };
        var steps = DeployPlan.For(ProjectKind.Detect(folder), target);
        var events = new List<DeployEvent>();

        var run = await runner.RunAsync(target, steps, new SyncEvents(events), CancellationToken.None);

        Assert.True(run.Succeeded);
        Assert.Equal(["composer", "db", "env", "key", "migrate", "storage-link"], run.Results.Select(r => r.Step.Id));
        Assert.All(run.Results, r => Assert.Equal(DeployOutcomeKind.Done, r.Outcome.Kind));
        Assert.Equal(
        [
            composer.Phar + " install --no-progress",
            "artisan key:generate --no-interaction",
            "artisan migrate --no-interaction",
            "artisan storage:link --no-interaction",
        ], commands.Commands);
        Assert.All(commands.Calls, c =>
        {
            Assert.Equal(root.Php84.Executable, c.Exe);
            Assert.Equal(folder, c.WorkingDirectory);
            Assert.Equal(Path.Combine(root.Paths.Etc, "php", root.Php84.Name), c.Environment!["PHPRC"]);
        });
        Assert.Equal("1", commands.Calls[0].Environment!["COMPOSER_NO_INTERACTION"]);
        Assert.Contains(db.Calls, c => c.Contains("CREATE DATABASE IF NOT EXISTS `tienda`", StringComparison.Ordinal));

        // El .env: el del ejemplo con lo de Roost encima, en su sitio, y la clave puesta.
        var env = EnvFile.Load(Path.Combine(folder, ".env")).Env;
        Assert.Equal("mysql", env.Get("DB_CONNECTION"));
        Assert.Equal("3409", env.Get("DB_PORT"));
        Assert.Equal("tienda", env.Get("DB_DATABASE"));
        Assert.Equal("https://tienda.test:8445", env.Get("APP_URL"));
        Assert.Equal("base64:nueva", env.Get("APP_KEY"));
        Assert.DoesNotContain("# DB_", File.ReadAllText(Path.Combine(folder, ".env")));

        // Cada paso empieza, dice sus líneas y acaba; y todo queda en el log del sitio.
        Assert.Equal(6, events.Count(e => e.Started));
        Assert.Contains(events, e => e.Line == "Installing dependencies from lock file");
        Assert.Contains(events, e => e.Line == "  + DB_DATABASE=tienda");
        var log = File.ReadAllText(DeployLog.FilePath(root.Paths, "tienda"));
        Assert.Contains("=== ", log);
        Assert.Contains("composer install", log);
        Assert.Contains("Application key set successfully", log);
    }

    [Fact]
    public async Task UnPasoQueFalla_ParaAhi_YDevuelveLasUltimasQuinceLineas()
    {
        using var root = new DeployRoot();
        var folder = root.Project("tienda", "artisan", "composer.json", ".env.example");
        var target = root.Target(root.Site("tienda", folder));
        var composer = FakeComposer(root);
        var output = string.Join('\n', Enumerable.Range(1, 30).Select(i => $"linea {i}"));
        var commands = new FakeCommandRunner().On(composer.Phar + " install", exitCode: 2, stdout: output, stderr: "Your requirements could not be resolved to an installable set of packages.");
        var runner = new DeployRunner(root.Paths, commands) { Composer = composer };

        var run = await runner.RunAsync(target, DeployPlan.For(ProjectKind.Detect(folder), target), null, CancellationToken.None);

        Assert.False(run.Succeeded);
        Assert.Equal(0, run.FailedIndex);
        var (step, outcome) = run.Failure!.Value;
        Assert.Equal("composer", step.Id);
        Assert.Equal(DeployOutcomeKind.Failed, outcome.Kind);
        Assert.Equal(2, outcome.ExitCode);
        Assert.Equal("composer install termino con el codigo 2.", outcome.Note);
        Assert.Equal(DeployRunner.TailLines, outcome.Tail.Count);
        Assert.Equal("Your requirements could not be resolved to an installable set of packages.", outcome.Tail[^1]);
        Assert.Equal("linea 17", outcome.Tail[0]);   // las 14 últimas de stdout y la de stderr
        Assert.Single(commands.Calls);   // nada después del que falla
        Assert.False(File.Exists(Path.Combine(folder, ".env")));

        // «Reintentar desde aquí»: desde ese paso, con lo que ya va bien.
        commands.On(composer.Phar + " install", stdout: "ok\n");
        commands.On("artisan", stdout: "ok\n");
        var retry = await runner.RunAsync(target, DeployPlan.For(ProjectKind.Detect(folder), target), null, CancellationToken.None, from: run.FailedIndex!.Value);
        Assert.True(retry.Succeeded);
        Assert.True(File.Exists(Path.Combine(folder, ".env")));
    }

    [Fact]
    public async Task UnPasoQueNoArranca_EsUnFallo_ConSuMotivo()
    {
        using var root = new DeployRoot();
        var folder = root.Project("tienda", "artisan", "composer.json");
        var target = root.Target(root.Site("tienda", folder));
        var runner = new DeployRunner(root.Paths, new FakeCommandRunner());   // sin Composer
        var step = DeployPlan.For(ProjectKind.Detect(folder), target).Single(s => s.Id == "composer");

        var outcome = await runner.RunStepAsync(target, step, null, CancellationToken.None);

        Assert.Equal(DeployOutcomeKind.Failed, outcome.Kind);
        Assert.StartsWith("Composer no esta instalado", outcome.Note);
        Assert.Equal(outcome.Note, outcome.Tail[^1]);
    }

    [Fact]
    public async Task Cancelar_Lanza_YQuedaEnElLog()
    {
        using var root = new DeployRoot();
        var folder = root.Project("tienda", "artisan", "composer.json");
        var target = root.Target(root.Site("tienda", folder));
        var runner = new DeployRunner(root.Paths, new FakeCommandRunner().On("", stdout: "nunca")) { Composer = FakeComposer(root) };
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunStepAsync(target, DeployPlan.For(ProjectKind.Detect(folder), target)[0], null, cancel.Token));
        Assert.Contains("Cancelado.", File.ReadAllText(DeployLog.FilePath(root.Paths, "tienda")));
    }

    [Fact]
    public async Task ConSuEnv_NoPisaNada_SoloAnadeLoQueFalta_YNuncaEnsenaLaContrasena()
    {
        using var root = new DeployRoot();
        new Roost.Core.Stack.DbPasswords(root.Paths).Set(new InstalledDb(DbEngine.MariaDb, "11.4.13", Path.Combine(root.Paths.DbBin("mariadb"), "11.4.13")), "s3cr3t!");
        var original = "# mio\r\nAPP_KEY=base64:mia\r\nDB_CONNECTION=sqlite\r\nDB_DATABASE=la_mia\r\n";
        var folder = root.Project("tienda", new Dictionary<string, string> { ["artisan"] = "", [".env"] = original, ["public/storage/"] = "" });
        var target = root.Target(root.Site("tienda", folder));
        var runner = new DeployRunner(root.Paths, new FakeCommandRunner());
        var step = DeployPlan.For(ProjectKind.Detect(folder), target).Single(s => s.Id == "env");
        var lines = new SyncLines();

        var outcome = await runner.RunStepAsync(target, step, lines, CancellationToken.None);

        Assert.Equal(DeployOutcomeKind.Done, outcome.Kind);
        var text = File.ReadAllText(Path.Combine(folder, ".env"));
        // Lo suyo, intacto y con su CRLF; lo nuevo, tras su grupo y también con CRLF.
        Assert.StartsWith("# mio\r\nAPP_KEY=base64:mia\r\nAPP_URL=https://tienda.test:8445\r\nDB_CONNECTION=sqlite\r\nDB_DATABASE=la_mia\r\nDB_HOST=127.0.0.1\r\n", text);
        Assert.DoesNotContain("\n", text.Replace("\r\n", ""));
        var env = EnvFile.Parse(text);
        Assert.Equal("sqlite", env.Get("DB_CONNECTION"));
        Assert.Equal("la_mia", env.Get("DB_DATABASE"));
        Assert.Equal("s3cr3t!", env.Get("DB_PASSWORD"));
        Assert.Equal("https://tienda.test:8445", env.Get("APP_URL"));
        Assert.DoesNotContain(lines.Lines, l => l.Contains("s3cr3t!", StringComparison.Ordinal));
        Assert.Contains("  + DB_PASSWORD=(la que guarda Roost)", lines.Lines);
        Assert.Contains("  = DB_DATABASE (se deja el que tenia)", lines.Lines);
        Assert.DoesNotContain("s3cr3t!", File.ReadAllText(DeployLog.FilePath(root.Paths, "tienda")));

        // Otra vez: ya no hay nada que añadir.
        var again = await runner.RunStepAsync(target, step, null, CancellationToken.None);
        Assert.Equal(DeployOutcomeKind.Skipped, again.Kind);
    }

    [Fact]
    public async Task LaClave_SiYaEstaSeSalta_SinLlamarAArtisan()
    {
        using var root = new DeployRoot();
        var folder = root.Project("tienda", new Dictionary<string, string> { ["artisan"] = "", [".env"] = "APP_KEY=base64:ya\n" });
        var target = root.Target(root.Site("tienda", folder));
        var commands = new FakeCommandRunner();
        var runner = new DeployRunner(root.Paths, commands);
        var key = new DeployStep { Id = "key", Kind = DeployStepKind.AppKey, Label = "php artisan key:generate", Hint = "", Checked = true };

        var outcome = await runner.RunStepAsync(target, key, null, CancellationToken.None);

        Assert.Equal(DeployOutcomeKind.Skipped, outcome.Kind);
        Assert.StartsWith("Ya tiene APP_KEY", outcome.Note);
        Assert.Empty(commands.Calls);

        // Sin .env no hay donde escribirla.
        File.Delete(Path.Combine(folder, ".env"));
        var none = await runner.RunStepAsync(target, key, null, CancellationToken.None);
        Assert.Equal(DeployOutcomeKind.Failed, none.Kind);
    }

    [Fact]
    public async Task Symfony_EnvLocal_ConDatabaseUrlYUnSecreto_YLuegoSinPisar()
    {
        using var root = new DeployRoot(services: true);
        var folder = root.Project("api", new Dictionary<string, string>
        {
            ["bin/console"] = "", ["symfony.lock"] = "{}", ["composer.json"] = """{ "require": { "doctrine/doctrine-bundle": "^2.13" } }""",
            [".env"] = "APP_ENV=dev\nAPP_SECRET=\nDATABASE_URL=\"postgresql://app:!ChangeMe!@127.0.0.1:5432/app?serverVersion=16&charset=utf8\"\n",
        });
        var target = root.Target(root.Site("api", folder));
        var runner = new DeployRunner(root.Paths, new FakeCommandRunner());
        var step = DeployPlan.For(ProjectKind.Detect(folder), target).Single(s => s.Id == "env");
        var lines = new SyncLines();

        Assert.Equal(DeployOutcomeKind.Done, (await runner.RunStepAsync(target, step, lines, CancellationToken.None)).Kind);

        var local = EnvFile.Load(Path.Combine(folder, ".env.local")).Env;
        Assert.Equal("mysql://root@127.0.0.1:3409/api?serverVersion=11.4.13-MariaDB&charset=utf8mb4", local.Get("DATABASE_URL"));
        Assert.Equal("smtp://127.0.0.1:1028", local.Get("MAILER_DSN"));
        Assert.Equal("https://api.test:8445", local.Get("DEFAULT_URI"));
        Assert.Matches("^[0-9a-f]{32}$", local.Get("APP_SECRET")!);
        Assert.Contains("  + APP_SECRET=(uno nuevo, al azar)", lines.Lines);
        Assert.Contains("APP_ENV=dev", File.ReadAllText(Path.Combine(folder, ".env")));   // el .env, que va a git, no se toca

        // Con el .env.local suyo, solo se añade lo que falta.
        File.WriteAllText(Path.Combine(folder, ".env.local"), "DATABASE_URL=\"sqlite:///%kernel.project_dir%/var/data.db\"\nAPP_SECRET=mio\n");
        var complete = DeployPlan.For(ProjectKind.Detect(folder), target).Single(s => s.Id == "env");
        Assert.Equal(EnvMode.Complete, complete.EnvMode);
        await runner.RunStepAsync(target, complete, null, CancellationToken.None);
        local = EnvFile.Load(Path.Combine(folder, ".env.local")).Env;
        Assert.Equal("sqlite:///%kernel.project_dir%/var/data.db", local.Get("DATABASE_URL"));
        Assert.Equal("mio", local.Get("APP_SECRET"));
        Assert.Equal("smtp://127.0.0.1:1028", local.Get("MAILER_DSN"));
    }

    [Fact]
    public async Task Symfony_Migraciones_ConBinConsole()
    {
        using var root = new DeployRoot();
        var folder = root.Project("api", "bin/console", "symfony.lock");
        var target = root.Target(root.Site("api", folder));
        var commands = new FakeCommandRunner().On("", stdout: "[OK] Already at the latest version\n");
        var migrate = new DeployStep { Id = "migrate", Kind = DeployStepKind.Migrate, Label = "php bin/console doctrine:migrations:migrate", Hint = "", Checked = true };

        var outcome = await new DeployRunner(root.Paths, commands).RunStepAsync(target, migrate, null, CancellationToken.None);

        Assert.Equal(DeployOutcomeKind.Done, outcome.Kind);
        Assert.Equal([Path.Combine("bin", "console"), "doctrine:migrations:migrate", "--no-interaction", "--allow-no-migration"], commands.Calls.Single().Arguments);
    }

    [Fact]
    public async Task WordPress_WpConfigYMuPlugin_YSiYaEstanNoSeTocan()
    {
        using var root = new DeployRoot();
        var sample = "<?php\ndefine( 'DB_NAME', 'database_name_here' );\ndefine( 'DB_USER', 'username_here' );\ndefine( 'DB_PASSWORD', 'password_here' );\ndefine( 'DB_HOST', 'localhost' );\ndefine( 'AUTH_KEY', 'put your unique phrase here' );\n";
        var folder = root.Project("blog", new Dictionary<string, string> { ["wp-load.php"] = "", ["wp-config-sample.php"] = "﻿" + sample, ["wp-content/"] = "" });
        var target = root.Target(root.Site("blog", folder), DbState.Missing);
        var db = new DbClient();
        var runner = new DeployRunner(root.Paths, new FakeCommandRunner(), db);
        var lines = new SyncLines();

        var run = await runner.RunAsync(target, DeployPlan.For(ProjectKind.Detect(folder), target), new SyncEvents([]), CancellationToken.None);

        Assert.True(run.Succeeded);
        Assert.Equal(["db", "wp-config", "wp-loopback"], run.Results.Select(r => r.Step.Id));
        var config = File.ReadAllBytes(Path.Combine(folder, "wp-config.php"));
        Assert.NotEqual(0xEF, config[0]);   // sin BOM delante de <?php
        var text = Encoding.UTF8.GetString(config);
        Assert.Equal("blog", WpConfig.Read(text, "DB_NAME"));
        Assert.Equal("127.0.0.1:3409", WpConfig.Read(text, "DB_HOST"));
        Assert.DoesNotContain(WpConfig.SamplePhrase, text);
        Assert.Equal(WpConfig.LoopbackPlugin.Replace("\r\n", "\n"), File.ReadAllText(Path.Combine(folder, "wp-content", "mu-plugins", WpConfig.LoopbackPluginName)));
        Assert.Contains("blog", db.Databases);

        // Otra vez: el wp-config.php que hay no se pisa, y la base de datos ya existe.
        File.WriteAllText(Path.Combine(folder, "wp-config.php"), "<?php // el mio");
        var steps = new[] { "db", "wp-config", "wp-loopback" }.Select(id => run.Results.Single(r => r.Step.Id == id).Step).ToList();
        foreach (var step in steps)
            Assert.Equal(DeployOutcomeKind.Skipped, (await runner.RunStepAsync(target, step, lines, CancellationToken.None)).Kind);
        Assert.Equal("<?php // el mio", File.ReadAllText(Path.Combine(folder, "wp-config.php")));
    }

    [Fact]
    public async Task Npm_NuncaEjecutaNada()
    {
        using var root = new DeployRoot();
        var folder = root.Project("tienda", "artisan", "package.json");
        var target = root.Target(root.Site("tienda", folder));
        var commands = new FakeCommandRunner();
        var npm = DeployPlan.For(ProjectKind.Detect(folder), target).Single(s => s.Id == "npm");

        var outcome = await new DeployRunner(root.Paths, commands).RunStepAsync(target, npm, null, CancellationToken.None);
        Assert.Equal(DeployOutcomeKind.Skipped, outcome.Kind);
        Assert.Empty(commands.Calls);

        // Y RunAsync ni lo intenta: no está disponible.
        var run = await new DeployRunner(root.Paths, commands).RunAsync(target, [npm with { Checked = true }], null, CancellationToken.None);
        Assert.Empty(run.Results);
    }

    // --- Plantillas ---

    [Fact]
    public async Task PlantillaLaravel_CreateProjectEnUnTemporal_LoRegistra_YConUnFalloDespuesUndoLoQuitaTodo()
    {
        using var root = new DeployRoot();
        var plan = ProjectTemplates.Plan(root.Paths, new NewProjectRequest(ProjectTemplate.Laravel, "tienda", Database: "tienda"));
        var target = ProjectTemplates.Target(root.Paths, plan, dbRunning: true) is var t && t.Database is { } d ? t with { Database = d with { State = DbState.Missing } } : null!;
        var composer = FakeComposer(root);
        var db = new DbClient();
        string? temp = null;
        var commands = new FakeCommandRunner()
            .On(composer.Phar + " create-project laravel/laravel", stdout: "Creating a \"laravel/laravel\" project\n", onRun: s =>
            {
                temp = s.Arguments[3];
                // Lo que deja de verdad: el proyecto con su .env (de los scripts), su clave y su public/.
                Directory.CreateDirectory(Path.Combine(temp, "public"));
                Directory.CreateDirectory(Path.Combine(temp, "vendor"));
                File.WriteAllText(Path.Combine(temp, "artisan"), "");
                File.WriteAllText(Path.Combine(temp, ".env.example"), Example);
                File.WriteAllText(Path.Combine(temp, ".env"), Example.Replace("APP_KEY=", "APP_KEY=base64:de-los-scripts"));
            })
            .On("artisan migrate", exitCode: 1, stderr: "SQLSTATE[HY000] [2002] No connection could be made\n");
        var runner = new DeployRunner(root.Paths, commands, db) { Composer = composer, Template = plan };
        var steps = ProjectTemplates.Steps(plan, target);

        Assert.Equal(["create", "db", "env", "key", "migrate", "storage-link", "npm"], steps.Select(s => s.Id));
        Assert.True(steps[0].Required);
        var run = await runner.RunAsync(target, steps, null, CancellationToken.None);

        // create-project en www/.tmp-<guid> desde la www, y después a su sitio.
        var create = commands.Calls[0];
        Assert.Equal(root.Paths.Www, create.WorkingDirectory);
        Assert.StartsWith(Path.Combine(root.Paths.Www, ".tmp-"), temp);
        Assert.Equal("--no-progress", create.Arguments[^1]);
        Assert.False(Directory.Exists(temp));
        Assert.True(Directory.Exists(plan.Folder));

        // La clave la pusieron los scripts: se salta. El .env de la plantilla es de Roost: va con la BD.
        Assert.Equal(DeployOutcomeKind.Skipped, run.Results.Single(r => r.Step.Id == "key").Outcome.Kind);
        Assert.Equal("mysql", EnvFile.Load(Path.Combine(plan.Folder, ".env")).Env.Get("DB_CONNECTION"));
        var site = new SiteRegistry(root.Paths).Find("tienda")!;
        Assert.Equal("public", site.Root);
        Assert.Equal("tienda", site.Database);
        Assert.Null(site.Php);

        // Falló migrate: Undo quita el sitio, la base de datos que creó y la carpeta.
        Assert.Equal("migrate", run.Failure!.Value.Step.Id);
        Assert.True(runner.Journal.SiteCreated && runner.Journal.DatabaseCreated && runner.Journal.FolderCreated);
        var undone = runner.Undo(target);
        Assert.Equal(["el sitio tienda.test", "la base de datos tienda", $"la carpeta {plan.Folder}"], undone);
        Assert.Null(new SiteRegistry(root.Paths).Find("tienda"));
        Assert.DoesNotContain("tienda", db.Databases);
        Assert.False(Directory.Exists(plan.Folder));
        Assert.Empty(Directory.GetDirectories(root.Paths.Www));
        Assert.False(runner.Journal.Any);
    }

    [Fact]
    public async Task PlantillaQueFallaAlCrear_NoDejaNi_Carpeta_NiSitio_YUnaCarpetaVaciaSigueVacia()
    {
        using var root = new DeployRoot();
        var folder = Path.Combine(root.Paths.Www, "api");
        Directory.CreateDirectory(folder);   // ya existía, vacía
        var plan = ProjectTemplates.Plan(root.Paths, new NewProjectRequest(ProjectTemplate.Symfony, "api", Webapp: true));
        Assert.True(plan.FolderExists);
        var target = ProjectTemplates.Target(root.Paths, plan, dbRunning: false);
        var composer = FakeComposer(root);
        var commands = new FakeCommandRunner()
            .On(composer.Phar + " create-project symfony/skeleton", onRun: s => File.WriteAllText(Path.Combine(Directory.CreateDirectory(s.Arguments[3]).FullName, "composer.json"), "{}"))
            .On(composer.Phar + " config --json extra.symfony.docker false")
            .On(composer.Phar + " require --no-progress webapp", exitCode: 1, stderr: "Could not find package webapp\n");
        var runner = new DeployRunner(root.Paths, commands) { Composer = composer, Template = plan };

        var run = await runner.RunAsync(target, ProjectTemplates.Steps(plan, target), null, CancellationToken.None);

        Assert.Equal(0, run.FailedIndex);
        Assert.Equal(3, commands.Calls.Count);
        Assert.Equal(commands.Calls[0].Arguments[3], commands.Calls[1].WorkingDirectory);   // webapp, en el temporal
        Assert.True(Directory.Exists(folder));
        Assert.Empty(Directory.EnumerateFileSystemEntries(folder));
        Assert.Equal([folder], Directory.GetDirectories(root.Paths.Www));   // ni el .tmp-
        Assert.Null(new SiteRegistry(root.Paths).Find("api"));
        Assert.Empty(runner.Undo(target));   // nada que quitar
        Assert.True(Directory.Exists(folder));
    }

    [Fact]
    public async Task PlantillaWordPress_BajaElZip_LoCompruebaConSuSha1_YLoDescomprime()
    {
        using var root = new DeployRoot();
        var zip = WordPressZip();
        var handler = new WordPressHandler(zip, Convert.ToHexStringLower(SHA1.HashData(zip)));
        var plan = ProjectTemplates.Plan(root.Paths, new NewProjectRequest(ProjectTemplate.WordPress, "blog", Database: "blog"));
        var target = ProjectTemplates.Target(root.Paths, plan, dbRunning: false);
        var runner = new DeployRunner(root.Paths, new FakeCommandRunner()) { Http = new HttpClient(handler), Template = plan };
        var lines = new SyncLines();

        var outcome = await runner.RunStepAsync(target, ProjectTemplates.Steps(plan, target)[0], lines, CancellationToken.None);

        Assert.Equal(DeployOutcomeKind.Done, outcome.Kind);
        Assert.True(File.Exists(Path.Combine(plan.Folder, "wp-load.php")));   // sin la carpeta wordpress/ del zip
        Assert.True(File.Exists(Path.Combine(plan.Folder, "wp-config-sample.php")));
        Assert.True(File.Exists(Path.Combine(root.Paths.Downloads, "wordpress-7.1.3.zip")));
        Assert.Contains(lines.Lines, l => l.StartsWith("SHA-1 comprobado: ", StringComparison.Ordinal));
        Assert.Contains(lines.Lines, l => l.StartsWith("Descargando wordpress-7.1.3.zip: 100%", StringComparison.Ordinal));
        var site = new SiteRegistry(root.Paths).Find("blog")!;
        Assert.Null(site.Root);
        Assert.Equal("blog", site.Database);

        // Con el SHA-1 que no cuadra, no queda nada: ni carpeta, ni zip a medias.
        var bad = new DeployRoot();
        try
        {
            var badPlan = ProjectTemplates.Plan(bad.Paths, new NewProjectRequest(ProjectTemplate.WordPress, "blog"));
            var badTarget = ProjectTemplates.Target(bad.Paths, badPlan, dbRunning: false);
            var badRunner = new DeployRunner(bad.Paths, new FakeCommandRunner()) { Http = new HttpClient(new WordPressHandler(zip, new string('0', 40))), Template = badPlan };
            var failed = await badRunner.RunStepAsync(badTarget, ProjectTemplates.Steps(badPlan, badTarget)[0], null, CancellationToken.None);
            Assert.Equal(DeployOutcomeKind.Failed, failed.Kind);
            Assert.Contains("El SHA-1 de wordpress-7.1.3.zip no coincide", failed.Note);
            Assert.False(Directory.Exists(badPlan.Folder));
            Assert.Empty(Directory.Exists(bad.Paths.Downloads) ? Directory.GetFiles(bad.Paths.Downloads) : []);
            Assert.Null(new SiteRegistry(bad.Paths).Find("blog"));
        }
        finally
        {
            bad.Dispose();
        }
    }

    /// <summary>Un zip como el de wordpress.org: todo dentro de wordpress/.</summary>
    internal static byte[] WordPressZip()
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in new[] { ("wordpress/wp-load.php", "<?php"), ("wordpress/wp-config-sample.php", "<?php define( 'DB_NAME', 'database_name_here' );"), ("wordpress/wp-content/index.php", "<?php") })
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                writer.Write(text);
            }
        }
        return memory.ToArray();
    }

    /// <summary>La API de versiones, el .sha1 y el zip de wordpress.org, en memoria.</summary>
    internal sealed class WordPressHandler(byte[] zip, string sha1) : HttpMessageHandler
    {
        public const string Api = """
            {"offers":[{"response":"upgrade","download":"https://downloads.wordpress.org/release/wordpress-7.1.3.zip","locale":"en_US",
              "packages":{"full":"https://downloads.wordpress.org/release/wordpress-7.1.3.zip"},"current":"7.1.3","version":"7.1.3","php_version":"7.4","mysql_version":"5.5.5"}]}
            """;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            HttpContent? content = url switch
            {
                "https://api.wordpress.org/core/version-check/1.7/" => new StringContent(Api),
                "https://downloads.wordpress.org/release/wordpress-7.1.3.zip.sha1" => new StringContent(sha1),
                "https://downloads.wordpress.org/release/wordpress-7.1.3.zip" => new ByteArrayContent(zip),
                _ => null,
            };
            return Task.FromResult(content is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class SyncEvents(List<DeployEvent> events) : IProgress<DeployEvent>
    {
        public void Report(DeployEvent value)
        {
            lock (events) events.Add(value);
        }
    }
}
