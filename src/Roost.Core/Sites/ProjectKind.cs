namespace Roost.Core.Sites;

/// <summary>Qué tipo de proyecto hay en una carpeta, por sus ficheros.</summary>
public enum ProjectType
{
    /// <summary>Nada que reconocer: una carpeta con PHP (o vacía).</summary>
    Plain,

    /// <summary>Tiene <c>composer.json</c>, pero no es ninguno de los de abajo.</summary>
    Composer,

    /// <summary>Laravel: el <c>artisan</c> de la raíz.</summary>
    Laravel,

    /// <summary>Symfony (con Flex): <c>bin/console</c> y <c>symfony.lock</c>.</summary>
    Symfony,

    /// <summary>WordPress: <c>wp-config.php</c> o <c>wp-load.php</c>.</summary>
    WordPress,
}

/// <summary>
/// Lo que se sabe de un proyecto mirando sus ficheros (puro sobre disco y nunca lanza): el tipo, la
/// raíz web que le toca y lo que tiene de Composer. Lo usan Nuevo sitio (para decir «Proyecto
/// Laravel: raíz web public/»), el panel del sitio y los pasos de instalación (<c>Deploy/DeployPlan</c>),
/// que miran además lo de abajo: el <c>.env</c>, su ejemplo, la clave, el <c>wp-config.php</c>…
/// </summary>
/// <param name="WebRoot">La raíz web que se propone, relativa a la carpeta (<c>public</c> si existe); null = la carpeta.</param>
public sealed record ProjectKind(ProjectType Type, string? WebRoot, bool HasComposerJson, bool HasComposerLock, bool HasVendor)
{
    /// <summary>El <c>require.php</c> del composer.json («^8.2»), o null.</summary>
    public string? PhpConstraint { get; init; }

    /// <summary>
    /// La versión del framework que pide el composer.json (<c>laravel/framework</c>,
    /// <c>symfony/framework-bundle</c>), tal cual: «^13.0», «8.1.*». null si no se sabe.
    /// </summary>
    public string? FrameworkConstraint { get; init; }

    /// <summary>Tiene <c>.env</c> (Laravel, Symfony).</summary>
    public bool HasEnv { get; init; }

    /// <summary>Tiene <c>.env.example</c>, el que trae Laravel para copiarlo a <c>.env</c>.</summary>
    public bool HasEnvExample { get; init; }

    /// <summary>Symfony: tiene <c>.env.local</c>, la parte del <c>.env</c> que no va a git.</summary>
    public bool HasEnvLocal { get; init; }

    /// <summary>Laravel: el <c>.env</c> ya tiene <c>APP_KEY</c> con algo.</summary>
    public bool HasAppKey { get; init; }

    /// <summary>Laravel: ya tiene <c>public/storage</c> (el enlace de <c>storage:link</c>).</summary>
    public bool HasStorageLink { get; init; }

    /// <summary>Symfony: usa Doctrine (<c>doctrine/doctrine-bundle</c>, <c>doctrine/orm</c> o <c>doctrine/dbal</c>), así que quiere una base de datos.</summary>
    public bool HasDoctrine { get; init; }

    /// <summary>Symfony: usa las migraciones de Doctrine (<c>doctrine/doctrine-migrations-bundle</c>).</summary>
    public bool HasDoctrineMigrations { get; init; }

    /// <summary>WordPress: tiene <c>wp-config.php</c> (en su carpeta o en la de arriba, como lo busca WordPress).</summary>
    public bool HasWpConfig { get; init; }

    /// <summary>WordPress: tiene <c>wp-config-sample.php</c>, del que sale el <c>wp-config.php</c>.</summary>
    public bool HasWpConfigSample { get; init; }

    /// <summary>Tiene <c>package.json</c> (Vite, Mix…): <c>npm install</c> llega con Node en Roost.</summary>
    public bool HasPackageJson { get; init; }

    /// <summary>Los ficheros por los que se ha reconocido («composer.json, artisan»), para contarlo.</summary>
    public IReadOnlyList<string> Evidence { get; init; } = [];

    /// <summary>
    /// La versión mayor (o mayor.menor) del framework, para enseñarla («Laravel 13», «Symfony 8.1»):
    /// la primera que aparece en <see cref="FrameworkConstraint"/>. null si no se sabe.
    /// </summary>
    public string? FrameworkVersion =>
        FrameworkConstraint is { } c && System.Text.RegularExpressions.Regex.Match(c, @"(\d+)(?:\.(\d+))?") is { Success: true } m
            ? Type == ProjectType.Symfony && m.Groups[2].Success ? $"{m.Groups[1].Value}.{m.Groups[2].Value}" : m.Groups[1].Value
            : null;

    /// <summary>«Laravel 13», «Symfony 8.1», «WordPress» o null si no es ninguno de los conocidos.</summary>
    public string? FrameworkLabel => Framework is { } f ? (FrameworkVersion is { } v ? $"{f} {v}" : f) : null;

    /// <summary>El nombre del framework para enseñarlo, o null si no es ninguno de los conocidos.</summary>
    public string? Framework => Type switch
    {
        ProjectType.Laravel => "Laravel",
        ProjectType.Symfony => "Symfony",
        ProjectType.WordPress => "WordPress",
        _ => null,
    };

    /// <summary>Tiene <c>composer.json</c> y todavía no <c>vendor/</c>: hace falta un <c>composer install</c>.</summary>
    public bool NeedsComposerInstall => HasComposerJson && !HasVendor;

    public static ProjectKind Detect(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return new ProjectKind(ProjectType.Plain, null, false, false, false);

            bool File(string relative) => System.IO.File.Exists(Path.Combine(folder, relative));
            bool Dir(string relative) => Directory.Exists(Path.Combine(folder, relative));

            var hasComposer = File("composer.json");
            var type = File("artisan") ? ProjectType.Laravel
                : File(Path.Combine("bin", "console")) && File("symfony.lock") ? ProjectType.Symfony
                : File("wp-config.php") || File("wp-load.php") ? ProjectType.WordPress
                : hasComposer ? ProjectType.Composer
                : ProjectType.Plain;

            // WordPress se sirve desde su propia carpeta; los demás, desde public/ si la tienen.
            var root = type != ProjectType.WordPress && Dir("public") ? "public" : null;
            var composer = hasComposer ? ReadComposer(Path.Combine(folder, "composer.json")) : default;
            var evidence = new List<string>();
            if (hasComposer) evidence.Add("composer.json");
            switch (type)
            {
                case ProjectType.Laravel: evidence.Add("artisan"); break;
                case ProjectType.Symfony: evidence.AddRange(["bin/console", "symfony.lock"]); break;
                case ProjectType.WordPress: evidence.Add(File("wp-load.php") ? "wp-load.php" : "wp-config.php"); break;
            }

            return new ProjectKind(type, root, hasComposer, File("composer.lock"), Dir("vendor"))
            {
                PhpConstraint = composer.Php,
                FrameworkConstraint = type switch
                {
                    ProjectType.Laravel => composer.Laravel,
                    ProjectType.Symfony => composer.Symfony,
                    _ => null,
                },
                HasEnv = File(".env"),
                HasEnvExample = File(".env.example"),
                HasEnvLocal = File(".env.local"),
                HasAppKey = type == ProjectType.Laravel && HasValue(Path.Combine(folder, ".env"), "APP_KEY"),
                // Un enlace roto también cuenta como hecho: storage:link fallaría igual («already exists»).
                HasStorageLink = Dir(Path.Combine("public", "storage")) || File(Path.Combine("public", "storage")),
                HasDoctrine = composer.Doctrine || composer.DoctrineMigrations,
                HasDoctrineMigrations = composer.DoctrineMigrations,
                // WordPress también lo busca una carpeta más arriba, si esa no es otro WordPress.
                HasWpConfig = type == ProjectType.WordPress
                              && (File("wp-config.php") || File(Path.Combine("..", "wp-config.php")) && !File(Path.Combine("..", "wp-settings.php"))),
                HasWpConfigSample = File("wp-config-sample.php"),
                HasPackageJson = File("package.json"),
                Evidence = evidence,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new ProjectKind(ProjectType.Plain, null, false, false, false);
        }
    }

    /// <summary>Lo que interesa del composer.json: el PHP, la versión del framework y si usa las migraciones de Doctrine.</summary>
    private readonly record struct ComposerFacts(string? Php, string? Laravel, string? Symfony, bool Doctrine, bool DoctrineMigrations);

    private static ComposerFacts ReadComposer(string file)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(file));
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("require", out var require)
                || require.ValueKind != System.Text.Json.JsonValueKind.Object)
                return default;

            string? Get(string name) =>
                require.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;

            // Symfony con Flex fija la versión en extra.symfony.require («8.1.*»); si no, la del framework-bundle.
            string? symfony = null;
            if (doc.RootElement.TryGetProperty("extra", out var extra) && extra.ValueKind == System.Text.Json.JsonValueKind.Object
                && extra.TryGetProperty("symfony", out var sf) && sf.ValueKind == System.Text.Json.JsonValueKind.Object
                && sf.TryGetProperty("require", out var sfRequire) && sfRequire.ValueKind == System.Text.Json.JsonValueKind.String)
                symfony = sfRequire.GetString();

            return new ComposerFacts(Get("php"), Get("laravel/framework"), symfony ?? Get("symfony/framework-bundle"),
                Get("doctrine/doctrine-bundle") is not null || Get("doctrine/orm") is not null || Get("doctrine/dbal") is not null || Get("symfony/orm-pack") is not null,
                Get("doctrine/doctrine-migrations-bundle") is not null);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            return default;
        }
    }

    /// <summary>true si el .env tiene esa clave con algo (no vacía).</summary>
    private static bool HasValue(string envFile, string key)
    {
        try
        {
            return System.IO.File.Exists(envFile) && !string.IsNullOrEmpty(Deploy.EnvFile.Parse(System.IO.File.ReadAllText(envFile)).Get(key));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
