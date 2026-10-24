using Roost.Core;
using Roost.Core.Config;
using Roost.Core.Git;
using Roost.Core.Platform;
using Roost.Core.Scanning;
using Roost.Core.Sites;

namespace Roost.Desktop.Services;

/// <summary>
/// Lo que se sabe de «Mi clave SSH» sin tocar nada: qué claves hay en la carpeta <c>.ssh</c> del
/// usuario (solo los nombres de fichero) y en qué estado está el servicio ssh-agent de Windows.
/// </summary>
public sealed record OwnSshInfo(string Folder, IReadOnlyList<string> Keys, string AgentState);

/// <summary>
/// El git de la app: qué ejecutable (el MinGit de Roost o el de <c>[git] exe</c>), sobre qué
/// <see cref="ICommandRunner"/>, y el acceso (claves SSH de Roost, servidores conocidos y tokens) con
/// el que entra cada sitio. Lo usan el asistente «Desde git…», el bloque Git del panel, Ajustes › Git
/// y la comprobación de novedades. Los tests lo construyen con un runner guionizado, un almacén de
/// credenciales en memoria y un OpenSSH de pega: nunca tocan el Administrador de credenciales, las
/// claves del usuario ni la red.
/// </summary>
public sealed class GitContext
{
    private readonly OpenSshTools? _ssh;
    private readonly string _userSsh;
    private readonly Func<string> _agentState;

    /// <param name="store">Los tokens: por defecto, el Administrador de credenciales de Windows.</param>
    /// <param name="ssh">El OpenSSH: por defecto, el del sistema.</param>
    /// <param name="userSshFolder">La carpeta <c>.ssh</c> del usuario (solo se miran los nombres).</param>
    /// <param name="agentState">Cómo saber el estado del ssh-agent (los tests ponen uno fijo).</param>
    public GitContext(RoostPaths paths, ICommandRunner runner, ICredentialStore? store = null, OpenSshTools? ssh = null,
        string? userSshFolder = null, Func<string>? agentState = null)
    {
        Paths = paths;
        Runner = runner;
        _ssh = ssh;
        Access = new GitAccessFactory(paths, store ?? DefaultStore(), runner, GitAccessFactory.DefaultHelperExe(), ssh);
        _userSsh = userSshFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        _agentState = agentState ?? ReadAgentState;
    }

    public RoostPaths Paths { get; }

    public ICommandRunner Runner { get; }

    /// <summary>Las claves, los servidores conocidos y los tokens.</summary>
    public GitAccessFactory Access { get; }

    /// <summary>El OpenSSH de Windows, o null si falta (es una característica opcional).</summary>
    public OpenSshTools? Ssh => _ssh ?? OpenSsh.TryLocate();

    private static ICredentialStore DefaultStore() =>
        OperatingSystem.IsWindows() ? new WindowsCredentialStore() : NoCredentialStore.Instance;

    /// <summary>
    /// El git que toca, o null si aún no hay ninguno (el MinGit se instala la primera vez que hace
    /// falta). Lanza <see cref="RoostException"/> si el de <c>[git] exe</c> no existe.
    /// </summary>
    public GitLocation? Locate() => GitTool.Locate(Paths, Config());

    public RoostConfig Config()
    {
        try { return RoostConfig.Load(Paths.ConfigFile); }
        catch (RoostException) { return new RoostConfig(); }
    }

    public GitClient Client(GitLocation git) => new(git.Executable, Runner, new GitEnvironment(Paths));

    /// <summary>Un SiteManager que sabe de git y del acceso de cada sitio.</summary>
    public SiteManager Manager(GitLocation git) => new(Paths, Client(git)) { Access = Access };

    /// <summary>Uno sin git, para lo que no lo necesita (quitar la clave y el token de un sitio).</summary>
    public SiteManager AccessManager() => new(Paths) { Access = Access };

    /// <summary>El SiteManager con git si ya está instalado; null si no (nada se instala desde aquí).</summary>
    public SiteManager? TryManager()
    {
        try { return Locate() is { } git ? Manager(git) : null; }
        catch (RoostException) { return null; }
    }

    /// <summary>Las claves de la carpeta <c>.ssh</c> del usuario (los ficheros <c>id_*</c> sin <c>.pub</c>) y el estado del ssh-agent. Solo lee nombres.</summary>
    public OwnSshInfo OwnSsh()
    {
        IReadOnlyList<string> keys = [];
        try
        {
            if (Directory.Exists(_userSsh))
                keys = Directory.EnumerateFiles(_userSsh, "id_*")
                    .Select(Path.GetFileName).OfType<string>()
                    .Where(n => !n.EndsWith(".pub", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(n => n.StartsWith("id_ed25519", StringComparison.Ordinal) ? 0 : 1).ThenBy(n => n, StringComparer.Ordinal)
                    .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        string agent;
        try { agent = _agentState(); }
        catch (Exception) { agent = "desconocido"; }
        return new OwnSshInfo(_userSsh, keys, agent);
    }

    /// <summary>El servicio ssh-agent de Windows: «en marcha», «parado», «deshabilitado» o «no está». Solo lee (registro y <c>sc query</c>).</summary>
    private static string ReadAgentState()
    {
        if (!OperatingSystem.IsWindows()) return "desconocido";
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\ssh-agent");
        if (key is null) return "no está";
        if (WindowsServices.StartModeOf(key.GetValue("Start")) == ServiceStartMode.Disabled) return "deshabilitado";
        return WindowsServices.IsRunning(ProcessRunner.Default.Run("sc.exe", "query ssh-agent", TimeSpan.FromSeconds(5))) ? "en marcha" : "parado";
    }
}
