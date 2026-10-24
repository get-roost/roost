namespace Roost.Core.Sites;

/// <summary>Por qué no vale un alias (<see cref="SiteAliases.Check"/>); la CLI y la app ponen cada una su texto.</summary>
public enum AliasProblem
{
    None,

    /// <summary>No es un nombre .test válido (lo que admite el hosts).</summary>
    Invalid,

    /// <summary>Es el nombre del propio sitio.</summary>
    IsSiteName,

    /// <summary>Ya es un alias del sitio.</summary>
    AlreadyThere,

    /// <summary>El sitio ya tiene <see cref="SiteAliases.MaxPerSite"/>.</summary>
    TooMany,

    /// <summary>Es un nombre de otro sitio (el suyo o un alias suyo).</summary>
    OtherSite,

    /// <summary>Es un subdominio de un nombre de otro sitio: lo serviría ese.</summary>
    UnderOtherSite,

    /// <summary>Debajo de él queda un nombre de otro sitio.</summary>
    OverOtherSite,
}

/// <summary>
/// Sitios avanzados (C7): los alias de un sitio, otros nombres <c>.test</c> por los que se sirve
/// (<c>tienda-alias.test</c>), y sus subdominios con nombre (<c>api.tienda.test</c>). El servidor web
/// ya sirve <c>*.tienda.test</c> y el certificado lo cubre; lo que falta para un subdominio es su
/// línea en el hosts (con el DNS local resuelve solo). Un alias necesita además su <c>server_name</c>
/// y su nombre en el certificado. Las reglas de lo que vale son las del hosts
/// (<see cref="HostsBlock.IsValidHost"/>: lo que acaba escribiendo el helper elevado).
/// </summary>
public static class SiteAliases
{
    /// <summary>Alias como mucho por sitio: el helper rechaza peticiones de más de 1000 nombres.</summary>
    public const int MaxPerSite = 50;

    /// <summary>
    /// El alias como se guarda: sin espacios ni punto final, en minúsculas y con <c>.test</c> si es
    /// una sola etiqueta (vale <c>tienda-alias</c> o <c>tienda-alias.test</c>). Uno con puntos que no
    /// acaba en <c>.test</c> (<c>midominio.com</c>) se deja tal cual, y no vale: no se convierte a
    /// escondidas en <c>midominio.com.test</c>. No lo valida: eso es <see cref="WhyNot"/>.
    /// </summary>
    public static string Normalize(string alias)
    {
        var name = alias.Trim().TrimEnd('.').ToLowerInvariant();
        return name.EndsWith(".test", StringComparison.Ordinal) || name == "test" || name.Contains('.') ? name : name + ".test";
    }

    /// <summary>true si <paramref name="name"/> es un subdominio de <paramref name="parent"/> (<c>api.tienda.test</c> de <c>tienda.test</c>).</summary>
    public static bool IsSubdomainOf(string name, string parent) =>
        name.Length > parent.Length + 1 && name.EndsWith("." + parent, StringComparison.Ordinal);

    /// <summary>true si vale como nombre en el hosts (la regla del helper elevado, <see cref="HostsBlock.IsValidHost"/>).</summary>
    public static bool IsValidName(string name) => HostsBlock.IsValidHost(name);

    /// <summary>
    /// Qué le pasa a <paramref name="alias"/> (ya normalizado) para <paramref name="site"/>:
    /// <see cref="AliasProblem.None"/> si vale, y si choca con otro sitio, cuál y con qué nombre suyo.
    /// No puede ser un nombre de otro sitio ni de sus alias, ni un subdominio suyo (lo serviría el
    /// otro), ni dejar debajo un nombre de otro sitio (su <c>*.</c> se lo quitaría).
    /// </summary>
    public static (AliasProblem Problem, Site? Other, string? Name) Check(Site site, string alias, IReadOnlyList<Site> all)
    {
        if (!HostsBlock.IsValidHost(alias)) return (AliasProblem.Invalid, null, null);
        if (alias == site.Host) return (AliasProblem.IsSiteName, null, null);
        if (site.Aliases?.Contains(alias, StringComparer.Ordinal) == true) return (AliasProblem.AlreadyThere, null, null);
        if ((site.Aliases?.Count ?? 0) >= MaxPerSite) return (AliasProblem.TooMany, null, null);

        foreach (var other in all.Where(s => s.Name != site.Name))
        {
            foreach (var name in other.HostNames)
            {
                if (alias == name) return (AliasProblem.OtherSite, other, name);
                if (IsSubdomainOf(alias, name)) return (AliasProblem.UnderOtherSite, other, name);
                if (IsSubdomainOf(name, alias) && !IsSubdomainOf(alias, site.Host)) return (AliasProblem.OverOtherSite, other, name);
            }
        }
        return (AliasProblem.None, null, null);
    }

    /// <summary>Por qué no vale <paramref name="alias"/> (ya normalizado), en el texto de la CLI; null si vale (<see cref="Check"/>).</summary>
    public static string? WhyNot(Site site, string alias, IReadOnlyList<Site> all)
    {
        var (problem, other, name) = Check(site, alias, all);
        return problem switch
        {
            AliasProblem.None => null,
            AliasProblem.Invalid => $"'{alias}' no vale como nombre: tiene que acabar en .test y llevar solo minusculas, numeros, guiones y puntos (hasta {HostsBlock.MaxLabels} partes delante de .test).",
            AliasProblem.IsSiteName => $"{alias} ya es el nombre del sitio.",
            AliasProblem.AlreadyThere => $"{alias} ya es un alias de {site.Host}.",
            AliasProblem.TooMany => $"{site.Host} ya tiene {MaxPerSite} alias, que es el maximo.",
            AliasProblem.OtherSite => $"{alias} ya es de {other!.Host}.",
            AliasProblem.UnderOtherSite => $"{alias} es un subdominio de {name}, que es de {other!.Host}: lo serviria ese sitio.",
            _ => $"Debajo de {alias} queda {name}, que es de {other!.Host}.",
        };
    }

    /// <summary>
    /// Lo mismo para el nombre de un sitio (al darlo de alta o renombrarlo): que no sea ya un alias
    /// de otro, ni un subdominio de un alias de otro. null si vale.
    /// </summary>
    public static string? WhyNotName(string name, IReadOnlyList<Site> all, string? except = null)
    {
        var host = $"{name}.test";
        foreach (var other in all.Where(s => s.Name != except && s.Name != name))
        {
            foreach (var alias in other.Aliases ?? [])
            {
                if (alias == host) return $"{host} ya es un alias de {other.Host}.";
                if (IsSubdomainOf(host, alias)) return $"{host} es un subdominio de {alias}, que es un alias de {other.Host}.";
            }
        }
        return null;
    }

    /// <summary>
    /// Los alias que son otro nombre (<c>tienda-alias.test</c>), y no un subdominio del sitio ni de otro
    /// alias suyo: cada uno lleva también su <c>*.</c>, como el nombre del sitio.
    /// </summary>
    public static IReadOnlyList<string> Others(Site site)
    {
        var aliases = site.Aliases ?? [];
        return aliases.Where(a => !IsSubdomainOf(a, site.Host) && !aliases.Any(other => IsSubdomainOf(a, other))).ToList();
    }

    /// <summary>
    /// PURO: los nombres del <c>server_name</c> de nginx (y del <c>ServerName</c>/<c>ServerAlias</c> de
    /// Apache): el del sitio y cada alias que es otro nombre (<see cref="Others"/>), cada uno con su
    /// <c>*.</c>. Los subdominios ya entran por un <c>*.</c> (en nginx y en Apache, de cualquier nivel).
    /// </summary>
    public static IReadOnlyList<string> ServerNames(Site site)
    {
        var names = new List<string> { site.Host, "*." + site.Host };
        foreach (var alias in Others(site))
        {
            names.Add(alias);
            names.Add("*." + alias);
        }
        return names;
    }

    /// <summary>
    /// PURO: los nombres DNS del certificado del sitio: los de <see cref="ServerNames"/> y, además, los
    /// subdominios de más de un nivel (<c>v1.api.tienda.test</c>), que un <c>*.tienda.test</c> en un
    /// certificado no cubre (solo vale para una etiqueta).
    /// </summary>
    public static IReadOnlyList<string> CertNames(Site site)
    {
        var names = ServerNames(site).ToList();
        foreach (var alias in site.Aliases ?? [])
        {
            if (names.Contains(alias)) continue;
            var parent = alias[(alias.IndexOf('.') + 1)..];
            if (!names.Contains("*." + parent)) names.Add(alias);
        }
        return names;
    }
}
