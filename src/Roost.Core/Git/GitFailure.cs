using System.Text.RegularExpressions;

namespace Roost.Core.Git;

/// <summary>Por qué falló git, para decirlo con palabras del usuario y para que la app elija qué ofrecer.</summary>
public enum GitFailureKind
{
    Unknown,
    HostUnreachable,
    TlsError,
    NetworkInterrupted,

    /// <summary>Privado sin credenciales, o no existe: los proveedores no distinguen las dos cosas.</summary>
    AuthRequired,

    /// <summary>Una carpeta local que no es un repositorio.</summary>
    RepoNotFound,

    /// <summary>SSH: el servidor no acepta ninguna clave.</summary>
    KeyRejected,

    /// <summary>SSH: la huella del servidor no está en el known_hosts de Roost (ni en el del usuario, con «Mi clave SSH»).</summary>
    HostKeyUnverified,

    /// <summary>SSH: el servidor presenta otra clave que la que Roost tenía guardada. No se ha conectado.</summary>
    HostKeyChanged,

    /// <summary>SSH sin acceso elegido: por SSH siempre hace falta una clave.</summary>
    KeyRequired,

    /// <summary>SSH: la clave no se puede usar (permisos de más, contraseña, formato).</summary>
    KeyUnusable,

    /// <summary>HTTPS con un token guardado: el servidor no lo acepta (caducado, de otra cuenta, sin permiso).</summary>
    TokenRejected,

    /// <summary>HTTPS con un token guardado: existe, pero la organización exige autorizarlo (SSO) o le falta el permiso de contenido (403).</summary>
    TokenForbidden,

    /// <summary>HTTPS: lo guardado es la contraseña de la cuenta, que el proveedor ya no acepta.</summary>
    PasswordNotAccepted,

    /// <summary>Un acceso que no casa con la dirección (un token por SSH).</summary>
    AccessMismatch,
    BranchNotFound,
    FolderBusy,
    LocalChanges,
    Diverged,
    NotARepository,
    DubiousOwnership,
    TimedOut,
}

/// <summary>Un fallo de git clasificado: el tipo, el texto para el usuario (sin tildes) y la línea de git que lo explica.</summary>
public sealed partial record GitFailure(GitFailureKind Kind, string Message, string Detail)
{
    /// <summary>
    /// Clasifica el stderr de git (en inglés: el entorno lleva <c>LC_ALL=C</c>). <paramref name="url"/> y
    /// <paramref name="folder"/> solo sirven para nombrar las cosas en el mensaje. <paramref name="access"/>
    /// es cómo se intentó entrar: con un token guardado, un «pide acceso» es que lo rechazó; con la
    /// clave de Roost, se nombra la clave; con las huellas guardadas, se dice cuál cambió.
    /// </summary>
    public static GitFailure Classify(int exitCode, string stderr, GitUrl? url = null, string? folder = null, GitAccess? access = null)
    {
        stderr ??= "";
        url ??= access?.Url;
        var detail = GitOutput.Headline(stderr);
        var host = url?.Host ?? "el servidor";
        var label = url?.Label ?? "el repositorio";
        var where = folder ?? label;

        // Lo de las huellas va antes que «no se encuentra»: el aviso de ssh habla de «connection».
        if (ContainsAny(stderr, "REMOTE HOST IDENTIFICATION HAS CHANGED", "has changed and you have requested strict checking"))
            return HostKeyChanged(url, stderr, access, detail);

        if (ContainsAny(stderr, "Host key verification failed", "No ED25519 host key is known", "No RSA host key is known", "No ECDSA host key is known"))
        {
            var token = url?.Host is { } h ? KnownHosts.HostToken(h, url.Port) : host;
            return new(GitFailureKind.HostKeyUnverified, $"Roost no conoce la huella de {token}, asi que no se ha conectado. Comprueba la huella y aceptala con: roost git hosts add {token}", detail);
        }

        if (ContainsAny(stderr, "Could not resolve host", "Could not resolve hostname", "Name or service not known", "Failed to connect to", "Connection timed out", "Connection refused", "No route to host", "Temporary failure in name resolution"))
            return new(GitFailureKind.HostUnreachable, $"No se encuentra {host}. Comprueba el nombre y tu conexion. Si usas un proxy, Roost toma el de Windows.", detail);

        if (ContainsAny(stderr, "SSL certificate problem", "certificate verify failed", "unable to get local issuer certificate", "schannel: next InitializeSecurityContext failed", "SEC_E_UNTRUSTED_ROOT", "CRYPT_E_NO_REVOCATION_CHECK"))
            return new(GitFailureKind.TlsError, $"No se pudo comprobar el certificado de {host}. Si tu red tiene un proxy con certificado propio, instalalo en Windows: Roost usa el almacen de certificados del sistema.", detail);

        if (ContainsAny(stderr, "UNPROTECTED PRIVATE KEY FILE", "bad permissions"))
            return new(GitFailureKind.KeyUnusable, $"OpenSSH no usa la clave{KeyOf(access)} porque otros usuarios de este equipo pueden leerla. Quitale los permisos de mas (solo tu usuario) o vuelve a crearla con roost git key new.", detail);

        if (ContainsAny(stderr, "incorrect passphrase supplied", "Load key") && ContainsAny(stderr, "passphrase", "invalid format", "error in libcrypto"))
            return new(GitFailureKind.KeyUnusable, $"No se puede usar la clave{KeyOf(access)}: tiene contrasena o no tiene un formato de OpenSSH. Roost no puede preguntar la contrasena (git corre sin terminal).", detail);

        if (ContainsAny(stderr, "Permission denied (publickey", "Permission denied (publickey,"))
            return KeyRejected(url, access, detail);

        // La clave entra en el servidor pero no ve el repositorio: es la deploy key de otro.
        if (access?.Kind == Sites.SiteGit.AccessKey && url?.Scheme == GitScheme.Ssh && ContainsAny(stderr, "Repository not found", "does not appear to be a git repository", "The project you were looking for could not be found"))
            return new(GitFailureKind.KeyRejected, $"{host} acepta la clave de {access.KeyName}, pero con ella no se ve {label}: seguramente esta en las deploy keys de otro repositorio. Ponla en las de {label}" + (url.DeployKeysUrl is { } keys ? $" ({keys})." : "."), detail);

        if (access is { HasCredential: true } && url?.Scheme == GitScheme.Https)
        {
            if (ContainsAny(stderr, "SAML SSO", "SAML enforcement", "returned error: 403", "Permission to ", "is not authorized to access this resource"))
                return new(GitFailureKind.TokenForbidden, $"El token existe pero la organizacion {url.Owner} exige autorizarlo (SSO) o no tiene el permiso de contenido ({url.TokenPermission}). Abrelo en {url.ProviderName} y autorizalo.", detail);

            var rejected = ContainsAny(stderr, "terminal prompts disabled", "could not read Username", "could not read Password", "Authentication failed", "HTTP Basic: Access denied", "Invalid username or token", "Invalid username or password", "Invalid credentials", "Repository not found", "The project you were looking for could not be found", "returned error: 401", "Bad credentials");
            // GitHub contesta «Password authentication is not supported» a cualquier credencial mala,
            // también a un token caducado: que era una contraseña se sabe por la forma de lo guardado.
            if ((rejected && access.CredentialLooksLikePassword) || ContainsAny(stderr, "Support for password authentication was removed", "stopped supporting account passwords", "App passwords are no longer"))
                return new(GitFailureKind.PasswordNotAccepted, $"{url.ProviderName} ya no acepta la contrasena de la cuenta: hace falta un token" + (url.NewTokenUrl is { } create ? $". Crealo en {create} (permiso {url.TokenPermission})." : "."), detail);

            if (rejected)
                return new(GitFailureKind.TokenRejected, $"{url.ProviderName} no acepta ese token. Puede haber caducado, ser de otra cuenta o no tener permiso en {label} (necesita {url.TokenPermission}). Crea otro y sustituyelo.", detail);
        }

        if (ContainsAny(stderr, "already exists and is not an empty directory"))
        {
            var busy = FolderBusyPattern().Match(stderr) is { Success: true } m ? m.Groups["path"].Value : where;
            return new(GitFailureKind.FolderBusy, $"{busy} ya existe y no esta vacia. Elige otra carpeta o, si ya es ese proyecto, registralo con roost site add: Roost detectara su .git.", detail);
        }

        if (BranchPattern().Match(stderr) is { Success: true } branch)
            return new(GitFailureKind.BranchNotFound, $"En {label} no hay ninguna rama '{branch.Groups["branch"].Value}'.", detail);

        if (ContainsAny(stderr, "Your local changes to the following files would be overwritten", "Please commit your changes or stash them", "You have unstaged changes", "untracked working tree files would be overwritten", "Your local changes would be overwritten"))
        {
            var files = LocalChangeFiles(stderr);
            var which = files.Count == 0 ? "" : $" ({string.Join(", ", files.Take(5))}{(files.Count > 5 ? $" y {files.Count - 5} mas" : "")})";
            return new(GitFailureKind.LocalChanges, $"Hay cambios sin guardar en {where}{which}. Roost no actualiza ni cambia de rama por encima de tu trabajo. Guardalos (commit o stash) y vuelve a intentarlo.", detail);
        }

        if (ContainsAny(stderr, "Not possible to fast-forward", "can't be fast-forwarded", "Need to specify how to reconcile divergent branches", "have diverged"))
            return new(GitFailureKind.Diverged, $"La rama de {where} tiene commits que no estan en el remoto, asi que no se puede actualizar sin fusionar. Hazlo desde la terminal (git pull --rebase, o merge).", detail);

        if (ContainsAny(stderr, "dubious ownership"))
            return new(GitFailureKind.DubiousOwnership, $"git rechaza {where} porque la carpeta pertenece a otro usuario de Windows (dubious ownership). Cambia el propietario de la carpeta a tu usuario.", detail);

        if (ContainsAny(stderr, "not a git repository"))
            return new(GitFailureKind.NotARepository, $"{where} no es un repositorio git (no tiene .git).", detail);

        if (ContainsAny(stderr, "does not appear to be a git repository") && url is { IsLocal: true })
            return new(GitFailureKind.RepoNotFound, $"No hay ningun repositorio git en {url.Path}.", detail);

        if (ContainsAny(stderr, "terminal prompts disabled", "could not read Username", "could not read Password", "Authentication failed", "HTTP Basic: Access denied", "Invalid username or token", "Invalid username or password", "Repository not found", "The project you were looking for could not be found", "returned error: 401", "returned error: 403", "does not appear to be a git repository", "Could not read from remote repository", "Permission denied"))
        {
            var who = url?.Provider is GitProvider.Other or GitProvider.Local or null ? "el servidor" : url.ProviderName;
            return new(GitFailureKind.AuthRequired, $"{label} pide acceso, o no existe: {who} no distingue entre las dos cosas.", detail);
        }

        if (ContainsAny(stderr, "RPC failed", "early EOF", "hung up unexpectedly", "Connection reset", "transfer closed with outstanding read data", "Operation timed out", "Recv failure", "Send failure"))
            return new(GitFailureKind.NetworkInterrupted, $"Se corto la conexion con {host} a medias. Prueba de nuevo.", detail);

        var tail = detail.Length == 0 ? "" : $": {detail}";
        return new(GitFailureKind.Unknown, $"git ha fallado (codigo {exitCode}){tail}", detail);
    }

    /// <summary>Una orden que no contestó a tiempo.</summary>
    public static GitFailure TimedOut(TimeSpan limit, GitUrl? url = null) =>
        new(GitFailureKind.TimedOut, $"{url?.Host ?? "git"} no ha contestado en {(int)limit.TotalSeconds} s y se ha cortado. Comprueba tu conexion y prueba de nuevo.", "");

    /// <summary>Cambios locales que detectó Roost antes de pedirle nada a git (<see cref="RepoStatus.Changed"/>).</summary>
    public static GitFailure LocalChanges(string where, IReadOnlyList<string> files)
    {
        var which = files.Count == 0 ? "" : $" ({string.Join(", ", files.Take(5))}{(files.Count > 5 ? $" y {files.Count - 5} mas" : "")})";
        return new(GitFailureKind.LocalChanges, $"Hay cambios sin guardar en {files.Count} fichero(s) de {where}{which}. Roost no actualiza ni cambia de rama por encima de tu trabajo. Guardalos (commit o stash) y vuelve a intentarlo.", "");
    }

    /// <summary>Una rama que no está en el remoto, con las que sí hay.</summary>
    public static GitFailure BranchNotFound(string label, string branch, IReadOnlyList<string> branches)
    {
        var list = branches.Count == 0 ? "" : $" Las que hay: {string.Join(", ", branches.Take(12))}{(branches.Count > 12 ? "..." : "")}.";
        return new(GitFailureKind.BranchNotFound, $"En {label} no hay ninguna rama '{branch}'.{list}", "");
    }

    /// <summary>Una dirección SSH sin acceso elegido: por SSH siempre hace falta una clave.</summary>
    public static GitFailure KeyRequired(GitUrl url) =>
        new(GitFailureKind.KeyRequired, $"{url.Label} va por SSH, y por SSH siempre hace falta una clave: la de Roost para este sitio (--ssh), un fichero de clave (--ssh-key) o las tuyas de ~/.ssh y el ssh-agent (--own-ssh).", "");

    /// <summary>Un token con una dirección SSH de un servidor del que Roost no sabe la https.</summary>
    public static GitFailure TokenNeedsHttps(GitUrl url) =>
        new(GitFailureKind.AccessMismatch, $"Un token solo vale por HTTPS, y {url.Original} es SSH. Pon la direccion https:// del repositorio o entra con una clave SSH.", "");

    /// <summary>Una clave SSH con una dirección https de un servidor del que Roost no sabe la SSH.</summary>
    public static GitFailure KeyNeedsSsh(GitUrl url) =>
        new(GitFailureKind.AccessMismatch, $"Una clave SSH solo vale por SSH, y {url.Original} es https. Pon la direccion SSH del repositorio (git@{url.Host ?? "servidor"}:grupo/repo.git) o entra con un token.", "");

    /// <summary>
    /// La huella de un servidor que no coincide con la guardada (texto 8 del diseño): la de antes,
    /// de las que tenía Roost del mismo algoritmo, y la que presenta ahora, del aviso de ssh.
    /// </summary>
    public static GitFailure HostKeyChanged(GitUrl? url, string stderr, GitAccess? access, string detail = "")
    {
        var host = url?.Host is { } h ? KnownHosts.HostToken(h, url.Port) : "el servidor";
        var sent = SentKeyPattern().Match(stderr);
        string change = "";
        if (sent.Success)
        {
            var algorithm = sent.Groups["alg"].Value;
            var now = sent.Groups["fp"].Value.TrimEnd('.');
            var before = access?.HostKeys.FirstOrDefault(e => e.Key.Algorithm == algorithm)?.Key.Fingerprint;
            change = before is null ? $" (ahora presenta {algorithm} {now})" : $" ({algorithm} {before} -> {now})";
        }
        var builtIn = access?.PublishedHost is { } published
            ? $" Para {published.Host}, Roost trae la que publica {GitUrl.NameOf(published.Provider)}: si la ha cambiado, actualiza Roost."
            : "";
        return new(GitFailureKind.HostKeyChanged,
            $"La huella de {host} ha cambiado respecto a la que Roost tenia guardada{change}. Puede ser que hayan cambiado la clave del servidor o que alguien se este haciendo pasar por el. "
            + $"No se ha conectado. Si quien lo administra confirma el cambio, olvida la huella guardada (roost git hosts remove {host}) y vuelve a comprobar.{builtIn}",
            detail.Length > 0 ? detail : GitOutput.Headline(stderr));
    }

    private static GitFailure KeyRejected(GitUrl? url, GitAccess? access, string detail)
    {
        var host = url?.Host ?? "el servidor";
        var label = url?.Label ?? "el repositorio";
        if (access?.Kind == Sites.SiteGit.AccessKey && access.KeyName is { } key)
        {
            var reuse = url?.Provider switch
            {
                GitProvider.GitHub => " (y en ese repositorio, no en otro: GitHub no permite reutilizarlas)",
                GitProvider.Bitbucket => " (como clave de acceso del repositorio: Bitbucket no deja usar la misma clave en una cuenta)",
                _ => "",
            };
            var where = url?.DeployKeysUrl is { } page ? $" {page}" : "";
            return new(GitFailureKind.KeyRejected, $"{host} no acepta la clave de {key}. Asegurate de haber pegado la clave publica en las deploy keys de {label}{reuse}.{where}".TrimEnd(), detail);
        }
        if (access?.Kind == Sites.SiteGit.AccessOwn)
            return new(GitFailureKind.KeyRejected, $"{host} no acepta ninguna de tus claves SSH (las de ~/.ssh y las del ssh-agent de Windows). Pruebalo en tu terminal: ssh -T git@{host}", detail);
        return new(GitFailureKind.KeyRejected, $"{host} no acepta ninguna clave SSH de este equipo (Permission denied (publickey)).", detail);
    }

    private static string KeyOf(GitAccess? access) => access?.KeyName is { } key ? $" de {key}" : "";

    private static bool ContainsAny(string text, params string[] needles) =>
        needles.Any(n => text.Contains(n, StringComparison.OrdinalIgnoreCase));

    // Las líneas con tabulador que siguen a «the following files would be overwritten».
    private static List<string> LocalChangeFiles(string stderr)
    {
        var files = new List<string>();
        var collecting = false;
        foreach (var line in stderr.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.Contains("following files would be overwritten", StringComparison.Ordinal)) { collecting = true; continue; }
            if (!collecting) continue;
            if (trimmed.StartsWith('\t')) files.Add(trimmed.Trim());
            else collecting = false;
        }
        return files;
    }

    // «The fingerprint for the ED25519 key sent by the remote host is\nSHA256:….»
    [GeneratedRegex(@"fingerprint for the (?<alg>[A-Z0-9]+) key sent by the remote host is\s+(?<fp>SHA256:[A-Za-z0-9+/=]+)")]
    private static partial Regex SentKeyPattern();

    [GeneratedRegex(@"destination path '(?<path>[^']+)' already exists")]
    private static partial Regex FolderBusyPattern();

    [GeneratedRegex(@"Remote branch (?<branch>\S+) not found in upstream|pathspec '(?<branch>[^']+)' did not match|invalid reference: (?<branch>\S+)|couldn't find remote ref (?<branch>\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex BranchPattern();
}

/// <summary>Un fallo de git como error para el usuario: el mensaje es el de <see cref="GitFailure"/>.</summary>
public sealed class GitException(GitFailure failure) : RoostException(failure.Message)
{
    public GitFailure Failure { get; } = failure;
}
