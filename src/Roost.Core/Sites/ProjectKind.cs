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
/// Laravel: raíz web public/») y el panel del sitio; los pasos tras clonar (git al estilo Plesk)
/// partirán de aquí.
/// </summary>
/// <param name="WebRoot">La raíz web que se propone, relativa a la carpeta (<c>public</c> si existe); null = la carpeta.</param>
public sealed record ProjectKind(ProjectType Type, string? WebRoot, bool HasComposerJson, bool HasComposerLock, bool HasVendor)
{
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
            return new ProjectKind(type, root, hasComposer, File("composer.lock"), Dir("vendor"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new ProjectKind(ProjectType.Plain, null, false, false, false);
        }
    }
}
