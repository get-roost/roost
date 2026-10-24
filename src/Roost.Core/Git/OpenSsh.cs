namespace Roost.Core.Git;

/// <summary>Los programas del cliente OpenSSH que usa Roost.</summary>
public sealed record OpenSshTools(string Directory)
{
    public string Ssh => Tool("ssh");

    public string SshKeygen => Tool("ssh-keygen");

    public string SshKeyscan => Tool("ssh-keyscan");

    private string Tool(string name) => Path.Combine(Directory, OperatingSystem.IsWindows() ? name + ".exe" : name);
}

/// <summary>
/// El OpenSSH de Windows (<c>%SystemRoot%\System32\OpenSSH</c>, de serie en Windows 10 1809 y en
/// Windows 11) y los <c>GIT_SSH_COMMAND</c> de cada acceso. Siempre ese, con su ruta: el
/// <c>ssh</c> que trae MinGit (MSYS2, en <c>usr/bin</c>) es el que encontraría git sin ruta, y no
/// habla con el ssh-agent de Windows.
/// </summary>
public static class OpenSsh
{
    /// <summary>
    /// Lo que se espera a abrir la conexión TCP: sin él, un servidor que no contesta deja a git
    /// esperando lo que diga el sistema.
    /// </summary>
    public const int ConnectTimeoutSeconds = 20;

    /// <summary>Donde está el OpenSSH del sistema: <c>System32\OpenSSH</c> en Windows (desde un proceso de 32 bits, por Sysnative), <c>/usr/bin</c> en el resto.</summary>
    public static string DefaultDirectory()
    {
        if (!OperatingSystem.IsWindows()) return "/usr/bin";
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var system = Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess ? "Sysnative" : "System32";
        return Path.Combine(windows, system, "OpenSSH");
    }

    /// <summary>El texto cuando falta: en Windows es una característica opcional que se puede haber quitado.</summary>
    public static string MissingMessage(string directory, IEnumerable<string> missing) =>
        OperatingSystem.IsWindows()
            ? $"Falta el cliente OpenSSH de Windows (no estan {string.Join(", ", missing)} en {directory}). Es una caracteristica opcional de Windows: "
              + "Configuracion > Sistema > Caracteristicas opcionales > Agregar una caracteristica > Cliente OpenSSH, o en PowerShell como administrador: "
              + "Add-WindowsCapability -Online -Name OpenSSH.Client~~~~0.0.1.0. Sin el, entra con un token HTTPS."
            : $"Falta el cliente OpenSSH (no estan {string.Join(", ", missing)} en {directory}). Instala el paquete openssh-client de tu sistema.";

    /// <summary>El OpenSSH de <paramref name="directory"/> (por defecto, el del sistema), o null si le falta algo.</summary>
    public static OpenSshTools? TryLocate(string? directory = null)
    {
        var tools = new OpenSshTools(directory ?? DefaultDirectory());
        return Missing(tools).Count == 0 ? tools : null;
    }

    /// <summary>Como <see cref="TryLocate"/>, pero lanza <see cref="RoostException"/> con <see cref="MissingMessage"/> si falta.</summary>
    public static OpenSshTools Locate(string? directory = null)
    {
        var tools = new OpenSshTools(directory ?? DefaultDirectory());
        var missing = Missing(tools);
        if (missing.Count > 0) throw new RoostException(MissingMessage(tools.Directory, missing));
        return tools;
    }

    private static List<string> Missing(OpenSshTools tools) =>
        new[] { tools.Ssh, tools.SshKeygen, tools.SshKeyscan }.Where(f => !File.Exists(f)).Select(Path.GetFileName).OfType<string>().ToList();

    /// <summary>
    /// El ssh con la clave de Roost de un sitio: solo esa clave (<c>IdentitiesOnly</c>, sin ssh-agent
    /// ni la configuración de <c>~/.ssh</c>: <c>-F none</c>, para que nunca entre con una clave
    /// personal del usuario en lugar de la deploy key), sin preguntas, y solo contra el
    /// <c>known_hosts</c> de Roost con <c>StrictHostKeyChecking=yes</c>: un servidor desconocido o
    /// con la huella cambiada no conecta.
    /// </summary>
    public static string KeyCommand(OpenSshTools tools, string keyFile, string knownHostsFile) => string.Join(' ',
        ShellQuote(ForwardSlashes(tools.Ssh)),
        "-F", "none",
        "-o", ShellQuote("IdentityFile=" + ConfigPath(keyFile)),
        "-o", "IdentitiesOnly=yes",
        "-o", "IdentityAgent=none",
        "-o", "BatchMode=yes",
        "-o", "StrictHostKeyChecking=yes",
        "-o", ShellQuote("UserKnownHostsFile=" + ConfigPath(knownHostsFile)),
        "-o", $"ConnectTimeout={ConnectTimeoutSeconds}");

    /// <summary>
    /// «Mi clave SSH»: lo que tenga el usuario, como en su terminal (su <c>~/.ssh/config</c>, sus
    /// claves y el ssh-agent de Windows, que solo atiende al OpenSSH de Windows), pero sin preguntas
    /// (<c>BatchMode</c>) y con <c>StrictHostKeyChecking=yes</c> contra el <c>known_hosts</c> de Roost
    /// (las huellas de serie) y luego los del usuario. Así funciona lo que ya funcionaba en su
    /// terminal y un servidor desconocido no se acepta solo: con <c>yes</c>, ssh nunca escribe en el
    /// <c>known_hosts</c> del usuario.
    /// </summary>
    public static string OwnCommand(OpenSshTools tools, string knownHostsFile) => string.Join(' ',
        ShellQuote(ForwardSlashes(tools.Ssh)),
        "-o", "BatchMode=yes",
        "-o", "StrictHostKeyChecking=yes",
        "-o", ShellQuote($"UserKnownHostsFile={ConfigPath(knownHostsFile)} ~/.ssh/known_hosts ~/.ssh/known_hosts2"),
        "-o", $"ConnectTimeout={ConnectTimeoutSeconds}");

    /// <summary>
    /// Entrecomilla para el <c>sh</c> con el que git lanza <c>GIT_SSH_COMMAND</c> y los helpers
    /// (MinGit trae el dash de MSYS2): comillas simples, en las que nada es especial, y cada
    /// <c>'</c> como <c>'\''</c>. Probado con espacios, <c>'</c>, <c>$</c> y tildes en la ruta.
    /// </summary>
    public static string ShellQuote(string value) => "'" + value.Replace("'", @"'\''") + "'";

    /// <summary>
    /// Una ruta como valor de una opción de ssh (<c>-o Clave=valor</c>, que ssh lee como una línea de
    /// su configuración): con barras normales, entre comillas dobles (si no, un espacio la partiría
    /// en dos ficheros) y con cada <c>%</c> doblado (ssh expande <c>%d</c>, <c>%u</c>… en
    /// <c>IdentityFile</c> y <c>UserKnownHostsFile</c>). Por eso la clave va con
    /// <c>-o IdentityFile=</c> y no con <c>-i</c>: <c>-i</c> comprueba la ruta tal cual y luego la
    /// expande, así que una con <c>%</c> no funciona de ninguna de las dos maneras.
    /// </summary>
    public static string ConfigPath(string path) => "\"" + ForwardSlashes(path).Replace("%", "%%") + "\"";

    public static string ForwardSlashes(string path) => path.Replace('\\', '/');
}
