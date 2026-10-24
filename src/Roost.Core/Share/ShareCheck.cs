using System.Text.RegularExpressions;
using Roost.Core.Deploy;
using Roost.Core.Sites;

namespace Roost.Core.Share;

/// <summary>
/// Lo que conviene saber de un proyecto antes de compartirlo (<see cref="ShareCheck.Inspect"/>): de qué
/// tipo es, si tiene el modo de depuración puesto (con él, una página de error enseña rutas, la
/// configuración y a veces el <c>.env</c> a quien tenga el enlace) y la URL que tiene guardada.
/// </summary>
/// <param name="Debug">
/// Laravel con <c>APP_DEBUG=true</c>, Symfony en <c>APP_ENV=dev</c> (la barra y el profiler) o WordPress con
/// <c>WP_DEBUG</c> a true.
/// </param>
/// <param name="AppUrl">El <c>APP_URL</c> de Laravel, si lo tiene: sigue apuntando a <c>.test</c>.</param>
public sealed partial record ShareCheck(ProjectType Type, bool Debug, string? AppUrl)
{
    /// <summary>
    /// PURO sobre disco y nunca lanza: lee el <c>.env</c> (y el <c>.env.local</c> de Symfony, que manda)
    /// o el <c>wp-config.php</c> de la carpeta del sitio.
    /// </summary>
    public static ShareCheck Inspect(Site site)
    {
        try
        {
            var kind = ProjectKind.Detect(site.Folder);
            switch (kind.Type)
            {
                case ProjectType.Laravel:
                {
                    var env = Read(Path.Combine(site.Folder, ".env"));
                    return new ShareCheck(kind.Type, IsTrue(env?.Get("APP_DEBUG")), Blank(env?.Get("APP_URL")));
                }
                case ProjectType.Symfony:
                {
                    var appEnv = Read(Path.Combine(site.Folder, ".env.local"))?.Get("APP_ENV")
                                 ?? Read(Path.Combine(site.Folder, ".env"))?.Get("APP_ENV");
                    return new ShareCheck(kind.Type, string.Equals(appEnv?.Trim(), "dev", StringComparison.OrdinalIgnoreCase), null);
                }
                case ProjectType.WordPress:
                {
                    var config = Path.Combine(site.Folder, "wp-config.php");
                    var debug = File.Exists(config) && WpDebugOn().IsMatch(File.ReadAllText(config));
                    return new ShareCheck(kind.Type, debug, null);
                }
                default:
                    return new ShareCheck(kind.Type, false, null);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ShareCheck(ProjectType.Plain, false, null);
        }
    }

    private static EnvFile? Read(string path) => File.Exists(path) ? EnvFile.Load(path).Env : null;

    private static bool IsTrue(string? value) => value?.Trim().ToLowerInvariant() is "true" or "1" or "(true)" or "on" or "yes";

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [GeneratedRegex(@"define\s*\(\s*['""]WP_DEBUG['""]\s*,\s*(true|1)\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex WpDebugOn();
}
