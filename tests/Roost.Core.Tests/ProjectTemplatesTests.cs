using Roost.Core.Deploy;
using Roost.Core.Install;
using Roost.Core.Sites;

namespace Roost.Core.Tests;

/// <summary>Las plantillas: qué se valida antes de tocar nada, qué PHP valen y qué pasos tocan. Y la API de wordpress.org.</summary>
public class ProjectTemplatesTests
{
    [Fact]
    public void Find_PorSuNombre_SinImportarMayusculas()
    {
        Assert.Equal(ProjectTemplate.Laravel, ProjectTemplates.Find("Laravel")!.Template);
        Assert.Equal(ProjectTemplate.WordPress, ProjectTemplates.Find(" wordpress ")!.Template);
        Assert.Equal(ProjectTemplate.Symfony, ProjectTemplates.Find("symfony")!.Template);
        Assert.Null(ProjectTemplates.Find("drupal"));
    }

    [Fact]
    public void SuitablePhp_SoloLosQueValenParaLaVersionActual()
    {
        using var root = new DeployRoot();
        var installed = new PhpInstaller(root.Paths).ListInstalled();

        Assert.Equal([root.Php84], ProjectTemplates.SuitablePhp(ProjectTemplates.Laravel, installed));   // Laravel 13 pide 8.3
        Assert.Equal([root.Php84], ProjectTemplates.SuitablePhp(ProjectTemplates.Symfony, installed));
        Assert.Equal([root.Php84, root.Php74], ProjectTemplates.SuitablePhp(ProjectTemplates.WordPress, installed));
    }

    [Fact]
    public void Plan_ResuelveCarpetaYPhp_YLoValidaTodoAntes()
    {
        using var root = new DeployRoot();

        var plan = ProjectTemplates.Plan(root.Paths, new NewProjectRequest(ProjectTemplate.Laravel, " Tienda.test ", Database: "tienda"));
        Assert.Equal("tienda", plan.Name);
        Assert.Equal(Path.Combine(root.Paths.Www, "tienda"), plan.Folder);
        Assert.Equal(root.Php84, plan.Php);
        Assert.Null(plan.PhpSpec);
        Assert.False(plan.FolderExists);

        // Con el PHP 7.4 no hay Laravel 13: se dice cuál vale.
        var old = Assert.Throws<RoostException>(() => ProjectTemplates.Plan(root.Paths, new NewProjectRequest(ProjectTemplate.Laravel, "tienda", Php: "7.4")));
        Assert.Contains("Laravel 13 pide PHP 8.3 o mas nuevo", old.Message);
        Assert.Contains("tienes 8.4.27", old.Message);
        // WordPress sí.
        Assert.Equal(root.Php74, ProjectTemplates.Plan(root.Paths, new NewProjectRequest(ProjectTemplate.WordPress, "blog", Php: "7.4")).Php);

        Assert.Throws<RoostException>(() => ProjectTemplates.Plan(root.Paths, new NewProjectRequest(ProjectTemplate.Laravel, "Mi Tienda")));
        Assert.Throws<RoostException>(() => ProjectTemplates.Plan(root.Paths, new NewProjectRequest(ProjectTemplate.Laravel, "tienda", Php: "8.1")));
        Assert.Throws<RoostException>(() => ProjectTemplates.Plan(root.Paths, new NewProjectRequest(ProjectTemplate.Laravel, "tienda", Database: "mysql")));
        Assert.Throws<RoostException>(() => ProjectTemplates.Plan(root.Paths, new NewProjectRequest(ProjectTemplate.Laravel, "tienda", Webapp: true)));

        // Una carpeta con algo dentro no vale; vacía, sí.
        var busy = root.Project("ocupada", "index.php");
        var ex = Assert.Throws<RoostException>(() => ProjectTemplates.Plan(root.Paths, new NewProjectRequest(ProjectTemplate.Symfony, "api", Folder: busy)));
        Assert.Contains("no esta vacia", ex.Message);
        Directory.CreateDirectory(Path.Combine(root.Paths.Www, "vacia"));
        Assert.True(ProjectTemplates.Plan(root.Paths, new NewProjectRequest(ProjectTemplate.Symfony, "api", Folder: "vacia")).FolderExists);

        // Un sitio que ya existe no se pisa.
        root.Site("tienda", root.Project("tienda", "index.php"));
        Assert.Contains("Ya hay un sitio 'tienda'", Assert.Throws<RoostException>(() => ProjectTemplates.Plan(root.Paths, new NewProjectRequest(ProjectTemplate.WordPress, "tienda"))).Message);
    }

    [Fact]
    public void Steps_CadaPlantillaConLoSuyo()
    {
        using var root = new DeployRoot();
        DeployTarget Target(NewProjectPlan plan) =>
            ProjectTemplates.Target(root.Paths, plan, dbRunning: true) is var t && t.Database is { } db ? t with { Database = db with { State = DbState.Missing } } : t;

        var laravel = ProjectTemplates.Plan(root.Paths, new NewProjectRequest(ProjectTemplate.Laravel, "tienda", Database: "tienda"));
        var steps = ProjectTemplates.Steps(laravel, Target(laravel));
        Assert.Equal(["create", "db", "env", "key", "migrate", "storage-link", "npm"], steps.Select(s => s.Id));
        Assert.Equal("composer create-project laravel/laravel", steps[0].Command);
        Assert.Equal(EnvMode.Fresh, steps.Single(s => s.Id == "env").EnvMode);
        Assert.Equal("Poner la base de datos de Roost en .env", steps.Single(s => s.Id == "env").Label);
        Assert.True(steps.Single(s => s.Id == "migrate").Checked);

        var symfony = ProjectTemplates.Plan(root.Paths, new NewProjectRequest(ProjectTemplate.Symfony, "api", Database: "api", Webapp: true));
        var sf = ProjectTemplates.Steps(symfony, Target(symfony));
        Assert.Equal(["create", "db", "env", "migrate"], sf.Select(s => s.Id));
        Assert.Equal("composer create-project symfony/skeleton y composer require webapp", sf[0].Label);
        Assert.StartsWith("Symfony 8.1 en", sf[0].Hint);   // con PHP 8.4

        var bare = ProjectTemplates.Plan(root.Paths, new NewProjectRequest(ProjectTemplate.Symfony, "mini"));
        Assert.Equal(["create"], ProjectTemplates.Steps(bare, Target(bare)).Select(s => s.Id));

        var wordpress = ProjectTemplates.Plan(root.Paths, new NewProjectRequest(ProjectTemplate.WordPress, "blog", Database: "blog"));
        var wp = ProjectTemplates.Steps(wordpress, Target(wordpress));
        Assert.Equal(["create", "db", "wp-config", "wp-loopback"], wp.Select(s => s.Id));
        Assert.False(wp[0].RunsProjectCode);
        Assert.All(wp, s => Assert.True(s.Checked));
    }

    [Fact]
    public void Target_ConLaUrlDelSitioQueVaASer_YLaBaseDeDatosPedida()
    {
        using var root = new DeployRoot(services: true);
        var plan = ProjectTemplates.Plan(root.Paths, new NewProjectRequest(ProjectTemplate.Laravel, "tienda", Php: "8.4", Database: "tienda_db"));
        var target = ProjectTemplates.Target(root.Paths, plan, dbRunning: false);

        Assert.True(target.Fresh);
        Assert.Equal("https://tienda.test:8445", target.Url);
        Assert.Equal("tienda_db", target.Database!.Name);
        Assert.Equal(DbState.Stopped, target.Database.State);
        Assert.Equal(1028, target.MailSmtpPort);
        Assert.Equal(6382, target.RedisPort);
        Assert.Equal("8.4", plan.PhpSpec);
        Assert.Null(ProjectTemplates.Target(root.Paths, plan with { Database = null }, dbRunning: true).Database);
    }

    // --- La API de wordpress.org ---

    [Fact]
    public void WordPress_LaUltimaVersionDeLaApi()
    {
        var release = WordPressCatalog.Parse(DeployRunnerTests.WordPressHandler.Api);
        Assert.Equal("7.1.3", release.Version);
        Assert.Equal("https://downloads.wordpress.org/release/wordpress-7.1.3.zip", release.Download.ToString());
        Assert.Equal("https://downloads.wordpress.org/release/wordpress-7.1.3.zip.sha1", release.ChecksumUrl.ToString());
        Assert.Equal("7.4", release.MinPhp);
        Assert.Equal("wordpress-7.1.3.zip", release.FileName);
    }

    [Fact]
    public void WordPress_LaApi_PrefiereLaOfertaUpgrade_YRechazaLoQueNoEsDeWordPressOrg()
    {
        var two = """{"offers":[{"response":"autoupdate","version":"7.0.7","download":"https://downloads.w.org/release/wordpress-7.0.7.zip"},{"response":"upgrade","version":"7.1.3","download":"https://downloads.wordpress.org/release/wordpress-7.1.3.zip"}]}""";
        Assert.Equal("7.1.3", WordPressCatalog.Parse(two).Version);

        Assert.Throws<RoostException>(() => WordPressCatalog.Parse("""{"offers":[{"response":"upgrade","version":"7.1.3","download":"http://downloads.wordpress.org/release/wordpress-7.1.3.zip"}]}"""));
        Assert.Throws<RoostException>(() => WordPressCatalog.Parse("""{"offers":[{"response":"upgrade","version":"7.1.3","download":"https://wordpress.org.example.com/wordpress-7.1.3.zip"}]}"""));
        Assert.Throws<RoostException>(() => WordPressCatalog.Parse("""{"offers":[{"response":"upgrade","version":"../x","download":"https://downloads.wordpress.org/x.zip"}]}"""));
        Assert.Throws<RoostException>(() => WordPressCatalog.Parse("""{"offers":[]}"""));
        Assert.Throws<RoostException>(() => WordPressCatalog.Parse("no es json"));
    }

    [Fact]
    public void WordPress_ElSha1DelPuntoSha1()
    {
        Assert.Equal("13eb1d042b1ca39224e44906cd84fe203112210c", WordPressCatalog.ParseSha1("13eb1d042b1ca39224e44906cd84fe203112210c"));
        Assert.Equal("13eb1d042b1ca39224e44906cd84fe203112210c", WordPressCatalog.ParseSha1("13EB1D042B1CA39224E44906CD84FE203112210C  wordpress-7.1.3.zip\n"));
        Assert.Throws<RoostException>(() => WordPressCatalog.ParseSha1("<html>404</html>"));
        Assert.True(WordPressCatalog.IsWordPressOrg(new Uri("https://downloads.w.org/release/wordpress-7.1.3.zip")));
        Assert.False(WordPressCatalog.IsWordPressOrg(new Uri("https://evil.example/wordpress.org.zip")));
    }

    // --- Lo que ProjectKind sabe ahora ---

    [Fact]
    public void ProjectKind_ElEnvLaClaveElEnlaceYLaVersionDelFramework()
    {
        using var root = new DeployRoot();
        var folder = root.Project("tienda", new Dictionary<string, string>
        {
            ["artisan"] = "", ["composer.json"] = """{ "require": { "php": "^8.3", "laravel/framework": "^13.0" } }""",
            [".env"] = "APP_KEY=base64:abc\n", [".env.example"] = "", ["public/storage/"] = "", ["package.json"] = "{}",
        });
        var kind = ProjectKind.Detect(folder);

        Assert.Equal("^8.3", kind.PhpConstraint);
        Assert.Equal("^13.0", kind.FrameworkConstraint);
        Assert.Equal("Laravel 13", kind.FrameworkLabel);
        Assert.True(kind.HasEnv);
        Assert.True(kind.HasEnvExample);
        Assert.True(kind.HasAppKey);
        Assert.True(kind.HasStorageLink);
        Assert.True(kind.HasPackageJson);
        Assert.Equal(["composer.json", "artisan"], kind.Evidence);

        File.WriteAllText(Path.Combine(folder, ".env"), "APP_KEY=\n");
        Assert.False(ProjectKind.Detect(folder).HasAppKey);
    }

    [Fact]
    public void ProjectKind_WordPress_ConSuEjemplo_YElWpConfigDeArriba()
    {
        using var root = new DeployRoot();
        var folder = root.Project("blog", "wp-load.php", "wp-config-sample.php");
        var kind = ProjectKind.Detect(folder);
        Assert.True(kind.HasWpConfigSample);
        Assert.False(kind.HasWpConfig);
        Assert.Equal("WordPress", kind.FrameworkLabel);

        // WordPress busca también uno más arriba (si esa carpeta no es otro WordPress).
        File.WriteAllText(Path.Combine(root.Paths.Www, "wp-config.php"), "<?php");
        Assert.True(ProjectKind.Detect(folder).HasWpConfig);
        File.WriteAllText(Path.Combine(root.Paths.Www, "wp-settings.php"), "<?php");
        Assert.False(ProjectKind.Detect(folder).HasWpConfig);
    }
}
