using Roost.Core.Platform;
using Roost.Core.Scanning;
using Roost.Core.Sites;

namespace Roost.Core.Git;

/// <summary>
/// Cómo entra git en el remoto en una invocación: la configuración que se le añade solo a ella
/// (<c>GIT_CONFIG_COUNT</c>: el helper de credenciales de Roost) y el <c>GIT_SSH_COMMAND</c>. Lleva
/// además lo que hace falta para explicar un fallo (<see cref="GitFailure.Classify"/>): qué tipo de
/// acceso era, con qué clave, si había token y qué huellas tenía guardadas Roost del servidor.
/// Ningún secreto: el token lo pide git al helper por una tubería.
/// </summary>
public sealed record GitAccess
{
    /// <summary>Sin nada de Roost: el entorno aislado de siempre.</summary>
    public static GitAccess None { get; } = new();

    /// <summary>La dirección para la que se construyó (para nombrar las cosas en un fallo de pull o fetch, que solo ven la carpeta).</summary>
    public GitUrl? Url { get; init; }

    /// <summary><see cref="SiteGit.AccessNone"/>, <see cref="SiteGit.AccessToken"/>, <see cref="SiteGit.AccessKey"/> o <see cref="SiteGit.AccessOwn"/>.</summary>
    public string Kind { get; init; } = SiteGit.AccessNone;

    /// <summary>La clave SSH de Roost, con <see cref="SiteGit.AccessKey"/>.</summary>
    public string? KeyName { get; init; }

    /// <summary>El target del token que usará git (el más concreto que hay para la dirección), si hay alguno.</summary>
    public string? CredentialTarget { get; init; }

    /// <summary>true si hay un token guardado que valga para la dirección: entonces un «pide acceso» es que lo rechazó.</summary>
    public bool HasCredential => CredentialTarget is not null;

    /// <summary>Pares <c>clave=valor</c> de configuración solo para esta invocación.</summary>
    public IReadOnlyList<(string Key, string Value)> Config { get; init; } = [];

    /// <summary>El <c>GIT_SSH_COMMAND</c>; null = el de <see cref="GitEnvironment.DefaultSshCommand"/>.</summary>
    public string? SshCommand { get; init; }

    /// <summary>Las huellas que tenía Roost del servidor SSH al lanzar git (para decir cuál cambió).</summary>
    public IReadOnlyList<KnownHostEntry> HostKeys { get; init; } = [];

    /// <summary>El servidor de serie (GitHub, GitLab, Bitbucket), si lo es.</summary>
    public PublishedHost? PublishedHost { get; init; }
}

/// <summary>
/// Construye el <see cref="GitAccess"/> de cada operación a partir del tipo de acceso de un sitio
/// (<see cref="SiteGit"/>) o de lo que se elige al clonar. Reúne las claves (<see cref="SshKeys"/>),
/// los servidores conocidos (<see cref="KnownHosts"/>) y los tokens (<see cref="GitCredentials"/>).
/// <list type="bullet">
/// <item>https: siempre con el helper de Roost (<c>roost git-credential</c>), sea cual sea el tipo:
/// si no hay token para esa dirección, no contesta nada y git sigue como público; así un token que
/// se guarde después vale sin tocar el sitio. Antes, un <c>credential.helper</c> vacío borra
/// cualquier otro (también el de un <c>.git/config</c>).</item>
/// <item>ssh con la clave de Roost: <see cref="OpenSsh.KeyCommand"/>; con «Mi clave SSH»:
/// <see cref="OpenSsh.OwnCommand"/>; sin acceso no se lanza nada (por SSH siempre hace falta una
/// clave: <see cref="GitFailureKind.KeyRequired"/>).</item>
/// <item>una carpeta local: nada.</item>
/// </list>
/// </summary>
public sealed class GitAccessFactory
{
    private readonly OpenSshTools? _ssh;

    public GitAccessFactory(RoostPaths paths, ICredentialStore store, ICommandRunner runner, string? helperExe = null, OpenSshTools? ssh = null, string? credentialPrefix = null)
    {
        _ssh = ssh;
        HelperExe = helperExe;
        Keys = new SshKeys(paths, runner, ssh);
        KnownHosts = new KnownHosts(new GitEnvironment(paths).KnownHostsFile, runner, ssh);
        Credentials = new GitCredentials(store, credentialPrefix);
    }

    /// <summary>
    /// La de la CLI y la app: el Administrador de credenciales de Windows (en otro sistema, ninguno),
    /// el <c>roost.exe</c> que va junto a quien llama como helper y el OpenSSH del sistema.
    /// </summary>
    public static GitAccessFactory CreateDefault(RoostPaths paths, ICommandRunner runner) =>
        new(paths, OperatingSystem.IsWindows() ? new WindowsCredentialStore() : NoCredentialStore.Instance, runner, DefaultHelperExe());

    /// <summary><c>roost.exe</c> junto al ensamblado que corre (la CLI, o la app, que lo lleva a su lado).</summary>
    public static string DefaultHelperExe() =>
        Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "roost.exe" : "roost");

    public string? HelperExe { get; }

    public SshKeys Keys { get; }

    public KnownHosts KnownHosts { get; }

    public GitCredentials Credentials { get; }

    /// <summary>El OpenSSH que se usa (lanza <see cref="RoostException"/> si falta).</summary>
    public OpenSshTools Ssh => _ssh ?? OpenSsh.Locate();

    /// <summary>El helper tal como lo lanza git (por <c>sh</c>): <c>!'C:/…/roost.exe' git-credential</c>.</summary>
    public string? HelperCommand => HelperExe is null ? null : "!" + OpenSsh.ShellQuote(OpenSsh.ForwardSlashes(HelperExe)) + " git-credential";

    /// <summary>El acceso del repositorio de un sitio.</summary>
    public GitAccess For(SiteGit git)
    {
        if (!GitUrl.TryParse(git.Remote, out var url, out var why)) throw new RoostException($"El remoto del sitio ({GitLog.Redact(git.Remote)}) no vale: {why}");
        return For(url, git.Access, git.KeyName);
    }

    /// <summary>
    /// El acceso para <paramref name="url"/> con el tipo <paramref name="kind"/>. Lanza
    /// <see cref="GitException"/> si no casa con la dirección (SSH sin clave, token por SSH) y
    /// <see cref="RoostException"/> si falta la clave o el OpenSSH de Windows.
    /// </summary>
    public GitAccess For(GitUrl url, string kind, string? keyName = null)
    {
        switch (url.Scheme)
        {
            case GitScheme.Local:
                return new GitAccess { Url = url, Kind = kind, KeyName = keyName };

            case GitScheme.Https:
            {
                var found = Credentials.Find(url);
                return new GitAccess
                {
                    Url = url,
                    Kind = kind,
                    CredentialTarget = found?.Target,
                    Config = HelperCommand is { } helper ? [("credential.helper", ""), ("credential.helper", helper)] : [],
                };
            }

            default:
            {
                if (kind == SiteGit.AccessToken) throw new GitException(GitFailure.TokenNeedsHttps(url));
                if (kind is not (SiteGit.AccessKey or SiteGit.AccessOwn)) throw new GitException(GitFailure.KeyRequired(url));

                var ssh = Ssh;
                KnownHosts.EnsurePublished();
                string command;
                if (kind == SiteGit.AccessKey)
                {
                    var name = keyName ?? throw new RoostException("Falta el nombre de la clave SSH de Roost.");
                    if (!Keys.Exists(name))
                        throw new RoostException($"No esta la clave SSH de Roost de {name} ({Keys.PrivateFile(name)}). Creala con roost git key new {name} y ponla como deploy key de {url.Label}.");
                    command = OpenSsh.KeyCommand(ssh, Keys.PrivateFile(name), KnownHosts.FilePath);
                }
                else
                {
                    command = OpenSsh.OwnCommand(ssh, KnownHosts.FilePath);
                }
                return new GitAccess
                {
                    Url = url,
                    Kind = kind,
                    KeyName = kind == SiteGit.AccessKey ? keyName : null,
                    SshCommand = command,
                    HostKeys = url.Host is null ? [] : KnownHosts.For(url.Host, url.Port),
                    PublishedHost = url.Host is null ? null : KnownHosts.PublishedFor(url.Host, url.Port),
                };
            }
        }
    }
}
