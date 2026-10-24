using Roost.Core.Models;

namespace Roost.Core.Composer;

/// <summary>Un <c>composer.phar</c> con el SHA-256 que tiene que dar y el PHP más viejo con el que funciona.</summary>
public sealed record ComposerBuild(string Version, Uri Url, string Sha256, ComponentVersion MinPhp)
{
    /// <summary>El nombre en la caché de descargas: con la versión, para que dos no se pisen.</summary>
    public string FileName => $"composer-{Version}.phar";

    public string Label => $"Composer {Version}";
}

/// <summary>
/// Composer fijado a mano, como <c>GitCatalog</c> con MinGit: la última 2.x estable de
/// getcomposer.org con su SHA-256. No se lee <c>getcomposer.org/versions</c> en cada instalación
/// porque eso solo dice qué versión es la última, no qué bytes son los buenos: el SHA-256 va en el
/// binario de Roost, comprobado al fijarlo.
/// </summary>
/// <remarks>
/// Para cambiar de versión: mirar la última estable en <c>https://getcomposer.org/versions</c>
/// (<c>stable[0]</c>, con su <c>min-php</c>); bajar a una carpeta vacía
/// <c>https://getcomposer.org/download/&lt;versión&gt;/composer.phar</c>, su
/// <c>composer.phar.sha256sum</c> y su <c>composer.phar.asc</c>; comprobar que el
/// <c>sha256sum</c> del fichero bajado da lo mismo que el publicado; verificar la firma con
/// <c>gpg --verify composer.phar.asc composer.phar</c> contra la clave de Packagist
/// (<c>161D FBE3 4288 9F01 DDAC 4E61 CBB3 D576 F2A0 946F</c>, de keys.openpgp.org: es la que usa el
/// <c>release.yml</c> de composer/composer), y fijar aquí la versión, la URL, el SHA-256 y el PHP
/// mínimo. Composer saca parches de seguridad: hay que subirlo con Roost.
/// </remarks>
public static class ComposerCatalog
{
    // composer.phar 2.10.3 (3.642.137 bytes), verificado el 24-oct-2026: el composer.phar.sha256sum
    // publicado y el SHA-256 del fichero descargado coinciden, y la firma .asc es buena con la clave
    // 161DFBE342889F01DDAC4E61CBB3D576F2A0946F («Packagist Conductors»). Pide PHP 7.2.5 o más nuevo
    // (min-php 70205); Roost instala de la 7.4 en adelante.
    public static readonly ComposerBuild Stable = new(
        "2.10.3",
        new Uri("https://getcomposer.org/download/2.10.3/composer.phar"),
        "7a2d379d5b8ffdaa028580ef26494c36d2feef4b178d3dd1473a4dbc5e17c8d6",
        new ComponentVersion(7, 2, 5, "7.2.5"));
}
