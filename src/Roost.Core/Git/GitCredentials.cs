using System.Globalization;

namespace Roost.Core.Git;

/// <summary>Un sitio para el que vale un token: protocolo, servidor (con su puerto si no es el de siempre) y ruta del repositorio (o ninguna: todo el servidor).</summary>
public sealed record CredentialLocation(string Protocol, string Host, string? Path)
{
    /// <summary><c>https://github.com/acme/tienda</c>: sin <c>.git</c>, sin barras sobrantes, el servidor en minúsculas.</summary>
    public string Key => $"{Protocol}://{Host}" + (Path is null ? "" : "/" + Path);

    /// <summary>Esta y las de arriba: <c>acme/tienda</c>, <c>acme</c> y el servidor entero.</summary>
    public IEnumerable<CredentialLocation> SelfAndParents()
    {
        var path = Path;
        while (path is not null)
        {
            yield return this with { Path = path };
            var slash = path.LastIndexOf('/');
            path = slash < 0 ? null : path[..slash];
        }
        yield return this with { Path = null };
    }

    /// <summary>
    /// Desde lo que manda git (<c>protocol</c>, <c>host</c>, <c>path</c>) o lo que escribe el usuario.
    /// La ruta se normaliza: sin barras al principio ni al final, sin <c>.git</c>. PURO.
    /// </summary>
    public static CredentialLocation Create(string protocol, string host, string? path)
    {
        var p = (path ?? "").Trim().Trim('/');
        if (p.EndsWith("/.git", StringComparison.OrdinalIgnoreCase)) p = p[..^5];
        if (p.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) p = p[..^4];
        p = p.Trim('/');
        return new CredentialLocation(protocol.Trim().ToLowerInvariant(), host.Trim().ToLowerInvariant(), p.Length == 0 ? null : p);
    }

    /// <summary>La de un repositorio https (con su ruta), o null si no es https.</summary>
    public static CredentialLocation? For(GitUrl url)
    {
        if (url.Scheme != GitScheme.Https || url.Host is null) return null;
        var protocol = url.Normalized.StartsWith("http://", StringComparison.Ordinal) ? "http" : "https";
        var host = url.Port is { } port ? $"{url.Host}:{port.ToString(CultureInfo.InvariantCulture)}" : url.Host;
        return Create(protocol, host, url.Path);
    }

    /// <summary>
    /// Lo que escribe el usuario en <c>roost git token</c>: <c>github.com/acme/tienda</c>,
    /// <c>https://github.com/acme/tienda.git</c> o <c>github.com</c> (todo el servidor). Solo
    /// https; http solo para un servidor de este equipo (el token iría en claro). PURO.
    /// </summary>
    public static bool TryParse(string text, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out CredentialLocation? location, [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? error)
    {
        location = null;
        error = null;
        var value = (text ?? "").Trim();
        if (!value.Contains("://", StringComparison.Ordinal)) value = "https://" + value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Host.Length == 0 || uri.Scheme is not ("https" or "http"))
        {
            error = $"'{text}' no vale: pon el servidor y la ruta del repositorio, como github.com/acme/tienda (o solo github.com para todo el servidor).";
            return false;
        }
        if (uri.UserInfo.Length > 0)
        {
            error = GitUrl.CredentialsInsideMessage;
            return false;
        }
        if (uri.Scheme == "http" && !uri.IsLoopback)
        {
            error = "Un token por http:// iria sin cifrar: Roost solo los usa por https:// (o con un servidor de este mismo equipo).";
            return false;
        }
        var host = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port.ToString(CultureInfo.InvariantCulture)}";
        location = Create(uri.Scheme, host, Uri.UnescapeDataString(uri.AbsolutePath));
        return true;
    }
}

/// <summary>
/// Los tokens HTTPS de git en el almacén (el Administrador de credenciales de Windows), con target
/// <c>Roost:git:https://servidor/ruta</c>. Un token vale para su ruta y todo lo que cuelga de ella:
/// el de <c>github.com/acme/tienda</c> solo para ese repositorio (lo normal, con
/// <c>credential.useHttpPath</c>), el de <c>github.com/acme</c> para los de acme y el de
/// <c>github.com</c> para todo el servidor. Gana el más concreto.
/// </summary>
public sealed class GitCredentials(ICredentialStore store, string? prefix = null)
{
    public const string ProductionPrefix = "Roost:git:";

    /// <summary>El prefijo con <see cref="TestVariable"/>: las pruebas a mano nunca tocan los targets de verdad.</summary>
    public const string TestPrefix = "Roost-test:git:";

    /// <summary>Con <c>ROOST_TEST_CREDENTIALS=1</c>, Roost (y el helper que lanza git) usan <see cref="TestPrefix"/>.</summary>
    public const string TestVariable = "ROOST_TEST_CREDENTIALS";

    public static string DefaultPrefix() =>
        Environment.GetEnvironmentVariable(TestVariable) == "1" ? TestPrefix : ProductionPrefix;

    public string Prefix { get; } = prefix ?? DefaultPrefix();

    public ICredentialStore Store => store;

    public string TargetOf(CredentialLocation location) => Prefix + location.Key;

    /// <summary>Lo que se enseña de un target: sin el prefijo.</summary>
    public string Describe(string target) => target.StartsWith(Prefix, StringComparison.Ordinal) ? target[Prefix.Length..] : target;

    /// <summary>Los targets que valdrían para esa ubicación, del más concreto al servidor entero.</summary>
    public IReadOnlyList<string> Candidates(CredentialLocation location) => location.SelfAndParents().Select(TargetOf).ToList();

    /// <summary>El token que usaría git para esa ubicación, o null.</summary>
    public StoredCredential? Find(CredentialLocation location) =>
        Candidates(location).Select(store.Read).FirstOrDefault(c => c is not null);

    /// <summary>El de un repositorio https, o null (también si no es https).</summary>
    public StoredCredential? Find(GitUrl url) => CredentialLocation.For(url) is { } location ? Find(location) : null;

    /// <summary>
    /// El usuario que va con el token si no se dice otro. GitHub no lo mira con un token
    /// (<c>x-access-token</c> vale también para los de GitHub Apps); GitLab acepta cualquiera con un
    /// token personal, de proyecto o de grupo (<c>oauth2</c>, que vale también para OAuth); los
    /// tokens de acceso de Bitbucket van con <c>x-token-auth</c>. Un deploy token de GitLab o
    /// un servidor propio pueden pedir el suyo: <c>--user</c>.
    /// </summary>
    public static string DefaultUserName(string host) => host.ToLowerInvariant() switch
    {
        "github.com" => "x-access-token",
        "gitlab.com" => "oauth2",
        "bitbucket.org" => "x-token-auth",
        _ => "roost",
    };

    /// <summary>Guarda el token de esa ubicación (sustituye el que hubiera). Devuelve el target.</summary>
    public string Save(CredentialLocation location, string token, string? userName = null)
    {
        token = (token ?? "").Trim();
        if (token.Length == 0) throw new RoostException("El token esta vacio.");
        if (token.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
            throw new RoostException("El token no puede llevar espacios ni saltos de linea: copialo entero, sin nada mas.");
        var user = string.IsNullOrWhiteSpace(userName) ? DefaultUserName(location.Host.Split(':')[0]) : userName.Trim();
        if (user.Any(char.IsControl)) throw new RoostException("El usuario no puede llevar saltos de linea.");
        var target = TargetOf(location);
        store.Write(new StoredCredential(target, user, token));
        return target;
    }

    public bool Remove(string target) => store.Delete(target);

    /// <summary>Los tokens de Roost (solo los targets y el usuario).</summary>
    public IReadOnlyList<CredentialInfo> List() => store.List(Prefix);

    /// <summary>
    /// Si lo que se guarda no parece un token del proveedor (los de GitHub empiezan por
    /// <c>github_pat_</c> o <c>ghp_</c>, los de GitLab por <c>glpat-</c>…), el aviso: suele ser la
    /// contraseña de la cuenta, que GitHub, GitLab y Bitbucket ya no aceptan. PURO.
    /// </summary>
    public static string? LooksLikePassword(string host, string token)
    {
        string[] prefixes = host.ToLowerInvariant() switch
        {
            "github.com" => ["github_pat_", "ghp_", "gho_", "ghu_", "ghs_"],
            "gitlab.com" => ["glpat-", "gldt-", "glptt-", "gloas-", "glcbt-"],
            "bitbucket.org" => ["ATBB", "ATCTT", "ATATT"],
            _ => [],
        };
        if (prefixes.Length == 0 || prefixes.Any(p => token.StartsWith(p, StringComparison.Ordinal))) return null;
        return $"Eso no parece un token de {host} (suelen empezar por {string.Join(" o ", prefixes.Take(2))}). Si es la contrasena de la cuenta, no servira: {host} solo acepta tokens.";
    }
}

/// <summary>
/// El protocolo de los helpers de credenciales de git (<c>gitcredentials</c>): git escribe por
/// stdin líneas <c>clave=valor</c> (<c>protocol</c>, <c>host</c>, <c>path</c>…) hasta una línea en
/// blanco, y el helper contesta por stdout <c>username=</c> y <c>password=</c>. El token viaja de
/// proceso a proceso por la tubería: nunca en argumentos, entorno ni ficheros. PURO salvo el almacén.
/// </summary>
public static class GitCredentialProtocol
{
    /// <summary>Lee la petición de git. Las claves de varios valores (<c>capability[]</c>, <c>wwwauth[]</c>) no le hacen falta a Roost y se saltan.</summary>
    public static IReadOnlyDictionary<string, string> Parse(TextReader input)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        while (input.ReadLine() is { } line)
        {
            line = line.TrimEnd('\r');
            if (line.Length == 0) break;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq];
            if (key.EndsWith("[]", StringComparison.Ordinal)) continue;
            values[key] = line[(eq + 1)..];
        }
        return values;
    }

    /// <summary>
    /// <c>get</c>: el token de lo más concreto que haya para esa dirección (repositorio, grupo,
    /// servidor), o nada (y git sigue sin credencial: con los prompts apagados, falla). Solo https;
    /// http solo con un servidor de este equipo.
    /// </summary>
    public static string Get(GitCredentials credentials, IReadOnlyDictionary<string, string> request)
    {
        if (Location(request) is not { } location) return "";
        if (credentials.Find(location) is not { } found) return "";
        var user = string.IsNullOrEmpty(found.UserName) ? GitCredentials.DefaultUserName(location.Host.Split(':')[0]) : found.UserName;
        return $"username={user}\npassword={found.Secret}\n";
    }

    /// <summary>
    /// <c>erase</c>: git lo pide cuando el servidor rechaza la credencial que le dimos. Se borra solo
    /// la del repositorio exacto y solo si es esa misma (el mismo token): uno de grupo o de todo el
    /// servidor puede valer para otros repositorios y no se toca. true si se borró.
    /// </summary>
    public static bool Erase(GitCredentials credentials, IReadOnlyDictionary<string, string> request)
    {
        if (Location(request) is not { Path: not null } location) return false;
        var target = credentials.TargetOf(location);
        if (credentials.Store.Read(target) is not { } stored) return false;
        if (request.TryGetValue("password", out var password) && password != stored.Secret) return false;
        return credentials.Remove(target);
    }

    /// <summary>
    /// Lo que hace <c>roost git-credential &lt;get|store|erase&gt;</c>. <c>store</c> no hace nada: los
    /// tokens los guarda el usuario con <c>roost git token set</c>, y lo único que git podría querer
    /// guardar es lo que acaba de darle Roost. Devuelve el código de salida.
    /// </summary>
    public static int Run(string action, TextReader input, TextWriter output, GitCredentials credentials)
    {
        var request = Parse(input);
        switch (action)
        {
            case "get":
                output.Write(Get(credentials, request));
                output.Flush();
                return 0;
            case "erase":
                Erase(credentials, request);
                return 0;
            case "store":
                return 0;
            default:
                return 1;
        }
    }

    private static CredentialLocation? Location(IReadOnlyDictionary<string, string> request)
    {
        if (!request.TryGetValue("protocol", out var protocol) || !request.TryGetValue("host", out var host) || host.Length == 0) return null;
        protocol = protocol.ToLowerInvariant();
        if (protocol == "http")
        {
            var name = host.Split(':')[0];
            if (name is not ("localhost" or "127.0.0.1" or "[::1]")) return null;
        }
        else if (protocol != "https")
        {
            return null;
        }
        request.TryGetValue("path", out var path);
        return CredentialLocation.Create(protocol, host, path);
    }
}
