using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Roost.Core.Platform;

namespace Roost.Core.Sites;

/// <summary>
/// Un sitio: <c>https://&lt;nombre&gt;.test/</c> servido desde la carpeta de un proyecto
/// (ROOST.md §4.2, §5.6). Puede fijar su propio PHP; si no, usa el global.
/// </summary>
public sealed partial record Site
{
    public required string Name { get; init; }

    /// <summary>Carpeta del proyecto, absoluta.</summary>
    public required string Folder { get; init; }

    /// <summary>Raíz web relativa a la carpeta ("public" en Laravel o Symfony); null = la carpeta.</summary>
    public string? Root { get; init; }

    /// <summary>PHP del sitio: rama, versión o instalación; null = el PHP global.</summary>
    public string? Php { get; init; }

    public string Host => $"{Name}.test";

    public string DocumentRoot => Root is null ? Folder : Path.GetFullPath(Path.Combine(Folder, Root));

    // Una etiqueta DNS: minúsculas, números y guiones, sin guion al principio ni al final.
    [GeneratedRegex("^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$")]
    private static partial Regex NamePattern();

    public static bool IsValidName(string name) => NamePattern().IsMatch(name);
}

/// <summary>Los sitios registrados, en <c>state/sites.json</c>. Lo escribe Roost; no es configuración del usuario.</summary>
public sealed class SiteRegistry(RoostPaths paths)
{
    public string FilePath => Path.Combine(paths.State, "sites.json");

    /// <summary>Los sitios, por nombre.</summary>
    public IReadOnlyList<Site> Load()
    {
        if (!File.Exists(FilePath)) return [];
        try
        {
            var sites = JsonSerializer.Deserialize(File.ReadAllText(FilePath), SiteJson.Default.ListSite) ?? [];
            return sites.OrderBy(s => s.Name, StringComparer.Ordinal).ToList();
        }
        catch (JsonException ex)
        {
            throw new RoostException($"{FilePath} esta roto ({ex.Message}). Arreglalo o borralo y vuelve a registrar los sitios.");
        }
    }

    public Site? Find(string name) => Load().FirstOrDefault(s => s.Name == name);

    /// <summary>Añade el sitio o, si ya existe uno con ese nombre, lo sustituye (repetir <c>site add</c> no rompe nada).</summary>
    public void Upsert(Site site) => Save(Load().Where(s => s.Name != site.Name).Append(site));

    public bool Remove(string name)
    {
        var sites = Load();
        if (sites.All(s => s.Name != name)) return false;
        Save(sites.Where(s => s.Name != name));
        return true;
    }

    private void Save(IEnumerable<Site> sites)
    {
        Directory.CreateDirectory(paths.State);
        // Escritura atómica: un corte a medias no deja un sites.json roto.
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(sites.OrderBy(s => s.Name, StringComparer.Ordinal).ToList(), SiteJson.Default.ListSite));
        File.Move(temp, FilePath, overwrite: true);
    }
}

/// <summary>
/// El JSON de <c>sites.json</c>, generado al compilar: así lo puede leer también el shim de
/// <c>php</c>, que se publica con NativeAOT (sin serialización por reflexión).
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true)]
[JsonSerializable(typeof(List<Site>))]
internal sealed partial class SiteJson : JsonSerializerContext;
