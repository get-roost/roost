using System.Text.RegularExpressions;
using Roost.Core.Sites;

namespace Roost.Core.Git;

/// <summary>Cómo se llega al repositorio.</summary>
public enum GitScheme
{
    /// <summary><c>https://</c> (o <c>http://</c>, para un servidor de la red local).</summary>
    Https,

    /// <summary><c>ssh://</c> o la forma corta <c>git@servidor:ruta</c>.</summary>
    Ssh,

    /// <summary>Una carpeta de este equipo (<c>C:\repos\tienda.git</c>, <c>file:///…</c>): tests y repositorios locales.</summary>
    Local,
}

/// <summary>Quién aloja el repositorio, para los textos, los enlaces y las huellas de serie.</summary>
public enum GitProvider { GitHub, GitLab, Bitbucket, Codeberg, AzureDevOps, Other, Local }

/// <summary>
/// La dirección de un repositorio, entendida (PURO). Acepta https, ssh, la forma corta
/// <c>git@servidor:grupo/repo.git</c> y una carpeta local; rechaza <c>usuario:contraseña@</c>
/// (las credenciales van aparte, cifradas, nunca en la dirección) y <c>git://</c>.
/// </summary>
public sealed partial record GitUrl
{
    /// <summary>Tal como la escribió el usuario, recortada.</summary>
    public required string Original { get; init; }

    public required GitScheme Scheme { get; init; }

    /// <summary>En minúsculas; null para una carpeta local.</summary>
    public string? Host { get; init; }

    /// <summary>Solo si va explícito en la dirección.</summary>
    public int? Port { get; init; }

    /// <summary>El usuario de la dirección: <c>git</c> en ssh; en https, uno sin contraseña (se guarda, pero no va en <see cref="Normalized"/>).</summary>
    public string? User { get; init; }

    /// <summary>La ruta en el servidor sin la barra inicial (<c>acme/tienda.git</c>), o la carpeta local completa.</summary>
    public required string Path { get; init; }

    public required GitProvider Provider { get; init; }

    /// <summary>Lo que se le pasa a git y lo que se guarda: sin usuario de https, sin barra final, con el host en minúsculas.</summary>
    public required string Normalized { get; init; }

    /// <summary><c>acme/tienda</c> (la ruta sin <c>.git</c>), o el nombre de la carpeta si es local.</summary>
    public required string Label { get; init; }

    /// <summary>El último tramo de la ruta sin <c>.git</c>: <c>tienda</c>.</summary>
    public required string RepoName { get; init; }

    /// <summary>Un nombre de sitio válido (<see cref="Site.IsValidName"/>) sacado del nombre del repositorio: <c>Hello-World</c> → <c>hello-world</c>.</summary>
    public required string SuggestedSiteName { get; init; }

    public bool IsLocal => Scheme == GitScheme.Local;

    /// <summary>El nombre del proveedor para enseñarlo.</summary>
    public string ProviderName => Provider == GitProvider.Other ? Host ?? "otro" : NameOf(Provider);

    /// <summary>El nombre de un proveedor para enseñarlo («otro» si no es ninguno conocido).</summary>
    public static string NameOf(GitProvider provider) => provider switch
    {
        GitProvider.GitHub => "GitHub",
        GitProvider.GitLab => "GitLab",
        GitProvider.Bitbucket => "Bitbucket",
        GitProvider.Codeberg => "Codeberg",
        GitProvider.AzureDevOps => "Azure DevOps",
        GitProvider.Local => "carpeta local",
        _ => "otro",
    };

    /// <summary>El primer tramo de la etiqueta: la organización o el usuario (<c>acme</c> de <c>acme/tienda</c>).</summary>
    public string Owner => Label.Split('/')[0];

    /// <summary>
    /// La página de las deploy keys de este repositorio, donde se pega la clave pública de Roost
    /// (solo lectura), o null si el proveedor no las tiene por repositorio (Azure DevOps) o no se
    /// conoce (un servidor propio).
    /// </summary>
    public string? DeployKeysUrl => IsLocal ? null : Provider switch
    {
        GitProvider.GitHub => $"https://github.com/{Label}/settings/keys",
        GitProvider.GitLab => $"https://gitlab.com/{Label}/-/settings/repository#js-deploy-keys-settings",
        GitProvider.Bitbucket => $"https://bitbucket.org/{Label}/admin/access-keys/",
        GitProvider.Codeberg => $"https://codeberg.org/{Label}/settings/keys",
        _ => null,
    };

    /// <summary>Dónde se crea un token con el permiso justo (solo leer el repositorio), o null si no se conoce.</summary>
    public string? NewTokenUrl => IsLocal ? null : Provider switch
    {
        GitProvider.GitHub => "https://github.com/settings/personal-access-tokens/new",
        GitProvider.GitLab => "https://gitlab.com/-/user_settings/personal_access_tokens?name=Roost&scopes=read_repository",
        GitProvider.Bitbucket => $"https://bitbucket.org/{Label}/admin/access-tokens",
        GitProvider.Codeberg => "https://codeberg.org/user/settings/applications",
        GitProvider.AzureDevOps => $"https://dev.azure.com/{Owner}/_usersSettings/tokens",
        _ => null,
    };

    /// <summary>El permiso que necesita el token para clonar y actualizar, como lo llama cada proveedor.</summary>
    public string TokenPermission => Provider switch
    {
        GitProvider.GitHub => "Contents: Read",
        GitProvider.GitLab => "read_repository",
        GitProvider.Bitbucket => "Repositories: Read",
        GitProvider.Codeberg => "read:repository",
        GitProvider.AzureDevOps => "Code: Read",
        _ => "leer el repositorio",
    };

    /// <summary>
    /// La dirección SSH del mismo repositorio (<c>git@github.com:acme/tienda.git</c>) para los
    /// proveedores conocidos; ella misma si ya es SSH; null si no se sabe (un servidor propio).
    /// </summary>
    public GitUrl? ToSsh()
    {
        if (Scheme == GitScheme.Ssh) return this;
        if (IsLocal || Host is null || Provider is not (GitProvider.GitHub or GitProvider.GitLab or GitProvider.Bitbucket or GitProvider.Codeberg)) return null;
        return Parse($"git@{Host}:{Label}.git");
    }

    /// <summary>La dirección https del mismo repositorio para los proveedores conocidos; ella misma si ya es https; null si no se sabe.</summary>
    public GitUrl? ToHttps()
    {
        if (Scheme == GitScheme.Https) return this;
        if (IsLocal || Host is null || Port is not null || Provider is not (GitProvider.GitHub or GitProvider.GitLab or GitProvider.Bitbucket or GitProvider.Codeberg)) return null;
        return Parse($"https://{Host}/{Label}.git");
    }

    public const string InvalidMessage =
        "No se entiende esa direccion. Vale https://servidor/grupo/repo, git@servidor:grupo/repo.git, "
        + "ssh://git@servidor/grupo/repo o una carpeta de este equipo (C:\\repos\\tienda.git).";

    public const string CredentialsInsideMessage =
        "Quita el usuario y la contrasena de la direccion: Roost te los pedira aparte y los guardara cifrados.";

    public const string GitProtocolMessage =
        "El protocolo git:// va sin cifrar ni autenticar y Roost no lo usa: pon la direccion https:// o ssh:// del repositorio.";

    /// <summary>Lanza <see cref="RoostException"/> con el motivo si la dirección no vale.</summary>
    public static GitUrl Parse(string text)
    {
        if (TryParse(text, out var url, out var error)) return url;
        throw new RoostException(error);
    }

    public static bool TryParse(string text, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out GitUrl? url, [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? error)
    {
        url = null;
        error = null;
        var original = (text ?? "").Trim();
        if (original.Length == 0 || original.Any(char.IsControl))
        {
            error = InvalidMessage;
            return false;
        }

        var schemeMatch = SchemePattern().Match(original);
        if (schemeMatch.Success)
        {
            var scheme = schemeMatch.Groups["scheme"].Value.ToLowerInvariant();
            switch (scheme)
            {
                case "http" or "https":
                    return TryParseUri(original, GitScheme.Https, out url, out error);
                case "ssh" or "git+ssh" or "ssh+git":
                    return TryParseUri(original, GitScheme.Ssh, out url, out error);
                case "file":
                    if (!Uri.TryCreate(original, UriKind.Absolute, out var fileUri) || !fileUri.IsFile)
                    {
                        error = InvalidMessage;
                        return false;
                    }
                    return TryParseLocal(original, fileUri.LocalPath, out url, out error);
                case "git":
                    error = GitProtocolMessage;
                    return false;
                default:
                    error = InvalidMessage;
                    return false;
            }
        }

        // usuario:contraseña@servidor:ruta, la forma corta con credenciales dentro.
        if (ScpWithPasswordPattern().IsMatch(original))
        {
            error = CredentialsInsideMessage;
            return false;
        }

        var scp = ScpPattern().Match(original);
        if (scp.Success)
        {
            var host = scp.Groups["host"].Value.ToLowerInvariant();
            var path = scp.Groups["path"].Value.TrimEnd('/');
            if (path.Length == 0 || !TryRepoName(path, out var repo))
            {
                error = InvalidMessage;
                return false;
            }
            var user = scp.Groups["user"].Value;
            url = new GitUrl
            {
                Original = original,
                Scheme = GitScheme.Ssh,
                Host = host,
                User = user,
                Path = path.TrimStart('/'),
                Provider = ProviderOf(host),
                Normalized = $"{user}@{host}:{path}",
                Label = LabelOf(path),
                RepoName = repo,
                SuggestedSiteName = SiteNameFor(repo),
            };
            return true;
        }

        if (System.IO.Path.IsPathRooted(original))
            return TryParseLocal(original, original, out url, out error);

        error = InvalidMessage;
        return false;
    }

    private static bool TryParseUri(string original, GitScheme scheme, out GitUrl? url, out string? error)
    {
        url = null;
        error = null;
        if (!Uri.TryCreate(original, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
        {
            error = InvalidMessage;
            return false;
        }

        var userInfo = Uri.UnescapeDataString(uri.UserInfo);
        if (userInfo.Contains(':'))
        {
            error = CredentialsInsideMessage;
            return false;
        }

        var path = Uri.UnescapeDataString(uri.AbsolutePath).Trim('/');
        if (path.Length == 0 || !TryRepoName(path, out var repo))
        {
            error = InvalidMessage;
            return false;
        }

        var host = uri.Host.ToLowerInvariant();
        var port = uri.IsDefaultPort ? (int?)null : uri.Port;
        var user = userInfo.Length == 0 ? null : userInfo;
        var portText = port is null ? "" : ":" + port;
        // En ssh el usuario es parte de la dirección (git@); en https se guarda aparte.
        var normalized = scheme == GitScheme.Ssh
            ? $"ssh://{(user is null ? "" : user + "@")}{host}{portText}/{path}"
            : $"{uri.Scheme.ToLowerInvariant()}://{host}{portText}/{path}";

        url = new GitUrl
        {
            Original = original,
            Scheme = scheme,
            Host = host,
            Port = port,
            User = user,
            Path = path,
            Provider = ProviderOf(host),
            Normalized = normalized,
            Label = LabelOf(path),
            RepoName = repo,
            SuggestedSiteName = SiteNameFor(repo),
        };
        return true;
    }

    private static bool TryParseLocal(string original, string localPath, out GitUrl? url, out string? error)
    {
        url = null;
        error = null;
        string full;
        try
        {
            full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(localPath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = InvalidMessage;
            return false;
        }

        if (!TryRepoName(full.Replace('\\', '/'), out var repo))
        {
            error = InvalidMessage;
            return false;
        }

        url = new GitUrl
        {
            Original = original,
            Scheme = GitScheme.Local,
            Path = full,
            Provider = GitProvider.Local,
            Normalized = full,
            Label = repo,
            RepoName = repo,
            SuggestedSiteName = SiteNameFor(repo),
        };
        return true;
    }

    /// <summary>El último tramo con contenido, sin <c>.git</c>; <c>repo/.git</c> cuenta como <c>repo</c>.</summary>
    private static bool TryRepoName(string path, out string repo)
    {
        repo = "";
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = segments.Length - 1; i >= 0; i--)
        {
            var segment = segments[i];
            if (segment == ".git" || segment == "~" || segment == ".") continue;
            if (segment.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) segment = segment[..^4];
            if (segment.Length == 0 || segment == "..") return false;
            repo = segment;
            return true;
        }
        return false;
    }

    private static string LabelOf(string path)
    {
        var label = path.Trim('/');
        if (label.StartsWith("~/", StringComparison.Ordinal)) label = label[2..];
        if (label.EndsWith("/.git", StringComparison.OrdinalIgnoreCase)) label = label[..^5];
        if (label.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) label = label[..^4];
        return label;
    }

    /// <summary>Un nombre de sitio válido a partir del nombre de un repositorio (PURO).</summary>
    public static string SiteNameFor(string repoName)
    {
        var name = NotLabelChars().Replace(repoName.ToLowerInvariant(), "-").Trim('-');
        if (name.Length > 63) name = name[..63].Trim('-');
        return Site.IsValidName(name) ? name : "sitio";
    }

    private static GitProvider ProviderOf(string host) => host switch
    {
        "github.com" or "www.github.com" or "gist.github.com" => GitProvider.GitHub,
        "gitlab.com" or "www.gitlab.com" => GitProvider.GitLab,
        "bitbucket.org" or "www.bitbucket.org" => GitProvider.Bitbucket,
        "codeberg.org" => GitProvider.Codeberg,
        "dev.azure.com" or "ssh.dev.azure.com" or "vs-ssh.visualstudio.com" => GitProvider.AzureDevOps,
        _ when host.EndsWith(".visualstudio.com", StringComparison.Ordinal) => GitProvider.AzureDevOps,
        _ => GitProvider.Other,
    };

    [GeneratedRegex(@"^(?<scheme>[A-Za-z][A-Za-z0-9+.\-]*)://")]
    private static partial Regex SchemePattern();

    // git@servidor:ruta (sin //, que sería una URL, y con usuario, para no confundirlo con C:\ruta).
    [GeneratedRegex(@"^(?<user>[^@/\\:\s]+)@(?<host>[^:/\\\s@]+):(?!//)(?<path>[^\s]+)$")]
    private static partial Regex ScpPattern();

    [GeneratedRegex(@"^[^@/\\:\s]+:[^@/\\\s]*@[^:/\\\s@]+:")]
    private static partial Regex ScpWithPasswordPattern();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NotLabelChars();
}
