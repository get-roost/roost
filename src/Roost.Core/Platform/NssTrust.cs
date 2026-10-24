using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Roost.Core.Scanning;
using Roost.Core.Sites;

namespace Roost.Core.Platform;

/// <summary>
/// La CA local en los almacenes NSS del usuario en Linux, con <c>certutil</c> (paquete
/// <c>libnss3-tools</c>; no viene de serie): el de Chrome, Chromium, Edge y Brave
/// (<c>~/.pki/nssdb</c>, que se crea si no existe), el del Chromium de snap, los de las apps de
/// flatpak que lo tengan y cada perfil de Firefox (de la distro, de snap y de flatpak), porque
/// Firefox no lee los demás. No pide privilegios. Con Firefox abierto, su <c>cert9.db</c> puede estar
/// ocupado: entonces ese perfil se salta y se dice. La CA del sistema (curl, wget, el PHP de la
/// distro) va aparte, por el helper (<c>LocalCa.TrustSystem</c>).
/// </summary>
public sealed class NssTrust : ITrustStore
{
    /// <summary>El nombre con el que se ve la CA en el navegador (<c>-n</c> de certutil).</summary>
    public const string Nickname = "Roost Local CA";

    /// <summary>La confianza que se le da: CA para servidores (SSL), y nada más (ni correo ni firma de código).</summary>
    public const string TrustFlags = "C,,";

    public const string Package = "libnss3-tools";

    private readonly ICommandRunner _runner;
    private readonly string _home;

    /// <param name="home">La home que se recorre (los tests, una carpeta temporal); null = la del usuario.</param>
    /// <param name="certutil">Dónde está certutil; null = buscarlo (<see cref="FindCertutil"/>).</param>
    public NssTrust(ICommandRunner runner, string? home = null, string? certutil = null)
    {
        _runner = runner;
        _home = home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Certutil = certutil ?? FindCertutil();
    }

    public string Description => "los almacenes de tu usuario (Chrome, Chromium, Edge, Brave y cada perfil de Firefox)";

    /// <summary>La ruta de certutil, o null si no está instalado.</summary>
    public string? Certutil { get; }

    /// <summary>El certutil del PATH o el de <c>/usr/bin</c>; null si falta (<see cref="Package"/>).</summary>
    public static string? FindCertutil() =>
        PathProbe.ResolveActive(["certutil"]) ?? (File.Exists("/usr/bin/certutil") ? "/usr/bin/certutil" : null);

    /// <summary>Un almacén NSS: su carpeta (la de <c>cert9.db</c>), cómo se llama para el usuario y si es el principal (el de Chrome).</summary>
    public sealed record Database(string Path, string Label, bool IsMain)
    {
        public bool Exists => File.Exists(System.IO.Path.Combine(Path, "cert9.db"));
    }

    /// <summary>
    /// Los almacenes de este usuario: el principal siempre (se crea al confiar si no existe) y, de los
    /// demás, los que existen.
    /// </summary>
    public IReadOnlyList<Database> Databases()
    {
        var list = new List<Database> { new(Path.Combine(_home, ".pki", "nssdb"), "Chrome, Chromium, Edge y Brave", true) };

        var snap = Path.Combine(_home, "snap", "chromium", "current", ".pki", "nssdb");
        if (File.Exists(Path.Combine(snap, "cert9.db"))) list.Add(new Database(snap, "Chromium (snap)", false));

        var flatpak = Path.Combine(_home, ".var", "app");
        if (Directory.Exists(flatpak))
        {
            foreach (var app in Directory.GetDirectories(flatpak).Order(StringComparer.Ordinal))
            {
                var db = Path.Combine(app, ".pki", "nssdb");
                if (File.Exists(Path.Combine(db, "cert9.db"))) list.Add(new Database(db, $"{Path.GetFileName(app)} (flatpak)", false));
            }
        }

        foreach (var root in FirefoxTrust.LinuxRoots(_home))
        {
            foreach (var profile in FirefoxTrust.Profiles(root))
            {
                if (File.Exists(Path.Combine(profile, "cert9.db")))
                    list.Add(new Database(profile, $"Firefox ({Path.GetFileName(profile)})", false));
            }
        }
        return list;
    }

    public bool IsTrusted(X509Certificate2 ca) =>
        Certutil is not null && Thumbprints(Databases()[0]).Contains(ca.Thumbprint, StringComparer.OrdinalIgnoreCase);

    public TrustReport Trust(X509Certificate2 ca)
    {
        if (Certutil is null) throw MissingCertutil("anade");
        var pem = ca.ExportCertificatePem();
        var stores = new List<string>();
        var skipped = new List<string>();
        foreach (var db in Databases())
        {
            if (!db.Exists)
            {
                if (!db.IsMain) continue;
                Directory.CreateDirectory(db.Path);
                var created = Run(db, ["-N", "--empty-password"]);
                if (!created.Succeeded)
                {
                    skipped.Add($"{db.Label}: no se pudo crear {db.Path} ({FirstLine(created)})");
                    continue;
                }
            }

            // Una CA de Roost anterior (regenerada, o de otra Roost) con el mismo nombre se quita: si no, el navegador tendría dos.
            RemoveNickname(db, keep: ca.Thumbprint);
            if (Thumbprints(db).Contains(ca.Thumbprint, StringComparer.OrdinalIgnoreCase))
            {
                stores.Add(db.Label);
                continue;
            }

            var added = Run(db, ["-A", "-n", Nickname, "-t", TrustFlags], stdin: pem);
            if (added.Succeeded) stores.Add(db.Label);
            else skipped.Add($"{db.Label}: {FirstLine(added)}{(db.Label.StartsWith("Firefox", StringComparison.Ordinal) ? " (cierra Firefox y repite roost cert trust)" : "")}");
        }

        if (stores.Count == 0)
            throw new RoostException("No se ha podido anadir la CA a ningun almacen: " + string.Join("; ", skipped));
        return new TrustReport(stores, skipped);
    }

    public TrustReport Untrust(X509Certificate2 ca)
    {
        if (Certutil is null) throw MissingCertutil("quita");
        var stores = new List<string>();
        var skipped = new List<string>();
        foreach (var db in Databases().Where(d => d.Exists))
        {
            if (!Thumbprints(db).Contains(ca.Thumbprint, StringComparer.OrdinalIgnoreCase)) continue;
            var error = RemoveNickname(db, keep: null);
            if (error is null) stores.Add(db.Label);
            else skipped.Add($"{db.Label}: {error}");
        }
        if (stores.Count == 0 && skipped.Count > 0)
            throw new RoostException("No se ha podido quitar la CA de " + string.Join("; ", skipped));
        return new TrustReport(stores, skipped);
    }

    /// <summary>Las huellas (SHA-1) de lo que hay en el almacén con el nombre de Roost (<c>-L -n … -a</c> saca sus PEM).</summary>
    public IReadOnlyList<string> Thumbprints(Database db)
    {
        if (!db.Exists) return [];
        var listed = Run(db, ["-L", "-n", Nickname, "-a"]);
        if (!listed.Succeeded) return [];   // 255: no hay ninguno con ese nombre
        return PemBlocks(listed.Stdout).Select(block =>
        {
            try
            {
                using var cert = X509Certificate2.CreateFromPem(block);
                return cert.Thumbprint;
            }
            catch (CryptographicException)
            {
                return "";
            }
        }).Where(t => t.Length > 0).ToList();
    }

    /// <summary>Quita del almacén lo que se llame como la CA de Roost y no tenga la huella <paramref name="keep"/> (null = todo). null si ha ido bien; si no, el error.</summary>
    private string? RemoveNickname(Database db, string? keep)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var thumbs = Thumbprints(db);
            if (!thumbs.Any(t => keep is null || !t.Equals(keep, StringComparison.OrdinalIgnoreCase))) return null;
            var removed = Run(db, ["-D", "-n", Nickname]);
            if (!removed.Succeeded) return FirstLine(removed);
            // certutil -D quita uno de los que tienen ese nombre; si quitó el que se quería guardar, se vuelve a añadir en Trust.
        }
        return "no se ha podido dejar un solo '" + Nickname + "'";
    }

    private CommandResult Run(Database db, IReadOnlyList<string> arguments, string? stdin = null) =>
        _runner.RunAsync(new CommandSpec(Certutil!, ["-d", "sql:" + db.Path, .. arguments], Stdin: stdin, Timeout: TimeSpan.FromSeconds(30)), null, CancellationToken.None)
            .GetAwaiter().GetResult();

    private static RoostException MissingCertutil(string verb) =>
        new($"Falta certutil, con el que se {verb} la CA en Chrome y Firefox. Instalalo: sudo apt install {Package}");

    private static string FirstLine(CommandResult result) =>
        $"{result.Stderr}\n{result.Stdout}".Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? $"codigo {result.ExitCode}";

    /// <summary>PURO: cada bloque <c>BEGIN/END CERTIFICATE</c> de un texto.</summary>
    public static IReadOnlyList<string> PemBlocks(string text)
    {
        const string begin = "-----BEGIN CERTIFICATE-----";
        const string end = "-----END CERTIFICATE-----";
        var blocks = new List<string>();
        for (var at = text.IndexOf(begin, StringComparison.Ordinal); at >= 0; at = text.IndexOf(begin, at + 1, StringComparison.Ordinal))
        {
            var stop = text.IndexOf(end, at, StringComparison.Ordinal);
            if (stop < 0) break;
            blocks.Add(text[at..(stop + end.Length)]);
        }
        return blocks;
    }
}
