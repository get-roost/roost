using System.Globalization;
using System.Text.RegularExpressions;
using Roost.Core.Git;
using Roost.Core.Sites;

namespace Roost.Desktop.ViewModels;

/// <summary>
/// Los textos de git de la app, con tildes (los de Core van sin ellas): se eligen por
/// <see cref="GitFailure.Kind"/> y se arman con lo que sabe la pantalla (la dirección, la clave, la
/// carpeta), en vez de enseñar el mensaje de Core. Son los del diseño de «git al estilo Plesk»
/// (§2.4). PURO: lo prueban los tests de vistas.
/// </summary>
public static partial class GitTexts
{
    /// <summary>Lo que no vale de una dirección, a partir del motivo de <see cref="GitUrl.TryParse"/> (texto 1 del diseño).</summary>
    public static string InvalidUrl(string coreError) => coreError switch
    {
        GitUrl.CredentialsInsideMessage => "Quita el usuario y la contraseña de la dirección: Roost te los pedirá aparte y los guardará cifrados.",
        GitUrl.GitProtocolMessage => "El protocolo git:// va sin cifrar ni autenticar y Roost no lo usa: pon la dirección https:// o ssh:// del repositorio.",
        _ => "No se entiende esa dirección. Vale https://servidor/grupo/repo, git@servidor:grupo/repo.git o ssh://git@servidor/grupo/repo (o una carpeta de este equipo).",
    };

    /// <summary>El nombre del proveedor tal como sale en las frases («GitHub», o el servidor).</summary>
    public static string Who(GitUrl? url) => url is null || url.Provider is GitProvider.Other or GitProvider.Local ? "el servidor" : url.ProviderName;

    /// <summary>
    /// Por qué falló git, para el usuario y con tildes. <paramref name="keyName"/> es la clave de Roost
    /// con la que se intentó; <paramref name="where"/>, el sitio o la carpeta (en un pull o un cambio de
    /// rama); <paramref name="changedFiles"/>, los ficheros con cambios si se conocen.
    /// </summary>
    public static string Explain(GitFailure failure, GitUrl? url, string? keyName = null, string? where = null,
        IReadOnlyList<string>? changedFiles = null, string? branch = null, bool ownKey = false)
    {
        var host = url?.Host ?? "el servidor";
        var token = url?.Host is { } h ? KnownHosts.HostToken(h, url.Port) : "el servidor";
        var label = url?.Label ?? where ?? "el repositorio";
        var place = where ?? label;
        var provider = url?.ProviderName ?? "el servidor";
        var detail = failure.Detail;
        switch (failure.Kind)
        {
            case GitFailureKind.HostUnreachable:
                return $"No se encuentra {host}. Comprueba el nombre y tu conexión. Si usas un proxy, Roost toma el de Windows.";
            case GitFailureKind.TlsError:
                return $"No se pudo comprobar el certificado de {host}. Si tu red tiene un proxy con certificado propio, instálalo en Windows: Roost usa el almacén de certificados del sistema.";
            case GitFailureKind.NetworkInterrupted:
                return $"Se cortó la conexión con {host} a medias. Prueba de nuevo.";
            case GitFailureKind.AuthRequired:
                return $"{label} pide acceso, o no existe: {Who(url)} no distingue entre las dos cosas.";
            case GitFailureKind.RepoNotFound:
                return $"No hay ningún repositorio git en {url?.Path ?? place}.";
            case GitFailureKind.KeyRejected:
                if (ownKey)
                    return $"{host} no acepta ninguna de tus claves SSH (las de ~/.ssh y las del ssh-agent de Windows). Pruébalo en tu terminal: ssh -T git@{host}";
                if (keyName is not null && !detail.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) && detail.Length > 0)
                    return $"{host} acepta la clave de {keyName}, pero con ella no se ve {label}: seguramente está en las deploy keys de otro repositorio. Ponla en las de {label}.";
                if (keyName is not null)
                {
                    var reuse = url?.Provider switch
                    {
                        GitProvider.GitHub => " (y en ese repositorio, no en otro: GitHub no permite reutilizarlas)",
                        GitProvider.Bitbucket => " (como clave de acceso del repositorio: Bitbucket no deja usar la misma clave en una cuenta)",
                        _ => "",
                    };
                    return $"{host} no acepta la clave de {keyName}. Asegúrate de haber pegado la clave pública en las deploy keys de {label}{reuse}.";
                }
                return $"{host} no acepta ninguna clave SSH de este equipo (Permission denied (publickey)).";
            case GitFailureKind.HostKeyUnverified:
                return $"Roost no conoce la huella de {token}, así que no se ha conectado. Compárala con la que te dé quien administra {host} y, si coincide, pulsa Confiar en este servidor.";
            case GitFailureKind.HostKeyChanged:
                return HostKeyChanged(failure, url);
            case GitFailureKind.KeyRequired:
                return $"{label} va por SSH, y por SSH siempre hace falta una clave: la de Roost para este sitio, un fichero de clave tuyo o las tuyas de ~/.ssh y el ssh-agent.";
            case GitFailureKind.KeyUnusable:
                var of = keyName is null ? "" : $" de {keyName}";
                return detail.Contains("UNPROTECTED", StringComparison.OrdinalIgnoreCase) || detail.Contains("bad permissions", StringComparison.OrdinalIgnoreCase)
                    ? $"OpenSSH no usa la clave{of} porque otros usuarios de este equipo pueden leerla. Quítale los permisos de más (solo tu usuario) o vuelve a crearla."
                    : $"No se puede usar la clave{of}: tiene contraseña o no tiene un formato de OpenSSH. Roost no puede preguntar la contraseña (git corre sin terminal).";
            case GitFailureKind.TokenRejected:
                return $"{provider} no acepta ese token. Puede haber caducado, ser de otra cuenta o no tener permiso en {label} (necesita {url?.TokenPermission ?? "leer el repositorio"}). Crea otro y sustitúyelo.";
            case GitFailureKind.TokenForbidden:
                return $"El token existe pero la organización {url?.Owner ?? ""} exige autorizarlo (SSO) o no tiene el permiso de contenido ({url?.TokenPermission ?? "leer el repositorio"}). Ábrelo en {provider} y autorízalo.";
            case GitFailureKind.PasswordNotAccepted:
                return $"{provider} ya no acepta la contraseña de la cuenta: hace falta un token. Créalo con el enlace de arriba.";
            case GitFailureKind.AccessMismatch:
                return failure.Message.StartsWith("Un token", StringComparison.Ordinal)
                    ? $"Un token solo vale por HTTPS, y {url?.Original ?? "esa dirección"} es SSH. Pon la dirección https:// del repositorio o entra con una clave SSH."
                    : $"Una clave SSH solo vale por SSH, y {url?.Original ?? "esa dirección"} es https. Pon la dirección SSH del repositorio (git@{url?.Host ?? "servidor"}:grupo/repo.git) o entra con un token.";
            case GitFailureKind.BranchNotFound:
                // El de Core no lleva palabras con tilde y trae las ramas que hay.
                return failure.Message;
            case GitFailureKind.FolderBusy:
                return FolderBusy(place);
            case GitFailureKind.LocalChanges:
                return LocalChanges(place, changedFiles);
            case GitFailureKind.Diverged:
                return $"{branch ?? "La rama"} en tu equipo tiene commits que no están en el remoto, así que no se puede actualizar sin fusionar. Hazlo desde la terminal (git pull --rebase, o merge).";
            case GitFailureKind.NotARepository:
                return $"{place} no es un repositorio git (no tiene .git).";
            case GitFailureKind.DubiousOwnership:
                return $"git rechaza {place} porque la carpeta pertenece a otro usuario de Windows (dubious ownership). Cambia el propietario de la carpeta a tu usuario.";
            case GitFailureKind.TimedOut:
                return $"{url?.Host ?? "git"} no ha contestado a tiempo y se ha cortado. Comprueba tu conexión y prueba de nuevo.";
            default:
                return detail.Length > 0 ? $"git ha fallado: {detail}" : "git ha fallado sin decir por qué.";
        }
    }

    /// <summary>Texto 8: la huella cambió. Las dos huellas salen del mensaje de Core, que las calcula del aviso de ssh.</summary>
    private static string HostKeyChanged(GitFailure failure, GitUrl? url)
    {
        var token = url?.Host is { } h ? KnownHosts.HostToken(h, url.Port) : "el servidor";
        var change = "";
        if (ChangedPattern().Match(failure.Message) is { Success: true } m)
            change = $" ({m.Groups["before"].Value} → {m.Groups["now"].Value})";
        else if (PresentedPattern().Match(failure.Message) is { Success: true } p)
            change = $" (ahora presenta {p.Groups["alg"].Value} {p.Groups["now"].Value})";
        var builtIn = url?.Host is { } host && KnownHosts.PublishedFor(host, url.Port) is { } published
            ? $" Para {published.Host}, Roost trae la que publica {GitUrl.NameOf(published.Provider)}: si la ha cambiado, actualiza Roost."
            : "";
        return $"La huella de {token} ha cambiado respecto a la que Roost tenía guardada{change}. Puede ser que hayan cambiado la clave del servidor "
            + $"o que alguien se esté haciendo pasar por él. No se ha conectado. Si quien lo administra confirma el cambio, pulsa Olvidar en Ajustes › Git y vuelve a comprobar.{builtIn}";
    }

    /// <summary>Texto 10: la carpeta no está vacía.</summary>
    public static string FolderBusy(string folder) =>
        $"{folder} ya existe y no está vacía. Elige otra carpeta o, si ya es ese proyecto, crea el sitio con la plantilla Vacía: Roost detectará su .git.";

    /// <summary>Texto 11: cambios sin guardar.</summary>
    public static string LocalChanges(string where, IReadOnlyList<string>? files)
    {
        var count = files?.Count ?? 0;
        var which = count == 0 ? "" : $" ({string.Join(", ", files!.Take(5))}{(count > 5 ? $" y {count - 5} más" : "")})";
        var amount = count == 0 ? "" : count == 1 ? " 1 fichero de" : $" {count} ficheros de";
        return $"Hay cambios sin guardar en{amount} {where}{which}. Roost no actualiza ni cambia de rama por encima de tu trabajo. Guárdalos (commit o stash) y vuelve a intentarlo.";
    }

    /// <summary>Texto 13: no se pudo preparar git (lo de Core trae el motivo sin tildes).</summary>
    public static string GitSetupFailed(string coreMessage) =>
        coreMessage.Contains("SHA-256", StringComparison.Ordinal)
            ? $"No se pudo preparar git: el SHA-256 de {GitCatalog.Windows.FileName} no coincide con el esperado. No se instala nada. Prueba de nuevo o indica otro git en Ajustes › Git."
            : $"No se pudo preparar git ({Trim(coreMessage)}). No se instala nada. Prueba de nuevo o indica otro git en Ajustes › Git.";

    private static string Trim(string message)
    {
        const string prefix = "No se pudo preparar git: ";
        var text = message.StartsWith(prefix, StringComparison.Ordinal) ? message[prefix.Length..] : message;
        var cut = text.IndexOf(" Prueba de nuevo", StringComparison.Ordinal);
        return (cut > 0 ? text[..cut] : text).TrimEnd('.', ' ');
    }

    /// <summary>Para qué vale un token, como se enseña: <c>github.com/acme/tienda</c> (sin el <c>https://</c>; un <c>http://</c> local, sí).</summary>
    public static string Where(string described) =>
        described.StartsWith("https://", StringComparison.Ordinal) ? described["https://".Length..] : described;

    /// <summary>Cómo entra el sitio, para el panel y Acceso….</summary>
    public static string AccessLabel(SiteGit git) => git.Access switch
    {
        SiteGit.AccessToken => "token HTTPS",
        SiteGit.AccessKey => "clave SSH de Roost",
        SiteGit.AccessOwn => "tus claves SSH",
        _ => "público, sin credenciales",
    };

    /// <summary>Los pasos para pegar la deploy key, según el proveedor (maqueta B).</summary>
    public static IReadOnlyList<string> DeployKeySteps(GitUrl? url) =>
    [
        "1. Copia la clave.",
        url?.Provider switch
        {
            GitProvider.GitHub => "2. En GitHub › Settings › Deploy keys › Add deploy key, pégala. Deja «Allow write access» sin marcar.",
            GitProvider.GitLab => "2. En GitLab › Settings › Repository › Deploy keys › Add new key, pégala. Deja «Grant write permissions» sin marcar.",
            GitProvider.Bitbucket => "2. En Bitbucket › Repository settings › Access keys › Add key, pégala (son de solo lectura).",
            GitProvider.Codeberg => "2. En Codeberg › Settings › Deploy keys › Add deploy key, pégala, sin permiso de escritura.",
            _ => "2. Pégala como deploy key (de solo lectura) del repositorio en tu servidor.",
        },
        "3. Vuelve aquí y pulsa Probar acceso.",
    ];

    private static readonly string[] Months = ["ene", "feb", "mar", "abr", "may", "jun", "jul", "ago", "sep", "oct", "nov", "dic"];

    /// <summary>«hoy 10:42», «ayer 18:05», «12 oct 09:30» o «12 oct 2025», en la hora local.</summary>
    public static string When(DateTimeOffset when, DateTimeOffset? now = null)
    {
        var local = when.ToLocalTime();
        var today = (now ?? DateTimeOffset.Now).ToLocalTime().Date;
        var time = local.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (local.Date == today) return $"hoy {time}";
        if (local.Date == today.AddDays(-1)) return $"ayer {time}";
        var day = $"{local.Day} {Months[local.Month - 1]}";
        return local.Year == today.Year ? $"{day} {time}" : $"{day} {local.Year}";
    }

    /// <summary>«1 fichero», «3 ficheros».</summary>
    public static string Plural(int count, string one, string many) => count == 1 ? $"1 {one}" : $"{count} {many}";

    /// <summary>«main y 3 ramas más», «main», «ninguna rama».</summary>
    public static string BranchesSummary(string? defaultBranch, IReadOnlyList<string> branches)
    {
        if (branches.Count == 0) return "ninguna rama todavía";
        var first = defaultBranch ?? branches[0];
        var others = branches.Count - 1;
        return others == 0 ? first : $"{first} y {Plural(others, "rama más", "ramas más")}";
    }

    // «(ED25519 SHA256:antes -> SHA256:ahora)» y «(ahora presenta ED25519 SHA256:…)», del mensaje de GitFailure.HostKeyChanged.
    [GeneratedRegex(@"\((?<alg>[A-Z0-9]+) (?<before>SHA256:[A-Za-z0-9+/=]+) -> (?<now>SHA256:[A-Za-z0-9+/=]+)\)")]
    private static partial Regex ChangedPattern();

    [GeneratedRegex(@"\(ahora presenta (?<alg>[A-Z0-9]+) (?<now>SHA256:[A-Za-z0-9+/=]+)\)")]
    private static partial Regex PresentedPattern();
}
