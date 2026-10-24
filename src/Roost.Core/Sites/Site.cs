using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Roost.Core.Git;
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

    /// <summary>
    /// Su base de datos en la global, si se eligió una (Nuevo sitio: crear una o usar una que ya
    /// existe); null = la que sale de su nombre (<c>DbAdmin.NameFor</c>). Solo el nombre: nada secreto.
    /// </summary>
    public string? Database { get; init; }

    /// <summary>El repositorio del que viene el sitio (ROOST.md: git al estilo Plesk); null en los sitios de siempre.</summary>
    public SiteGit? Git { get; init; }

    /// <summary>
    /// Sitios avanzados (C7): otros nombres <c>.test</c> por los que se sirve el sitio, ya
    /// normalizados (<see cref="SiteAliases"/>): un alias (<c>tienda-alias.test</c>) o un subdominio
    /// suyo (<c>api.tienda.test</c>). null o vacío = ninguno.
    /// </summary>
    public IReadOnlyList<string>? Aliases { get; init; }

    /// <summary>
    /// Sitios avanzados (C3): la carpeta aparcada de la que sale el sitio (cada subcarpeta suya es un
    /// sitio); null = registrado a mano. Si su carpeta desaparece, Roost lo quita solo.
    /// </summary>
    public string? ParkedIn { get; init; }

    public string Host => $"{Name}.test";

    /// <summary>Todos los nombres del sitio que tienen que resolver: el suyo y sus alias (para el hosts).</summary>
    [JsonIgnore]
    public IReadOnlyList<string> HostNames => [Host, .. Aliases ?? []];

    public string DocumentRoot => Root is null ? Folder : Path.GetFullPath(Path.Combine(Folder, Root));

    // Una etiqueta DNS: minúsculas, números y guiones, sin guion al principio ni al final.
    [GeneratedRegex("^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$")]
    private static partial Regex NamePattern();

    public static bool IsValidName(string name) => NamePattern().IsMatch(name);
}

/// <summary>
/// Lo que Roost sabe del repositorio de un sitio. NADA secreto: el token y la clave viven
/// aparte (el Administrador de credenciales y <c>state/git/keys</c>); aquí solo va el tipo de
/// acceso y el nombre con el que buscarlos.
/// </summary>
public sealed record SiteGit
{
    /// <summary><see cref="Access"/>: un repositorio público, sin credenciales.</summary>
    public const string AccessNone = "none";

    /// <summary><see cref="Access"/>: token HTTPS en el Administrador de credenciales (<see cref="CredentialTarget"/>).</summary>
    public const string AccessToken = "token";

    /// <summary><see cref="Access"/>: la clave SSH que Roost generó para el sitio (<see cref="KeyName"/>).</summary>
    public const string AccessKey = "key";

    /// <summary><see cref="Access"/>: lo que tenga el usuario (su <c>~/.ssh</c>, su git); Roost no guarda nada.</summary>
    public const string AccessOwn = "own";

    /// <summary>La dirección del remoto, ya normalizada (<see cref="GitUrl.Normalized"/>): nunca con credenciales dentro.</summary>
    public required string Remote { get; init; }

    /// <summary>La rama del checkout.</summary>
    public required string Branch { get; init; }

    /// <summary>Cómo se entra: <see cref="AccessNone"/>, <see cref="AccessToken"/>, <see cref="AccessKey"/> o <see cref="AccessOwn"/>.</summary>
    public string Access { get; init; } = AccessNone;

    /// <summary>El nombre de la clave SSH de Roost (<c>state/git/keys/&lt;nombre&gt;</c>), con <see cref="AccessKey"/>. Lo rellena el paquete de claves.</summary>
    public string? KeyName { get; init; }

    /// <summary>El target del Administrador de credenciales, con <see cref="AccessToken"/>. Lo rellena el paquete de tokens.</summary>
    public string? CredentialTarget { get; init; }

    [JsonConverter(typeof(JsonStringEnumConverter<GitProvider>))]
    public GitProvider Provider { get; init; } = GitProvider.Other;

    /// <summary>Si la app mira cada cierto tiempo si hay commits nuevos (solo <c>ls-remote</c>; nunca descarga).</summary>
    public bool CheckUpdates { get; init; } = true;

    /// <summary>La última vez que Roost clonó o actualizó el sitio.</summary>
    public DateTimeOffset? LastPullUtc { get; init; }

    /// <summary>El commit del checkout la última vez que Roost lo miró.</summary>
    public GitCommit? LastCommit { get; init; }
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
        // Escritura atómica: un corte a medias no deja un sites.json roto. El temporal es único: la
        // app y el daemon pueden escribir a la vez (los dos vigilan las carpetas aparcadas, C3).
        var temp = $"{FilePath}.tmp-{Guid.NewGuid():N}";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(sites.OrderBy(s => s.Name, StringComparer.Ordinal).ToList(), SiteJson.Default.ListSite));
            File.Move(temp, FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) Install.InstallFiles.TryDeleteFile(temp);
        }
    }
}

/// <summary>
/// El JSON de <c>sites.json</c>, generado al compilar: así lo puede leer también el shim de
/// <c>php</c>, que se publica con NativeAOT (sin serialización por reflexión).
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true)]
[JsonSerializable(typeof(List<Site>))]
internal sealed partial class SiteJson : JsonSerializerContext;
