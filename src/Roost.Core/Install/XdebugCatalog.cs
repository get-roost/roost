namespace Roost.Core.Install;

/// <summary>
/// La DLL de Xdebug de xdebug.org para una rama de PHP (NTS, x64), con el SHA-256 que tiene que dar.
/// Una DLL vale para todas las versiones de su rama: lo que la ata es la API de PHP, que no cambia
/// dentro de una rama (8.4.26 y 8.4.27 cargan la misma).
/// </summary>
/// <param name="Version">La de Xdebug: <c>3.5.3</c>.</param>
/// <param name="PhpBranch">La rama de PHP para la que está compilada: <c>8.4</c>.</param>
/// <param name="Compiler">El Visual Studio con el que está compilada: el mismo que el PHP de windows.php.net de esa rama.</param>
/// <param name="FileName">El nombre en xdebug.org, que dice todo eso (<c>php_xdebug-3.5.3-8.4-nts-vs17-x86_64.dll</c>).</param>
/// <param name="Size">Lo que ocupa, en bytes (para el progreso de la descarga).</param>
public sealed record XdebugBuild(string Version, string PhpBranch, string Compiler, string FileName, string Sha256, long Size)
{
    public Uri Url => new($"https://xdebug.org/files/{FileName}");

    public string Label => $"Xdebug {Version}";
}

/// <summary>
/// Xdebug fijado a mano, como <c>GitCatalog</c> con MinGit: una DLL por rama de PHP de las que
/// instala Roost (7.4 a 8.5), NTS y x64, que es lo que sirve Roost. No se lee xdebug.org en cada
/// instalación porque la página solo dice qué hay, no qué bytes son los buenos: el SHA-256 va en el
/// binario de Roost, comprobado al fijarlo. La 7.4 se quedó en Xdebug 3.1 (la 3.2 ya pide PHP 8.0);
/// las demás van con la última.
/// </summary>
/// <remarks>
/// Para cambiar de versión:
/// <list type="number">
/// <item>mirar en <c>https://xdebug.org/docs/compat</c> qué ramas de PHP soporta y en
/// <c>https://xdebug.org/download</c> la última, con sus DLL de Windows: «NTS», «64 bit», una por
/// rama de PHP, con el VS de windows.php.net (vc15 para 7.4, vs16 de 8.0 a 8.3, vs17 para 8.4 y 8.5;
/// lo dice el <c>releases.json</c> de windows.php.net);</item>
/// <item>leer el SHA-256 de cada una del HTML de la página, que lo pone en el <c>title</c> del enlace:
/// <c>curl -s https://xdebug.org/download | grep -o "title=.SHA256[^>]*nts[^>]*"</c>;</item>
/// <item>bajar cada DLL a una carpeta vacía (<c>https://xdebug.org/files/&lt;fichero&gt;</c>) y
/// comprobar que <c>sha256sum</c> da lo mismo que el publicado;</item>
/// <item>fijar aquí la versión, el fichero, el SHA-256 y el tamaño, y probarlo en real con
/// <c>roost php xdebug on &lt;rama&gt;</c>: <c>php -v</c> tiene que decir «with Xdebug v&lt;versión&gt;».</item>
/// </list>
/// Xdebug no firma las DLL (ni PGP ni Authenticode): lo que hay es el HTTPS de xdebug.org y su
/// SHA-256, que por eso se comprueba con el fichero bajado. Xdebug saca arreglos: hay que subirlo
/// con Roost. La DLL de una versión vieja que ya esté instalada se sigue usando hasta que se vuelva
/// a activar (<see cref="XdebugInstaller.Locate"/>).
/// </remarks>
public static class XdebugCatalog
{
    /// <summary>El de este sistema (hoy, el de Windows; el de Linux llega con el plan de Linux).</summary>
    public static IReadOnlyList<XdebugBuild> Current => Windows;

    // Verificado el 24-oct-2026: el SHA-256 del title de xdebug.org/download (y /download/historical
    // para la 3.1.6) y el del fichero bajado a una carpeta vacía coinciden en las siete. Xdebug 3.5.3
    // es del 8-jun-2026; la 3.1.6 es la última que soporta PHP 7.4.
    public static readonly IReadOnlyList<XdebugBuild> Windows =
    [
        new("3.1.6", "7.4", "vc15", "php_xdebug-3.1.6-7.4-vc15-nts-x86_64.dll", "704a27985094e71803ab5dd2e75099d860d9fdb1de623013f567219c3d780721", 283_648),
        new("3.5.3", "8.0", "vs16", "php_xdebug-3.5.3-8.0-nts-vs16-x86_64.dll", "a3ffd4d5b1f9ca0766b1f3ed321246808a51ce4ba49dc32ae195bfafc4d4d9a2", 327_680),
        new("3.5.3", "8.1", "vs16", "php_xdebug-3.5.3-8.1-nts-vs16-x86_64.dll", "15bb3abcd0755af8378550793bcf7603636add31dc4a04d24bfd2a1f764399b4", 331_264),
        new("3.5.3", "8.2", "vs16", "php_xdebug-3.5.3-8.2-nts-vs16-x86_64.dll", "b449eda7eeefc80c651fac334582c5f0877cd72cfe55fc1c5f0f98bf6a4edb94", 330_240),
        new("3.5.3", "8.3", "vs16", "php_xdebug-3.5.3-8.3-nts-vs16-x86_64.dll", "4c80487592484c36d21a7006a53738dfc07884fc6867720ddc5c11559371cdce", 330_240),
        new("3.5.3", "8.4", "vs17", "php_xdebug-3.5.3-8.4-nts-vs17-x86_64.dll", "a94d4fcfe4086a300b2decaf7c04e59f4981aa5e56992d48b55c5e49dfd9b9ee", 331_776),
        new("3.5.3", "8.5", "vs17", "php_xdebug-3.5.3-8.5-nts-vs17-x86_64.dll", "0a0d52bf276dad7e5f90ac30a5110b3119d291b20c118be0cb7f5752064f1811", 331_776),
    ];

    /// <summary>La DLL de una instalación, o null si Roost no tiene ninguna para ella (TS, x86 o una rama sin DLL).</summary>
    public static XdebugBuild? For(InstalledPhp php) =>
        WhyNot(php) is null ? Current.FirstOrDefault(b => b.PhpBranch == BranchOf(php)) : null;

    /// <summary>Por qué Roost no puede poner Xdebug en una instalación, o null si puede.</summary>
    public static string? WhyNot(InstalledPhp php)
    {
        if (php.ThreadSafe)
            return $"{php.Name} es TS: Roost pone Xdebug en las NTS, que son las que sirve (php-cgi). Instala la NTS de esa version.";
        if (!php.Name.EndsWith("-x64", StringComparison.OrdinalIgnoreCase))
            return $"{php.Name} no es de 64 bits: Roost pone Xdebug en las NTS x64.";
        var branch = BranchOf(php);
        if (branch is null || !Current.Any(b => b.PhpBranch == branch))
            return $"Roost no tiene Xdebug para PHP {branch ?? php.Name}: lo tiene de la {Current[0].PhpBranch} a la {Current[^1].PhpBranch}.";
        return null;
    }

    private static string? BranchOf(InstalledPhp php) => php.Version is { } v ? $"{v.Major}.{v.Minor}" : null;
}
