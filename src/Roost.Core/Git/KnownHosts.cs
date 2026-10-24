using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Roost.Core.Scanning;

namespace Roost.Core.Git;

/// <summary>Una clave de servidor SSH como va en un <c>known_hosts</c>: el tipo y el blob en base64.</summary>
public sealed record SshHostKey(string Type, string Base64)
{
    /// <summary><c>SHA256:…</c>, calculada por Roost (<see cref="KnownHosts.Fingerprint"/>).</summary>
    public string Fingerprint => KnownHosts.Fingerprint(Base64);

    /// <summary>El nombre corto que enseña OpenSSH: ED25519, ECDSA, RSA.</summary>
    public string Algorithm => KnownHosts.AlgorithmOf(Type);
}

/// <summary>Una línea de un <c>known_hosts</c>: a qué servidores vale, la clave y el comentario (Roost apunta ahí cuándo se aceptó).</summary>
public sealed record KnownHostEntry(IReadOnlyList<string> Hosts, SshHostKey Key, string? Comment)
{
    public bool Matches(string hostToken) => Hosts.Any(h => string.Equals(h, hostToken, StringComparison.OrdinalIgnoreCase));

    public string ToLine() => $"{string.Join(',', Hosts)} {Key.Type} {Key.Base64}" + (Comment is null ? "" : " " + Comment);
}

/// <summary>Un servidor cuyas claves trae Roost de serie: las que publica su proveedor.</summary>
/// <param name="Source">Dónde las publica (para citarlo).</param>
public sealed record PublishedHost(string Host, GitProvider Provider, string Source, IReadOnlyList<SshHostKey> Keys);

/// <summary>Qué sabe Roost de un servidor SSH antes de conectar.</summary>
public enum HostTrust
{
    /// <summary>Roost no tiene ninguna huella suya: hay que comprobarla y aceptarla (TOFU explícito).</summary>
    Unknown,

    /// <summary>De serie (GitHub, GitLab, Bitbucket) y lo guardado coincide con lo publicado.</summary>
    Published,

    /// <summary>Aceptada por el usuario tras ver su huella.</summary>
    Trusted,

    /// <summary>Es un servidor de serie, pero lo que hay guardado no coincide con lo que publica su proveedor.</summary>
    Mismatch,
}

/// <summary>El estado de un servidor en el <c>known_hosts</c> de Roost.</summary>
public sealed record HostStatus(string Host, int? Port, HostTrust Trust, IReadOnlyList<KnownHostEntry> Stored, PublishedHost? Published)
{
    public string Token => KnownHosts.HostToken(Host, Port);
}

/// <summary>
/// El <c>known_hosts</c> propio de Roost (<c>state/git/known_hosts</c>), el único que lee el ssh de
/// las claves de Roost, siempre con <c>StrictHostKeyChecking=yes</c>: ssh nunca lo escribe, solo
/// Roost. Trae de serie las claves que publican GitHub, GitLab y Bitbucket (comprobadas contra sus
/// huellas publicadas en los tests); cualquier otro servidor se acepta solo tras enseñar su huella
/// (TOFU explícito, nunca en silencio). Si un servidor conocido presenta otra clave, ssh no conecta
/// y <see cref="GitFailure"/> lo cuenta como «la huella ha cambiado».
/// </summary>
public sealed partial class KnownHosts(string file, ICommandRunner runner, OpenSshTools? ssh = null)
{
    /// <summary>Lo que se espera a <c>ssh-keyscan</c> (y su propio <c>-T</c>).</summary>
    public static readonly TimeSpan ScanTimeout = TimeSpan.FromSeconds(10);

    public const string Header = """
        # Lo escribe Roost: los servidores SSH en los que confia para clonar y actualizar los sitios.
        # Los de github.com, gitlab.com y bitbucket.org son las claves que publica cada proveedor.
        # Los demas, los que aceptaste tras ver su huella. Gestionalo con roost git hosts.

        """;

    /// <summary>
    /// Las claves de serie. Las líneas salen de donde cada proveedor las publica en formato
    /// known_hosts, y cada una tiene que dar la huella SHA-256 que el proveedor publica aparte
    /// (<see cref="PublishedFingerprints"/>; lo comprueba <c>KnownHostsTests</c>). Bajadas y
    /// comprobadas el 24-oct-2026:
    /// <list type="bullet">
    /// <item>GitHub: <c>https://api.github.com/meta</c> (<c>ssh_keys</c>) y
    /// https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/githubs-ssh-key-fingerprints</item>
    /// <item>GitLab: https://docs.gitlab.com/user/gitlab_com/ («SSH known_hosts entries» y «SSH host keys fingerprints»)</item>
    /// <item>Bitbucket: <c>https://bitbucket.org/site/ssh</c> y
    /// https://support.atlassian.com/bitbucket-cloud/docs/configure-ssh-and-two-step-verification/</item>
    /// </list>
    /// Si un proveedor rota una clave (GitHub cambió la RSA en marzo de 2023), se cambian aquí la
    /// línea y la huella, y se publica una Roost nueva.
    /// </summary>
    public static readonly IReadOnlyList<PublishedHost> BuiltIn =
    [
        new("github.com", GitProvider.GitHub, "https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/githubs-ssh-key-fingerprints",
        [
            new("ssh-ed25519", "AAAAC3NzaC1lZDI1NTE5AAAAIOMqqnkVzrm0SdG6UOoqKLsabgH5C9okWi0dh2l9GKJl"),
            new("ecdsa-sha2-nistp256", "AAAAE2VjZHNhLXNoYTItbmlzdHAyNTYAAAAIbmlzdHAyNTYAAABBBEmKSENjQEezOmxkZMy7opKgwFB9nkt5YRrYMjNuG5N87uRgg6CLrbo5wAdT/y6v0mKV0U2w0WZ2YB/++Tpockg="),
            new("ssh-rsa", "AAAAB3NzaC1yc2EAAAADAQABAAABgQCj7ndNxQowgcQnjshcLrqPEiiphnt+VTTvDP6mHBL9j1aNUkY4Ue1gvwnGLVlOhGeYrnZaMgRK6+PKCUXaDbC7qtbW8gIkhL7aGCsOr/C56SJMy/BCZfxd1nWzAOxSDPgVsmerOBYfNqltV9/hWCqBywINIR+5dIg6JTJ72pcEpEjcYgXkE2YEFXV1JHnsKgbLWNlhScqb2UmyRkQyytRLtL+38TGxkxCflmO+5Z8CSSNY7GidjMIZ7Q4zMjA2n1nGrlTDkzwDCsw+wqFPGQA179cnfGWOWRVruj16z6XyvxvjJwbz0wQZ75XK5tKSb7FNyeIEs4TT4jk+S4dhPeAUC5y+bDYirYgM4GC7uEnztnZyaVWQ7B381AK4Qdrwt51ZqExKbQpTUNn+EjqoTwvqNj4kqx5QUCI0ThS/YkOxJCXmPUWZbhjpCg56i+2aB6CmK2JGhn57K5mj0MNdBXA4/WnwH6XoPWJzK5Nyu2zB3nAZp+S5hpQs+p1vN1/wsjk="),
        ]),
        new("gitlab.com", GitProvider.GitLab, "https://docs.gitlab.com/user/gitlab_com/",
        [
            new("ssh-ed25519", "AAAAC3NzaC1lZDI1NTE5AAAAIAfuCHKVTjquxvt6CM6tdG4SLp1Btn/nOeHHE5UOzRdf"),
            new("ecdsa-sha2-nistp256", "AAAAE2VjZHNhLXNoYTItbmlzdHAyNTYAAAAIbmlzdHAyNTYAAABBBFSMqzJeV9rUzU4kWitGjeR4PWSa29SPqJ1fVkhtj3Hw9xjLVXVYrU9QlYWrOLXBpQ6KWjbjTDTdDkoohFzgbEY="),
            new("ssh-rsa", "AAAAB3NzaC1yc2EAAAADAQABAAABAQCsj2bNKTBSpIYDEGk9KxsGh3mySTRgMtXL583qmBpzeQ+jqCMRgBqB98u3z++J1sKlXHWfM9dyhSevkMwSbhoR8XIq/U0tCNyokEi/ueaBMCvbcTHhO7FcwzY92WK4Yt0aGROY5qX2UKSeOvuP4D6TPqKF1onrSzH9bx9XUf2lEdWT/ia1NEKjunUqu1xOB/StKDHMoX4/OKyIzuS0q/T1zOATthvasJFoPrAjkohTyaDUz2LN5JoH839hViyEG82yB+MjcFV5MU3N1l1QL3cVUCh93xSaua1N85qivl+siMkPGbO5xR/En4iEY6K2XPASUEMaieWVNTRCtJ4S8H+9"),
        ]),
        new("bitbucket.org", GitProvider.Bitbucket, "https://support.atlassian.com/bitbucket-cloud/docs/configure-ssh-and-two-step-verification/",
        [
            new("ssh-ed25519", "AAAAC3NzaC1lZDI1NTE5AAAAIIazEu89wgQZ4bqs3d63QSMzYVa0MuJ2e2gKTKqu+UUO"),
            new("ecdsa-sha2-nistp256", "AAAAE2VjZHNhLXNoYTItbmlzdHAyNTYAAAAIbmlzdHAyNTYAAABBBPIQmuzMBuKdWeF4+a2sjSSpBK0iqitSQ+5BM9KhpexuGt20JpTVM7u5BDZngncgrqDMbWdxMWWOGtZ9UgbqgZE="),
            new("ssh-rsa", "AAAAB3NzaC1yc2EAAAADAQABAAABgQDQeJzhupRu0u0cdegZIa8e86EG2qOCsIsD1Xw0xSeiPDlCr7kq97NLmMbpKTX6Esc30NuoqEEHCuc7yWtwp8dI76EEEB1VqY9QJq6vk+aySyboD5QF61I/1WeTwu+deCbgKMGbUijeXhtfbxSxm6JwGrXrhBdofTsbKRUsrN1WoNgUa8uqN1Vx6WAJw1JHPhglEGGHea6QICwJOAr/6mrui/oB7pkaWKHj3z7d1IC4KWLtY47elvjbaTlkN04Kc/5LFEirorGYVbt15kAUlqGM65pk6ZBxtaO3+30LVlORZkxOh+LKL/BvbZ/iRNhItLqNyieoQj/uh/7Iv4uyH/cV/0b4WDSd3DptigWq84lJubb9t/DnZlrJazxyDCulTmKdOR7vs9gMTo+uoIrPSb8ScTtvw65+odKAlBj59dhnVp9zd7QUojOpXlL62Aw56U4oO+FALuevvMjiWeavKhJqlR7i5n9srYcrNV7ttmDw7kf/97P5zauIhxcjX+xHv4M="),
        ]),
    ];

    /// <summary>
    /// Las huellas SHA-256 tal como las publica cada proveedor en su página (las de
    /// <see cref="PublishedHost.Source"/>), copiadas aparte de las claves para poder contrastarlas.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> PublishedFingerprints = new Dictionary<string, IReadOnlyList<string>>
    {
        ["github.com"] =
        [
            "SHA256:+DiY3wvvV6TuJJhbpZisF/zLDA0zPMSvHdkr4UvCOqU",   // ED25519
            "SHA256:p2QAMXNIC1TJYWeIOttrVc98/R1BUFWu3/LiyKgUfQM",   // ECDSA
            "SHA256:uNiVztksCsDhcc0u9e8BujQXVUpKZIDTMczCvj3tD2s",   // RSA
        ],
        ["gitlab.com"] =
        [
            "SHA256:eUXGGm1YGsMAS7vkcx6JOJdOGHPem5gQp4taiCfCLB8",   // ED25519
            "SHA256:HbW3g8zUjNSksFbqTiUWPWg2Bq1x8xdGUrliXFzSnUw",   // ECDSA
            "SHA256:ROQFvPThGrW4RuWLoL9tq9I9zJ42fK4XywyRtbOz/EQ",   // RSA
        ],
        ["bitbucket.org"] =
        [
            "SHA256:ybgmFkzwOSotHTHLJgHO0QN8L0xErw6vd0VhFA9m3SM",   // ED25519
            "SHA256:FC73VB6C4OQLSCrjEayhMp9UMxS97caD/Yyi2bhW/J0",   // ECDSA
            "SHA256:46OSHA1Rmj8E8ERTC6xkNcmGOw9oFxYr0WF6zWW8l1E",   // RSA
        ],
    };

    public string FilePath => file;

    /// <summary>El servidor de serie con ese nombre (sin puerto o con el 22), o null.</summary>
    public static PublishedHost? PublishedFor(string host, int? port = null) =>
        port is null or 22 ? BuiltIn.FirstOrDefault(b => string.Equals(b.Host, host, StringComparison.OrdinalIgnoreCase)) : null;

    /// <summary>Cómo nombra OpenSSH al servidor en el known_hosts: <c>host</c> en el 22, <c>[host]:puerto</c> en otro.</summary>
    public static string HostToken(string host, int? port) =>
        port is null or 22 ? host.ToLowerInvariant() : $"[{host.ToLowerInvariant()}]:{port.Value.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// La huella SHA-256 de una clave, como la enseña OpenSSH (<c>SHA256:</c> + base64 sin <c>=</c>),
    /// calculada aquí sobre el blob: no se lee de lo que imprima ningún programa (PURO).
    /// Lanza <see cref="FormatException"/> si el base64 no vale.
    /// </summary>
    public static string Fingerprint(string base64) =>
        "SHA256:" + Convert.ToBase64String(SHA256.HashData(Convert.FromBase64String(base64))).TrimEnd('=');

    public static string AlgorithmOf(string type) => type switch
    {
        "ssh-ed25519" => "ED25519",
        "ecdsa-sha2-nistp256" or "ecdsa-sha2-nistp384" or "ecdsa-sha2-nistp521" => "ECDSA",
        "ssh-rsa" => "RSA",
        "ssh-dss" => "DSA",
        _ => type,
    };

    /// <summary>Los tipos de clave que Roost acepta de un servidor (DSA está roto y OpenSSH ya no lo usa).</summary>
    public static bool IsSupportedType(string type) =>
        type is "ssh-ed25519" or "ecdsa-sha2-nistp256" or "ecdsa-sha2-nistp384" or "ecdsa-sha2-nistp521" or "ssh-rsa";

    /// <summary>
    /// Una línea de known_hosts, o null si es un comentario, está vacía, lleva marcador
    /// (<c>@cert-authority</c>, <c>@revoked</c>), va con el nombre cifrado (<c>|1|…</c>: Roost no los
    /// escribe) o no se entiende. El blob tiene que ser base64 y declarar dentro el mismo tipo (PURO).
    /// </summary>
    public static KnownHostEntry? ParseLine(string line)
    {
        var text = line.Trim();
        if (text.Length == 0 || text[0] is '#' or '@' or '|') return null;
        var parts = Whitespace().Split(text, 4);
        if (parts.Length < 3 || !IsSupportedType(parts[1])) return null;
        if (DeclaredType(parts[2]) != parts[1]) return null;
        var hosts = parts[0].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(h => h.ToLowerInvariant()).ToList();
        if (hosts.Count == 0) return null;
        return new KnownHostEntry(hosts, new SshHostKey(parts[1], parts[2]), parts.Length > 3 ? parts[3].Trim() : null);
    }

    /// <summary>El tipo que lleva dentro el blob de una clave (los primeros 4 bytes son la longitud del nombre), o null si no se lee.</summary>
    public static string? DeclaredType(string base64)
    {
        byte[] blob;
        try { blob = Convert.FromBase64String(base64); }
        catch (FormatException) { return null; }
        if (blob.Length < 4) return null;
        var length = BinaryPrimitives.ReadUInt32BigEndian(blob);
        if (length is 0 or > 64 || blob.Length < 4 + length) return null;
        return Encoding.ASCII.GetString(blob, 4, (int)length);
    }

    /// <summary>Lo que hay en el fichero (lo que no se entiende se salta).</summary>
    public IReadOnlyList<KnownHostEntry> Load()
    {
        if (!File.Exists(file)) return [];
        return File.ReadAllLines(file).Select(ParseLine).OfType<KnownHostEntry>().ToList();
    }

    /// <summary>Las líneas guardadas para ese servidor.</summary>
    public IReadOnlyList<KnownHostEntry> For(string host, int? port = null)
    {
        var token = HostToken(host, port);
        return Load().Where(e => e.Matches(token)).ToList();
    }

    /// <summary>
    /// Pone las claves de serie de los servidores que no tengan ninguna línea (la primera vez, o
    /// tras olvidar uno con <see cref="Forget"/>: vuelve la publicada). Lo que ya hay no se toca: si
    /// alguien cambió la de un servidor de serie, <see cref="Status"/> lo dice como
    /// <see cref="HostTrust.Mismatch"/> y ssh no conectará.
    /// </summary>
    public void EnsurePublished()
    {
        var entries = Load();
        var missing = BuiltIn.Where(b => !entries.Any(e => e.Matches(b.Host))).ToList();
        if (missing.Count == 0 && File.Exists(file)) return;
        var lines = ReadLines();
        foreach (var host in missing)
            lines.AddRange(host.Keys.Select(k => new KnownHostEntry([host.Host], k, null).ToLine()));
        Save(lines);
    }

    /// <summary>Qué sabe Roost de ese servidor sin conectar.</summary>
    public HostStatus Status(string host, int? port = null)
    {
        var stored = For(host, port);
        var published = PublishedFor(host, port);
        HostTrust trust;
        if (published is not null)
        {
            // Sin nada guardado vale lo publicado: EnsurePublished lo escribe antes de conectar.
            trust = stored.Count == 0 || stored.All(e => published.Keys.Any(k => k.Base64 == e.Key.Base64))
                ? HostTrust.Published : HostTrust.Mismatch;
        }
        else
        {
            trust = stored.Count == 0 ? HostTrust.Unknown : HostTrust.Trusted;
        }
        return new HostStatus(host.ToLowerInvariant(), port is 22 ? null : port, trust, stored, published);
    }

    /// <summary>
    /// Pregunta al servidor por sus claves (<c>ssh-keyscan</c>, sin autenticarse ni guardar nada).
    /// Las huellas se calculan aquí sobre cada blob, y solo valen las líneas del servidor pedido
    /// con un tipo que cuadre con el que lleva dentro. Lanza <see cref="RoostException"/> si no contesta.
    /// </summary>
    public async Task<IReadOnlyList<SshHostKey>> ScanAsync(string host, int? port, CancellationToken cancellation)
    {
        ValidateHost(host);
        var tools = ssh ?? OpenSsh.Locate();
        var portText = (port ?? 22).ToString(CultureInfo.InvariantCulture);
        var args = new List<string> { "-T", ((int)ScanTimeout.TotalSeconds).ToString(CultureInfo.InvariantCulture), "-t", "ed25519,ecdsa,rsa" };
        if (port is { } p && p != 22) args.AddRange(["-p", portText]);
        args.Add(host);
        var result = await runner.RunAsync(new CommandSpec(tools.SshKeyscan, args, Timeout: ScanTimeout + TimeSpan.FromSeconds(5), Stdin: ""), null, cancellation);
        var keys = ParseScan(result.Stdout, host, port);
        if (keys.Count > 0) return keys;

        // El ssh-keyscan del OpenSSH 9.5 de Windows ofrece un intercambio de claves que no sabe hacer
        // («choose_kex: unsupported KEX method sntrup761x25519-sha512») y no saca nada. El cliente ssh
        // sí negocia bien: se le deja apuntar la clave en un known_hosts temporal, de usar y tirar (ni
        // el de Roost ni el del usuario), sin ofrecer ninguna credencial; la huella se calcula aquí igual.
        var temp = Path.Combine(Path.GetTempPath(), "roost-hostkey-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var capture = Path.Combine(temp, "known_hosts");
            var none = Path.Combine(temp, "global_known_hosts");
            var probe = await runner.RunAsync(new CommandSpec(tools.Ssh,
            [
                "-F", "none",
                "-o", "BatchMode=yes",
                "-o", "StrictHostKeyChecking=accept-new",
                "-o", "UserKnownHostsFile=" + OpenSsh.ConfigPath(capture),
                "-o", "GlobalKnownHostsFile=" + OpenSsh.ConfigPath(none),
                "-o", "UpdateHostKeys=no",
                "-o", "HashKnownHosts=no",
                "-o", "PubkeyAuthentication=no",
                "-o", "PasswordAuthentication=no",
                "-o", "KbdInteractiveAuthentication=no",
                "-o", "GSSAPIAuthentication=no",
                "-o", "IdentityAgent=none",
                "-o", "HostKeyAlgorithms=ssh-ed25519,ecdsa-sha2-nistp256,ecdsa-sha2-nistp384,ecdsa-sha2-nistp521,rsa-sha2-512,rsa-sha2-256",
                "-o", "ConnectTimeout=" + ((int)ScanTimeout.TotalSeconds).ToString(CultureInfo.InvariantCulture),
                "-p", portText,
                "-l", "git",
                host,
            ], Timeout: ScanTimeout + TimeSpan.FromSeconds(10), Stdin: ""), null, cancellation);
            if (File.Exists(capture)) keys = ParseScan(File.ReadAllText(capture), host, port);
            if (keys.Count > 0) return keys;
            var why = GitOutput.Headline(probe.Stderr.Length > 0 ? probe.Stderr : result.Stderr);
            throw new RoostException($"{HostToken(host, port)} no ha dado ninguna clave SSH{(result.TimedOut || probe.TimedOut ? " (no contesto a tiempo)" : why.Length > 0 ? $" ({why})" : "")}. Comprueba el nombre, el puerto y tu conexion.");
        }
        finally
        {
            Install.InstallFiles.TryDeleteDirectory(temp);
        }
    }

    /// <summary>Las claves de la salida de <c>ssh-keyscan</c> que son de ese servidor, sin repetir (PURO).</summary>
    public static IReadOnlyList<SshHostKey> ParseScan(string stdout, string host, int? port)
    {
        var token = HostToken(host, port);
        return stdout.Split('\n').Select(ParseLine).OfType<KnownHostEntry>()
            .Where(e => e.Matches(token))
            .Select(e => e.Key)
            .DistinctBy(k => k.Base64)
            .OrderBy(k => k.Type switch { "ssh-ed25519" => 0, "ssh-rsa" => 2, _ => 1 })
            .ToList();
    }

    /// <summary>
    /// Acepta esas claves para el servidor (las que ya tenía se sustituyen). Lo llama quien ha
    /// enseñado la huella y tiene el sí del usuario, o la ha comparado con la publicada.
    /// </summary>
    public void Trust(string host, int? port, IReadOnlyList<SshHostKey> keys)
    {
        ValidateHost(host);
        if (keys.Count == 0) throw new ArgumentException("Sin claves que aceptar.", nameof(keys));
        var token = HostToken(host, port);
        var lines = ReadLines().Where(l => ParseLine(l) is not { } e || !e.Matches(token)).ToList();
        var published = PublishedFor(host, port);
        var comment = published is not null && keys.All(k => published.Keys.Any(p => p.Base64 == k.Base64))
            ? null
            : $"roost:aceptada-{DateTime.Now:yyyy-MM-dd}";
        lines.AddRange(keys.Select(k => new KnownHostEntry([token], k, comment).ToLine()));
        Save(lines);
    }

    /// <summary>Olvida el servidor: quita sus líneas. Uno de serie vuelve a tener la publicada la próxima vez. Devuelve cuántas quitó.</summary>
    public int Forget(string host, int? port)
    {
        var token = HostToken(host, port);
        var lines = ReadLines();
        var kept = lines.Where(l => ParseLine(l) is not { } e || !e.Matches(token)).ToList();
        var removed = lines.Count - kept.Count;
        if (removed > 0) Save(kept);
        return removed;
    }

    /// <summary>La fecha en que se aceptó, si la apuntó Roost en el comentario.</summary>
    public static string? AcceptedOn(KnownHostEntry entry) =>
        entry.Comment is { } c && c.StartsWith("roost:aceptada-", StringComparison.Ordinal) ? c["roost:aceptada-".Length..] : null;

    /// <summary>Un nombre de servidor o una IP: sin espacios, sin guion delante (no puede pasar por una opción de ssh-keyscan).</summary>
    public static bool IsValidHost(string host) => HostPattern().IsMatch(host);

    private static void ValidateHost(string host)
    {
        if (!IsValidHost(host)) throw new RoostException($"'{host}' no vale como nombre de servidor.");
    }

    private List<string> ReadLines()
    {
        if (!File.Exists(file)) return Header.TrimEnd().Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        return File.ReadAllLines(file).ToList();
    }

    private void Save(IEnumerable<string> lines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp = file + ".tmp";
        // LF: lo lee OpenSSH, que acepta los dos, y así es igual en todas partes.
        File.WriteAllText(temp, string.Join('\n', lines) + "\n");
        File.Move(temp, file, overwrite: true);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"^(?:[A-Za-z0-9](?:[A-Za-z0-9.\-]{0,251}[A-Za-z0-9])?|\[?[0-9A-Fa-f:.]+\]?)$")]
    private static partial Regex HostPattern();
}
