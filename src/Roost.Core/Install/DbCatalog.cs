using System.Text.Json;
using System.Text.RegularExpressions;
using Roost.Core.Models;

namespace Roost.Core.Install;

/// <summary>
/// Lo que escribe el usuario para elegir base de datos (ROOST.md §4.5): <c>mariadb@11.4</c>,
/// <c>mysql@8</c>, <c>mysql@5.7.44</c>, <c>mariadb</c> (sin versión) o solo la versión.
/// La versión puede ser mayor (<c>8</c>: la rama 8.x más nueva), rama (<c>11.4</c>) o exacta.
/// </summary>
public sealed partial record DbSpec(DbEngine? Engine, string? Version)
{
    [GeneratedRegex(@"^\d+(\.\d+){0,2}$")]
    private static partial Regex VersionPattern();

    public static DbSpec Parse(string text)
    {
        text = text.Trim();
        var at = text.IndexOf('@');
        var enginePart = at >= 0 ? text[..at] : text;
        var versionPart = at >= 0 ? text[(at + 1)..] : null;

        if (DbEngines.TryParse(enginePart, out var engine))
        {
            if (versionPart is not null && !VersionPattern().IsMatch(versionPart))
                throw new RoostException($"'{versionPart}' no es una version: usa por ejemplo mariadb@11.4, mysql@8 o mysql@5.7.44.");
            return new DbSpec(engine, string.IsNullOrEmpty(versionPart) ? null : versionPart);
        }

        if (at < 0 && VersionPattern().IsMatch(text))
            return new DbSpec(null, text);

        throw new RoostException($"'{text}' no es una base de datos: usa mariadb@<version> o mysql@<version> (por ejemplo mariadb@11.4, mysql@8.4).");
    }

    /// <summary>true si <paramref name="version"/> casa con la parte de versión (sin versión, casa todo).</summary>
    public bool Matches(DbEngine engine, ComponentVersion version)
    {
        if (Engine is { } wanted && wanted != engine) return false;
        if (Version is null) return true;

        var parts = Version.Split('.').Select(int.Parse).ToArray();
        return parts.Length switch
        {
            1 => version.Major == parts[0],
            2 => version.Major == parts[0] && version.Minor == parts[1],
            _ => version.Major == parts[0] && version.Minor == parts[1] && version.Patch == parts[2],
        };
    }

    public override string ToString() =>
        Engine is { } e ? (Version is null ? e.Key() : $"{e.Key()}@{Version}") : Version ?? "";
}

/// <summary>Elige un <see cref="DbBuild"/> del catálogo a partir de lo que pide el usuario.</summary>
public static class DbCatalog
{
    /// <summary>
    /// Sin versión se elige la rama de soporte largo más nueva del motor; con versión, la rama
    /// más nueva que case. Una versión exacta tiene que ser la última de su rama, porque los
    /// catálogos solo traen esa.
    /// </summary>
    public static DbBuild Resolve(IEnumerable<DbBuild> catalog, DbSpec spec)
    {
        var all = catalog.ToList();
        var candidates = all.Where(b => spec.Matches(b.Engine, b.Version)).OrderByDescending(b => b.Version).ToList();

        if (candidates.Count == 0)
        {
            // ¿Es una versión exacta que no es la última de su rama?
            if (spec.Version?.Count(c => c == '.') == 2)
            {
                var branchSpec = spec with { Version = string.Join('.', spec.Version.Split('.').Take(2)) };
                if (all.FirstOrDefault(b => branchSpec.Matches(b.Engine, b.Version)) is { } latest)
                {
                    throw new RoostException(
                        $"Solo se instala la ultima version de cada rama: para {latest.Spec} es {latest.Version}. "
                        + $"Instala {latest.Engine.Key()}@{latest.Version} (o pide la rama: {latest.Spec}).");
                }
            }

            var available = string.Join(", ", all.OrderByDescending(b => b.Version).Select(b => b.Spec));
            throw new RoostException($"No hay ninguna base de datos que case con '{spec}'. Disponibles: {available}.");
        }

        if (spec.Engine is null && candidates.Select(c => c.Engine).Distinct().Count() > 1)
            throw new RoostException($"'{spec}' vale para MariaDB y para MySQL: di cual, por ejemplo mariadb@{spec.Version} o mysql@{spec.Version}.");

        if (spec.Version is null && candidates.FirstOrDefault(b => b.Lts) is { } lts)
            return lts;
        return candidates[0];
    }
}

/// <summary>
/// MariaDB para Windows leído de la API REST oficial de descargas (ROOST.md §5.4): las ramas
/// estables y, de cada una, el zip de su última versión con su SHA-256.
/// </summary>
public static class MariaDbCatalog
{
    public static readonly Uri MajorsUrl = new("https://downloads.mariadb.org/rest-api/mariadb/");

    public static Uri LatestUrl(string branch) => new($"https://downloads.mariadb.org/rest-api/mariadb/{branch}/latest/");

    /// <summary>Una rama tal como la describe la API.</summary>
    public sealed record Major(string Branch, string Status, string? SupportType, DateOnly? Eol)
    {
        public bool Stable => Status.Equals("Stable", StringComparison.OrdinalIgnoreCase);
        public bool Lts => SupportType?.Contains("Long Term", StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>PURO: las ramas de <c>/rest-api/mariadb/</c>.</summary>
    public static IReadOnlyList<Major> ParseMajors(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var majors = new List<Major>();
        if (!doc.RootElement.TryGetProperty("major_releases", out var list) || list.ValueKind != JsonValueKind.Array)
            return majors;

        foreach (var item in list.EnumerateArray())
        {
            var branch = GetString(item, "release_id");
            var status = GetString(item, "release_status");
            if (branch is null || status is null) continue;
            var eol = DateOnly.TryParse(GetString(item, "release_eol_date"), out var d) ? d : (DateOnly?)null;
            majors.Add(new Major(branch, status, GetString(item, "release_support_type"), eol));
        }
        return majors;
    }

    /// <summary>PURO: el zip de Windows de <c>/rest-api/mariadb/&lt;rama&gt;/latest/</c>; null si no lo hay.</summary>
    public static DbBuild? ParseLatest(string json, Major major)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("releases", out var releases) || releases.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var release in releases.EnumerateObject())
        {
            if (!ComponentVersion.TryParse(release.Name, out var version)) continue;
            if (!release.Value.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array) continue;

            foreach (var file in files.EnumerateArray())
            {
                var name = GetString(file, "file_name");
                if (name is null || !name.EndsWith("-winx64.zip", StringComparison.OrdinalIgnoreCase)) continue;
                if (!file.TryGetProperty("checksum", out var checksum)) continue;
                var sha = GetString(checksum, "sha256sum");
                if (string.IsNullOrWhiteSpace(sha)) continue;

                var urls = new List<Uri>();
                if (Uri.TryCreate(GetString(file, "file_download_url"), UriKind.Absolute, out var api))
                    urls.Add(new UriBuilder(api) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri); // la API da http://
                urls.Add(new Uri($"https://archive.mariadb.org/mariadb-{version}/winx64-packages/{name}"));

                return new DbBuild
                {
                    Engine = DbEngine.MariaDb,
                    Version = version,
                    Branch = major.Branch,
                    Lts = major.Lts,
                    FileName = name,
                    Urls = urls,
                    Sha256 = sha.Trim().ToLowerInvariant(),
                };
            }
        }
        return null;
    }

    /// <summary>Descarga las ramas estables y la última versión de cada una, de más nueva a más vieja.</summary>
    public static async Task<IReadOnlyList<DbBuild>> FetchAsync(HttpClient http, CancellationToken cancellation = default)
    {
        var majors = ParseMajors(await http.GetStringAsync(MajorsUrl, cancellation)).Where(m => m.Stable).ToList();
        var builds = await Task.WhenAll(majors.Select(async m =>
            ParseLatest(await http.GetStringAsync(LatestUrl(m.Branch), cancellation), m)));
        return builds.OfType<DbBuild>().OrderByDescending(b => b.Version).ToList();
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>
/// MySQL para Windows fijado a mano: Oracle no tiene API de descargas (ROOST.md §5.4). Cada
/// SHA-256 es el de un zip cuya firma PGP (el <c>.asc</c> del CDN) se verificó con las claves
/// de MySQL Release Engineering publicadas en repo.mysql.com; el MD5 publicado también cuadró.
/// </summary>
public static class MySqlCatalog
{
    /// <summary>El de este sistema (hoy, el de Windows; el de Linux llega con el plan de Linux).</summary>
    public static IReadOnlyList<DbBuild> Current => Windows;

    // Verificado el 22-oct-2026. 8.4.11 y 8.0.46: clave BCA4 3417 C3B4 85DD 128E C6D4 B7B3 B788 A8D3 785C
    // (RPM-GPG-KEY-mysql-2023). 5.7.44: clave 859B E8D7 C586 F538 430B 19C2 467B 942D 3A79 BD29 (RPM-GPG-KEY-mysql-2022).
    public static readonly IReadOnlyList<DbBuild> Windows =
    [
        Build("8.4.11", lts: true, "a492371d687d2bab088b0062581144a0044b8964baefdf4faa579292b423d25c"),
        Build("8.0.46", lts: false, "28e9eda019d88eff4478d811ea2110b83f02a3966be157fe91cc55def3ab0d4d"),
        Build("5.7.44", lts: false, "aed661fe8120254a1dc30f5a4d5de346681922f4847cf025e2d4084eca78e70e"),
    ];

    private static DbBuild Build(string version, bool lts, string sha256)
    {
        ComponentVersion.TryParse(version, out var v);
        var branch = $"{v.Major}.{v.Minor}";
        var file = $"mysql-{version}-winx64.zip";
        return new DbBuild
        {
            Engine = DbEngine.MySql,
            Version = v,
            Branch = branch,
            Lts = lts,
            FileName = file,
            // Downloads/ solo guarda la última de cada rama; al salir la siguiente, pasa a archives/.
            Urls =
            [
                new Uri($"https://cdn.mysql.com/Downloads/MySQL-{branch}/{file}"),
                new Uri($"https://cdn.mysql.com/archives/mysql-{branch}/{file}"),
            ],
            Sha256 = sha256,
        };
    }
}
