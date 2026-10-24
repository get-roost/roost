using System.Text.Json;

namespace Roost.Core.Install;

/// <summary>
/// Lo que dice el <c>BUILD.json</c> de un PHP de roost-builds (los de Linux): su versión, las
/// extensiones que van integradas en el binario (compilado estático con static-php-cli: no se
/// cargan con <c>extension=</c> ni se pueden quitar), las compartidas que trae en <c>ext/</c>
/// (Xdebug), y de dónde sale (static-php-cli, glibc mínima, SHA-256 de la fuente de php.net). Los
/// zips de Windows no lo traen: ahí <see cref="Read"/> da null y las extensiones son las DLL de <c>ext/</c>.
/// Se lee con <c>JsonDocument</c>, sin reflexión: también lo lee el shim (NativeAOT).
/// </summary>
/// <param name="Version">La de PHP: <c>8.4.27</c>.</param>
/// <param name="Extensions">Las integradas, en minúsculas (<c>curl</c>, <c>opcache</c>, <c>pdo_mysql</c>…).</param>
/// <param name="Shared">Las compartidas de <c>ext/</c> con su versión (<c>xdebug</c> → <c>3.5.3</c>).</param>
/// <param name="StaticPhp">La versión de static-php-cli con la que se compiló.</param>
/// <param name="Glibc">La glibc mínima con la que corre (<c>2.17</c>).</param>
/// <param name="SourceSha256">El SHA-256 del tar.xz de php.net del que sale, verificado al compilar.</param>
public sealed record PhpBuildInfo(
    string? Version,
    IReadOnlySet<string> Extensions,
    IReadOnlyDictionary<string, string> Shared,
    string? StaticPhp,
    string? Glibc,
    string? SourceSha256)
{
    public const string FileName = "BUILD.json";

    /// <summary>La versión de Xdebug que trae en <c>ext/xdebug.so</c>, o null si no lo trae.</summary>
    public string? Xdebug => Shared.TryGetValue("xdebug", out var v) ? v : null;

    /// <summary>El <c>BUILD.json</c> de una instalación; null si no lo tiene (Windows) o no se entiende. Nunca lanza.</summary>
    public static PhpBuildInfo? Read(string installDirectory)
    {
        try
        {
            var file = Path.Combine(installDirectory, FileName);
            return File.Exists(file) ? Parse(File.ReadAllText(file)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>PURO: parsea el JSON; null si no es el nuestro. Las extensiones salen en minúsculas.</summary>
    public static PhpBuildInfo? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("extensions", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var e in list.EnumerateArray())
                    if (e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 } name)
                        extensions.Add(name.Trim().ToLowerInvariant());

            var shared = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("shared", out var map) && map.ValueKind == JsonValueKind.Object)
                foreach (var p in map.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.String)
                        shared[p.Name.Trim().ToLowerInvariant()] = p.Value.GetString() ?? "";

            return new PhpBuildInfo(
                GetString(root, "php"),
                extensions,
                shared,
                root.TryGetProperty("static_php_cli", out var spc) && spc.ValueKind == JsonValueKind.Object ? GetString(spc, "version") : GetString(root, "static_php_cli"),
                GetString(root, "glibc"),
                root.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.Object ? GetString(source, "sha256") : null);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
