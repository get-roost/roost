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

    /// <summary>SSH: la huella del servidor no está en el known_hosts o ha cambiado.</summary>
    HostKeyUnverified,
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
    /// <paramref name="folder"/> solo sirven para nombrar las cosas en el mensaje.
    /// </summary>
    public static GitFailure Classify(int exitCode, string stderr, GitUrl? url = null, string? folder = null)
    {
        stderr ??= "";
        var detail = GitOutput.Headline(stderr);
        var host = url?.Host ?? "el servidor";
        var label = url?.Label ?? "el repositorio";
        var where = folder ?? label;

        if (ContainsAny(stderr, "Could not resolve host", "Could not resolve hostname", "Name or service not known", "Failed to connect to", "Connection timed out", "Connection refused", "No route to host", "Temporary failure in name resolution"))
            return new(GitFailureKind.HostUnreachable, $"No se encuentra {host}. Comprueba el nombre y tu conexion. Si usas un proxy, Roost toma el de Windows.", detail);

        if (ContainsAny(stderr, "SSL certificate problem", "certificate verify failed", "unable to get local issuer certificate", "schannel: next InitializeSecurityContext failed", "SEC_E_UNTRUSTED_ROOT", "CRYPT_E_NO_REVOCATION_CHECK"))
            return new(GitFailureKind.TlsError, $"No se pudo comprobar el certificado de {host}. Si tu red tiene un proxy con certificado propio, instalalo en Windows: Roost usa el almacen de certificados del sistema.", detail);

        if (ContainsAny(stderr, "REMOTE HOST IDENTIFICATION HAS CHANGED", "Host key verification failed", "No ED25519 host key is known", "No RSA host key is known", "No ECDSA host key is known"))
            return new(GitFailureKind.HostKeyUnverified, $"No se pudo verificar la identidad de {host}: su huella SSH no esta entre las conocidas o ha cambiado. No se ha conectado.", detail);

        if (ContainsAny(stderr, "Permission denied (publickey", "Permission denied (publickey,"))
            return new(GitFailureKind.KeyRejected, $"{host} no acepta ninguna clave SSH de este equipo (Permission denied (publickey)).", detail);

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
