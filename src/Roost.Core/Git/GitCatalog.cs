namespace Roost.Core.Git;

/// <summary>Un zip de MinGit con el SHA-256 que tiene que dar.</summary>
/// <param name="Version">La versión de Git for Windows con su número de build: <c>2.55.0.3</c> es «2.55.0(3)».</param>
public sealed record GitBuild(string Version, Uri Url, string Sha256)
{
    public string FileName => $"MinGit-{Version}-64-bit.zip";

    /// <summary>Lo que dice <c>git --version</c> de este build (<c>git version 2.55.0.windows.3</c>).</summary>
    public string Label => $"MinGit {Version}";
}

/// <summary>
/// git fijado a mano, como <c>NginxCatalog</c>: MinGit, la variante de Git for Windows pensada para
/// que la lleven otras aplicaciones (sin Bash, sin Perl, sin GCM), x64. El SHA-256 es el <c>digest</c>
/// que publica la API de GitHub para ese asset de la release de <c>git-for-windows/git</c>, y se
/// comprobó además con el zip descargado. Para cambiar de versión: la release
/// <c>v&lt;versión&gt;.windows.&lt;build&gt;</c>, el asset <c>MinGit-&lt;versión&gt;.&lt;build&gt;-64-bit.zip</c> (no el
/// «busybox», que sigue marcado como experimental), y su SHA-256 de la tabla de assets, después de
/// bajarlo y comprobar que lo da. git saca parches de seguridad: hay que subirlo con Roost.
/// </summary>
public static class GitCatalog
{
    // MinGit-2.55.0.3-64-bit.zip, release v2.55.0.windows.3 (Git for Windows 2.55.0(3)), verificado el 24-oct-2026:
    // el digest de la API de GitHub y el SHA-256 del zip descargado coinciden.
    public static readonly GitBuild Windows = new(
        "2.55.0.3",
        new Uri("https://github.com/git-for-windows/git/releases/download/v2.55.0.windows.3/MinGit-2.55.0.3-64-bit.zip"),
        "f48e2d2dc74a24454adc6d8fd0ac25bf9c2386f19cfb06202b9465aaad4f9f05");
}
