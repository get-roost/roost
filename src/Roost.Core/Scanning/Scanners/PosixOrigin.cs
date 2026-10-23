namespace Roost.Core.Scanning.Scanners;

/// <summary>
/// El origen de un binario de Linux o macOS según dónde está (puro). Solo es <c>system</c> (y
/// por tanto ámbar honesto en la auditoría, porque puede llevar parches de backport) lo que
/// pone ahí el gestor de paquetes de la distro: <c>/usr/bin</c>, <c>/usr/sbin</c>, <c>/bin</c>,
/// <c>/sbin</c> y los repos de <c>/opt/remi</c>. Lo de <c>/usr/local</c> y <c>/opt</c> está compilado
/// a mano o es un binario oficial (las imágenes Docker <c>php:*</c> instalan en
/// <c>/usr/local/bin</c>): es la versión de upstream y se evalúa con certeza.
/// </summary>
public static class PosixOrigin
{
    public const string System = "system";
    public const string UsrLocal = "/usr/local";
    public const string Opt = "/opt";
    public const string Homebrew = "Homebrew";

    public static string Of(string path)
    {
        if (path.StartsWith("/opt/homebrew/", StringComparison.Ordinal)) return Homebrew;
        if (path.StartsWith("/opt/remi/", StringComparison.Ordinal)) return System;
        if (path.StartsWith("/usr/local/", StringComparison.Ordinal)) return UsrLocal;
        if (path.StartsWith("/opt/", StringComparison.Ordinal)) return Opt;
        return System;
    }
}
