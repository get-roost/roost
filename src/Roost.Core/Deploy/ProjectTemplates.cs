using Roost.Core.Config;
using Roost.Core.Install;
using Roost.Core.Models;
using Roost.Core.Node;
using Roost.Core.Platform;
using Roost.Core.Scanning;
using Roost.Core.Sites;
using Roost.Core.Stack;

namespace Roost.Core.Deploy;

/// <summary>Las plantillas de proyecto de Nuevo sitio y <c>roost new</c> (ROOST.md: C12).</summary>
public enum ProjectTemplate
{
    Laravel,
    Symfony,
    WordPress,
}

/// <summary>
/// Una plantilla: de dónde sale el proyecto y el PHP que pide su versión actual.
/// </summary>
/// <param name="Key">Como se escribe en <c>roost new &lt;plantilla&gt;</c>.</param>
/// <param name="Package">El paquete de <c>composer create-project</c>; null en WordPress (zip de wordpress.org).</param>
/// <param name="MinPhp">El PHP más viejo que vale para la versión actual (comprobado en Packagist).</param>
/// <param name="Version">Lo que sale hoy, para contarlo («Laravel 13»).</param>
public sealed record TemplateInfo(ProjectTemplate Template, string Key, string Name, string? Package, ComponentVersion MinPhp, string Version)
{
    public bool UsesComposer => Package is not null;

    /// <summary>
    /// El proyecto como queda justo después de crearlo (para proponer los pasos antes de que exista).
    /// Laravel sale con su .env, su APP_KEY y su sqlite de los scripts de create-project; la clave se
    /// propone igual, y si ya está se salta.
    /// </summary>
    public ProjectKind Expected(bool webapp) => Template switch
    {
        ProjectTemplate.Laravel => new ProjectKind(ProjectType.Laravel, "public", true, true, true)
        {
            HasEnv = true,
            HasEnvExample = true,
            HasPackageJson = true,
            Evidence = ["composer.json", "artisan"],
        },
        ProjectTemplate.Symfony => new ProjectKind(ProjectType.Symfony, "public", true, true, true)
        {
            HasEnv = true,
            HasDoctrine = webapp,
            HasDoctrineMigrations = webapp,
            Evidence = ["composer.json", "bin/console", "symfony.lock"],
        },
        _ => new ProjectKind(ProjectType.WordPress, null, false, false, false)
        {
            HasWpConfigSample = true,
            Evidence = ["wp-load.php"],
        },
    };
}

/// <summary>Lo que se pide para crear un proyecto con una plantilla. null: lo decide Roost (<c>www/&lt;nombre&gt;</c>, el PHP global).</summary>
/// <param name="Database">La base de datos del sitio (se crea si no existe); null = ninguna.</param>
/// <param name="Webapp">Symfony: también <c>composer require webapp</c> (Twig, Doctrine, formularios, seguridad…).</param>
public sealed record NewProjectRequest(ProjectTemplate Template, string Name, string? Folder = null, string? Php = null, string? Database = null, bool Webapp = false);

/// <summary>Lo que se va a hacer, resuelto y validado antes de tocar nada.</summary>
/// <param name="PhpSpec">El PHP del sitio tal como se guarda (null = el global).</param>
/// <param name="FolderExists">La carpeta ya existía (vacía): si no se acaba, se deja vacía en vez de borrarla.</param>
public sealed record NewProjectPlan(TemplateInfo Template, string Name, string Folder, InstalledPhp Php, string? PhpSpec, string? Database, bool Webapp, bool FolderExists);

/// <summary>
/// Las plantillas: Laravel (<c>composer create-project laravel/laravel</c>), Symfony
/// (<c>symfony/skeleton</c> y, si se pide, <c>webapp</c>) y WordPress (el zip de wordpress.org,
/// comprobado con su SHA-1). Después vienen los mismos pasos que tras clonar (<see cref="DeployPlan"/>):
/// la base de datos, el .env o el wp-config.php, las migraciones. Lo hace <see cref="DeployRunner"/>.
/// </summary>
public static class ProjectTemplates
{
    // El PHP de cada una se comprobó en Packagist el 24-oct-2026: laravel/laravel 13.11 pide ^8.3;
    // symfony/skeleton 8.1 pide >=8.4 y la 7.4 (LTS) >=8.2 (Composer elige la que vale con el PHP
    // con que se ejecuta). WordPress 7.1 pide PHP 7.4 (lo dice la API al crearlo).
    public static readonly TemplateInfo Laravel = new(ProjectTemplate.Laravel, "laravel", "Laravel", "laravel/laravel", new ComponentVersion(8, 3, 0, "8.3.0"), "Laravel 13");

    public static readonly TemplateInfo Symfony = new(ProjectTemplate.Symfony, "symfony", "Symfony", "symfony/skeleton", new ComponentVersion(8, 2, 0, "8.2.0"), "Symfony 8.1 con PHP 8.4 o mas nuevo; 7.4 (LTS) con 8.2 y 8.3");

    public static readonly TemplateInfo WordPress = new(ProjectTemplate.WordPress, "wordpress", "WordPress", null, new ComponentVersion(7, 4, 0, "7.4.0"), "la ultima de wordpress.org");

    public static IReadOnlyList<TemplateInfo> All { get; } = [Laravel, WordPress, Symfony];

    public static TemplateInfo Get(ProjectTemplate template) => All.First(t => t.Template == template);

    /// <summary>La plantilla por su nombre (<c>laravel</c>, <c>wordpress</c>, <c>symfony</c>), o null.</summary>
    public static TemplateInfo? Find(string key) => All.FirstOrDefault(t => t.Key.Equals(key.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Los PHP instalados (NTS, como los que se ofrecen en todas partes) que valen para la plantilla, del más nuevo al más viejo.</summary>
    public static IReadOnlyList<InstalledPhp> SuitablePhp(TemplateInfo template, IReadOnlyList<InstalledPhp> installed) =>
        installed.Where(p => !p.ThreadSafe && p.Version is { } v && v >= template.MinPhp).OrderByDescending(p => p.Version).ToList();

    /// <summary>
    /// Valida lo pedido sin tocar nada: el nombre (válido y libre), la carpeta (nueva o vacía), el PHP
    /// (instalado y de los que valen) y la base de datos. Lanza <see cref="RoostException"/> si algo no cuadra.
    /// </summary>
    public static NewProjectPlan Plan(RoostPaths paths, NewProjectRequest request)
    {
        var template = Get(request.Template);
        var name = request.Name.Trim().ToLowerInvariant();
        if (name.EndsWith(".test", StringComparison.Ordinal)) name = name[..^".test".Length];
        if (!Site.IsValidName(name))
            throw new RoostException($"'{name}' no vale como nombre de sitio: usa minusculas, numeros y guiones (por ejemplo tienda o mi-blog).");
        if (new SiteRegistry(paths).Find(name) is { } existing)
            throw new RoostException($"Ya hay un sitio '{name}' ({existing.Folder}). Elige otro nombre o quitalo antes: roost site remove {name}");

        var folder = Path.TrimEndingDirectorySeparator(request.Folder is { } f ? Path.GetFullPath(f, paths.Www) : Path.Combine(paths.Www, name));
        if (File.Exists(folder))
            throw new RoostException($"{folder} es un fichero, no una carpeta.");
        var exists = Directory.Exists(folder);
        if (exists && Directory.EnumerateFileSystemEntries(folder).Any())
            throw new RoostException($"{folder} ya existe y no esta vacia: una plantilla necesita una carpeta nueva (o vacia). Elige otra con --path, o registra ese proyecto con roost site add.");

        var installed = new PhpInstaller(paths).ListInstalled();
        if (installed.Count == 0)
            throw new RoostException("No hay ningun PHP instalado por Roost. Instala uno: roost php install 8.4");
        var spec = request.Php is { } p && !p.Equals("global", StringComparison.OrdinalIgnoreCase) ? p.Trim() : null;
        InstalledPhp php;
        if (spec is null)
        {
            var config = DeployTarget.LoadConfig(paths);
            php = new StackPlanner(paths, ProcessRunner.Default).ResolvePhp(config, installed);
        }
        else
        {
            var candidates = PhpInstaller.Match(installed, spec);
            if (candidates.Count == 0)
                throw new RoostException($"No hay ningun PHP de Roost que case con '{spec}'. Instalalo: roost php install {spec}");
            php = PhpInstaller.Best(candidates);
        }
        if (php.Version is { } version && version < template.MinPhp)
            throw new RoostException(
                $"{template.Version.Split(';')[0]} pide PHP {template.MinPhp.Major}.{template.MinPhp.Minor} o mas nuevo, y {(spec is null ? "el global" : spec)} es {php.Name}. "
                + $"Elige otro con --php{(SuitablePhp(template, installed) is [var best, ..] ? $" (tienes {best.Version})" : $", o instala uno: roost php install 8.4")}.");

        var database = request.Database?.Trim();
        if (database is { Length: 0 }) database = null;
        if (database is not null && (!DbAdmin.IsValidName(database) || DbAdmin.SystemSchemas.Contains(database)))
            throw new RoostException($"'{database}' no vale como base de datos de un sitio: usa solo letras, numeros y _ (hasta 64), y ninguna del sistema.");
        if (request.Webapp && template.Template != ProjectTemplate.Symfony)
            throw new RoostException("webapp es de Symfony: con las otras plantillas no vale.");

        return new NewProjectPlan(template, name, folder, php, spec, database, request.Webapp, exists);
    }

    /// <summary>
    /// Dónde y con qué: el sitio que va a ser (aún sin registrar), su URL, el PHP elegido y la base de
    /// datos global (si se pidió una). Lo del stack (Mailpit, Redis) sale de roost.toml.
    /// </summary>
    public static DeployTarget Target(RoostPaths paths, NewProjectPlan plan, bool dbRunning, int? dbPort = null)
    {
        var config = DeployTarget.LoadConfig(paths);
        var site = new Site { Name = plan.Name, Folder = plan.Folder };
        // El Node de la carpeta que va a ser (aún no existe: un .nvmrc de más arriba, o el global).
        var node = TerminalNode.TryResolve(paths, plan.Folder, out var nodeProblem);
        return new DeployTarget
        {
            SiteName = plan.Name,
            Folder = plan.Folder,
            Url = StackPlanner.SiteUrl(site, config.HttpsPort).TrimEnd('/'),
            Php = plan.Php,
            PhpReason = plan.PhpSpec is null ? "el PHP global" : $"el elegido ({plan.PhpSpec})",
            Database = DeployTarget.DatabaseFor(paths, config, plan.Database, dbRunning, dbPort),
            MailSmtpPort = config.MailEnabled ? config.MailSmtpPort : null,
            RedisPort = config.RedisEnabled ? config.RedisPort : null,
            Fresh = true,
            Node = node,
            NodeProblem = nodeProblem,
            NodeMissing = node is null ? TerminalNode.MissingSpec(paths, plan.Folder) : null,
        };
    }

    /// <summary>
    /// Los pasos de la plantilla: crear el proyecto (que no se puede desmarcar) y, después, los de
    /// <see cref="DeployPlan.For"/> para el proyecto tal como queda (la base de datos, el .env…).
    /// </summary>
    public static IReadOnlyList<DeployStep> Steps(NewProjectPlan plan, DeployTarget target)
    {
        var template = plan.Template;
        var create = template.Template switch
        {
            ProjectTemplate.Laravel => new DeployStep
            {
                Id = "create",
                Kind = DeployStepKind.CreateProject,
                Label = "composer create-project laravel/laravel",
                Command = "composer create-project laravel/laravel",
                Hint = $"{template.Version} en {plan.Folder}, con PHP {plan.Php.Version}. Los paquetes vienen de Packagist.",
            },
            ProjectTemplate.Symfony => new DeployStep
            {
                Id = "create",
                Kind = DeployStepKind.CreateProject,
                Label = plan.Webapp ? "composer create-project symfony/skeleton y composer require webapp" : "composer create-project symfony/skeleton",
                Command = "composer create-project symfony/skeleton",
                Hint = $"{(plan.Php.Version is { Major: >= 8 } v && (v.Major > 8 || v.Minor >= 4) ? "Symfony 8.1" : "Symfony 7.4 (LTS)")} en {plan.Folder}, con PHP {plan.Php.Version}"
                       + (plan.Webapp ? "; con webapp (Twig, Doctrine, formularios, seguridad)." : "."),
            },
            _ => new DeployStep
            {
                Id = "create",
                Kind = DeployStepKind.CreateProject,
                Label = "Descargar WordPress",
                Hint = $"La ultima version de wordpress.org en {plan.Folder}, comprobada con su SHA-1 antes de descomprimir.",
            },
        };
        create = create with { Checked = true, Required = true, RunsProjectCode = template.UsesComposer };
        return [create, .. DeployPlan.For(template.Expected(plan.Webapp), target)];
    }
}
