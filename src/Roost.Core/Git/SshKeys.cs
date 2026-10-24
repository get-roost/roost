using Roost.Core.Install;
using Roost.Core.Platform;
using Roost.Core.Scanning;
using Roost.Core.Sites;
using PrivateAcl = Roost.Core.Platform.PrivateFile;

namespace Roost.Core.Git;

/// <summary>Una clave SSH de Roost: dónde está y lo que se enseña de ella (nunca la parte privada).</summary>
/// <param name="PublicKey">La línea de la clave pública, para pegarla como deploy key.</param>
public sealed record SshKeyInfo(string Name, string PrivateFile, string PublicFile, string PublicKey, string Type, string Fingerprint)
{
    public string Algorithm => KnownHosts.AlgorithmOf(Type);
}

/// <summary>
/// Las claves SSH de los sitios, una por sitio en <c>state/git/keys/&lt;sitio&gt;</c> (y su
/// <c>.pub</c>). Son deploy keys: ed25519, sin contraseña (como Plesk, Forge o Coolify, que lo
/// compensan igual: solo lectura y una por repositorio) y legibles solo por el usuario (la carpeta
/// y cada fichero, con la ACL de <see cref="PrivateFile"/>; OpenSSH rechaza una clave que otros
/// puedan leer). Las crea el <c>ssh-keygen</c> del OpenSSH de Windows.
/// </summary>
public sealed class SshKeys(RoostPaths paths, ICommandRunner runner, OpenSshTools? ssh = null)
{
    private static readonly TimeSpan KeygenTimeout = TimeSpan.FromSeconds(30);

    public string Directory => Path.Combine(paths.GitState, "keys");

    public string PrivateFile(string name) => Path.Combine(Directory, Validate(name));

    public string PublicFile(string name) => PrivateFile(name) + ".pub";

    public bool Exists(string name) => File.Exists(PrivateFile(name));

    /// <summary>El comentario de la clave pública: así se reconoce en la lista de deploy keys del proveedor.</summary>
    public static string Comment(string name) => $"roost:{name}@{Environment.MachineName}";

    /// <summary>La clave del sitio, o null si no tiene (o su .pub no se entiende).</summary>
    public SshKeyInfo? Find(string name)
    {
        var priv = PrivateFile(name);
        var pub = PublicFile(name);
        if (!File.Exists(priv) || !File.Exists(pub)) return null;
        return Describe(name, priv, pub, File.ReadAllText(pub).Trim());
    }

    /// <summary>Todas las claves de Roost, por nombre.</summary>
    public IReadOnlyList<SshKeyInfo> List()
    {
        if (!System.IO.Directory.Exists(Directory)) return [];
        return System.IO.Directory.EnumerateFiles(Directory, "*.pub")
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .Where(Site.IsValidName)
            .Select(Find)
            .OfType<SshKeyInfo>()
            .OrderBy(k => k.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Crea la clave ed25519 del sitio (sin contraseña). Se genera con un nombre temporal dentro de
    /// la carpeta protegida y se renombra al acabar: si algo falla, no queda media clave. Lanza
    /// <see cref="RoostException"/> si ya tiene una (quitarla o no es cosa de quien llama).
    /// </summary>
    public async Task<SshKeyInfo> GenerateAsync(string name, CancellationToken cancellation)
    {
        if (Exists(name))
            throw new RoostException($"{name} ya tiene una clave SSH de Roost ({PrivateFile(name)}). Para crear otra, quitala antes: roost git key remove {name}");
        var tools = ssh ?? OpenSsh.Locate();
        var temp = NewTemp();
        try
        {
            // stdin vacío: si ssh-keygen preguntara algo (sobrescribir), no se queda esperando.
            var result = await runner.RunAsync(new CommandSpec(tools.SshKeygen,
                ["-q", "-t", "ed25519", "-N", "", "-C", Comment(name), "-f", temp], Timeout: KeygenTimeout, Stdin: ""), null, cancellation);
            if (!result.Succeeded || !File.Exists(temp) || !File.Exists(temp + ".pub"))
                throw new RoostException($"ssh-keygen no ha podido crear la clave de {name}{Tail(result)}");
            return Install(name, temp, File.ReadAllText(temp + ".pub").Trim());
        }
        finally
        {
            InstallFiles.TryDeleteFile(temp);
            InstallFiles.TryDeleteFile(temp + ".pub");
        }
    }

    /// <summary>
    /// Usa como clave del sitio una que ya tiene el usuario (<c>--ssh-key</c>): se copia a la carpeta
    /// de Roost, solo legible por el usuario, y su original no se toca. Tiene que ser una clave
    /// privada de OpenSSH sin contraseña (Roost no puede preguntarla: git corre sin terminal).
    /// </summary>
    public async Task<SshKeyInfo> ImportAsync(string name, string sourceFile, CancellationToken cancellation)
    {
        if (Exists(name))
            throw new RoostException($"{name} ya tiene una clave SSH de Roost ({PrivateFile(name)}). Quitala antes (roost git key remove {name}) o usala con --ssh.");
        var source = Path.GetFullPath(sourceFile);
        if (!File.Exists(source)) throw new RoostException($"No existe el fichero de clave {source}.");
        var head = File.ReadLines(source).FirstOrDefault()?.Trim() ?? "";
        if (head.StartsWith("ssh-", StringComparison.Ordinal) || head.StartsWith("ecdsa-", StringComparison.Ordinal))
            throw new RoostException($"{source} es una clave publica: pasa la privada (el mismo fichero sin .pub).");
        if (head.StartsWith("PuTTY-User-Key-File", StringComparison.Ordinal))
            throw new RoostException($"{source} es una clave de PuTTY (.ppk): exportala en formato OpenSSH con PuTTYgen (Conversions > Export OpenSSH key).");

        var tools = ssh ?? OpenSsh.Locate();
        var temp = NewTemp();
        try
        {
            File.Copy(source, temp);
            PrivateAcl.Restrict(temp);
            // -y saca la pública de la privada; con -P "" falla si la clave tiene contraseña.
            var result = await runner.RunAsync(new CommandSpec(tools.SshKeygen, ["-y", "-P", "", "-f", temp], Timeout: KeygenTimeout, Stdin: ""), null, cancellation);
            if (!result.Succeeded)
            {
                var why = result.Stderr.Contains("passphrase", StringComparison.OrdinalIgnoreCase)
                    ? $"{source} tiene contrasena: Roost no puede preguntarla (git corre sin terminal). Usa una clave sin contrasena (una deploy key) o --own-ssh con el ssh-agent de Windows."
                    : $"{source} no es una clave privada de OpenSSH que se pueda leer{Tail(result)}";
                throw new RoostException(why);
            }
            var publicKey = GitOutput.Lines(result.Stdout).FirstOrDefault()?.Trim() ?? "";
            if (publicKey.Split(' ') is not [var type, _, ..] || !KnownHosts.IsSupportedType(type))
                throw new RoostException($"{source} no es una clave SSH que Roost sepa usar (ed25519, ecdsa o rsa).");
            return Install(name, temp, publicKey);
        }
        finally
        {
            InstallFiles.TryDeleteFile(temp);
        }
    }

    /// <summary>Borra la clave del sitio (las dos partes). true si había algo que borrar.</summary>
    public bool Remove(string name)
    {
        var priv = PrivateFile(name);
        var pub = PublicFile(name);
        var existed = File.Exists(priv) || File.Exists(pub);
        foreach (var file in new[] { priv, pub })
        {
            if (!File.Exists(file)) continue;
            File.SetAttributes(file, FileAttributes.Normal);
            File.Delete(file);
        }
        return existed;
    }

    private SshKeyInfo Install(string name, string tempPrivate, string publicKey)
    {
        var priv = PrivateFile(name);
        var pub = PublicFile(name);
        File.Move(tempPrivate, priv);
        PrivateAcl.Restrict(priv);
        File.WriteAllText(pub, publicKey + "\n");
        return Describe(name, priv, pub, publicKey);
    }

    private string NewTemp()
    {
        // La carpeta nace protegida: lo que ssh-keygen escriba dentro hereda la ACL solo del usuario.
        PrivateAcl.EnsureDirectory(Directory);
        return Path.Combine(Directory, InstallFiles.TempPrefix + Guid.NewGuid().ToString("N"));
    }

    private static SshKeyInfo Describe(string name, string priv, string pub, string publicKey)
    {
        var parts = publicKey.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var type = parts.Length > 0 ? parts[0] : "?";
        string fingerprint;
        try { fingerprint = parts.Length > 1 ? KnownHosts.Fingerprint(parts[1]) : "?"; }
        catch (FormatException) { fingerprint = "?"; }
        return new SshKeyInfo(name, priv, pub, publicKey, type, fingerprint);
    }

    private static string Validate(string name)
    {
        if (!Site.IsValidName(name))
            throw new RoostException($"'{name}' no vale como nombre de clave: es el del sitio (minusculas, numeros y guiones).");
        return name;
    }

    private static string Tail(CommandResult result)
    {
        var line = GitOutput.Headline(result.Stderr);
        return line.Length == 0 ? "." : $": {line}";
    }
}
