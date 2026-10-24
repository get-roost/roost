using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Roost.Core.Platform;

namespace Roost.Core.Sites;

/// <summary>
/// CA local tipo mkcert, en C# puro (ROOST.md §5.8): una raíz ECDSA P-256 de 10 años en
/// <c>certs/ca</c>, con la clave privada legible solo por el usuario, y un certificado por
/// sitio (<c>&lt;nombre&gt;.test</c> y <c>*.&lt;nombre&gt;.test</c>) en <c>certs/sites</c>, que se
/// renueva solo. Confiar en la CA es un paso aparte y explícito (<see cref="Trust"/>).
/// </summary>
public sealed class LocalCa(RoostPaths paths)
{
    /// <summary>Validez de los certificados de sitio: por debajo de los 398 días que aceptan los navegadores.</summary>
    public static readonly TimeSpan SiteValidity = TimeSpan.FromDays(397);

    /// <summary>Con menos de esto por delante, el certificado de un sitio se renueva.</summary>
    public static readonly TimeSpan RenewBefore = TimeSpan.FromDays(30);

    public string CaCertFile => Path.Combine(paths.Certs, "ca", "roost-ca.crt");
    public string CaKeyFile => Path.Combine(paths.Certs, "ca", "roost-ca.key");

    public string SiteCertFile(string site) => Path.Combine(paths.Certs, "sites", site + ".crt");
    public string SiteKeyFile(string site) => Path.Combine(paths.Certs, "sites", site + ".key");

    /// <summary>La CA (con su clave), creándola la primera vez.</summary>
    public X509Certificate2 LoadOrCreate()
    {
        if (File.Exists(CaCertFile) && File.Exists(CaKeyFile))
            return X509Certificate2.CreateFromPemFile(CaCertFile, CaKeyFile);

        Directory.CreateDirectory(Path.GetDirectoryName(CaCertFile)!);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(
            $"CN=Roost Local CA ({Environment.UserName}@{Environment.MachineName}), O=Roost", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        var now = DateTimeOffset.UtcNow;
        using var ca = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(10));
        WritePrivate(CaKeyFile, key.ExportPkcs8PrivateKeyPem());
        File.WriteAllText(CaCertFile, ca.ExportCertificatePem());
        return X509Certificate2.CreateFromPemFile(CaCertFile, CaKeyFile);
    }

    /// <summary>
    /// Ficheros del certificado del sitio, emitiéndolo si no existe, si caduca en menos de
    /// <see cref="RenewBefore"/>, si no es de la CA actual o si <paramref name="force"/>.
    /// </summary>
    public (string CertFile, string KeyFile) EnsureSiteCert(Site site, bool force = false)
    {
        var certFile = SiteCertFile(site.Name);
        var keyFile = SiteKeyFile(site.Name);
        using var ca = LoadOrCreate();
        if (!force && IsUsable(certFile, keyFile, site, ca)) return (certFile, keyFile);

        Directory.CreateDirectory(Path.GetDirectoryName(certFile)!);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={site.Host}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false)); // serverAuth
        var san = new SubjectAlternativeNameBuilder();
        // El sitio y *.sitio y, con alias (C7), cada alias con el suyo (SiteAliases.CertNames).
        foreach (var name in SiteAliases.CertNames(site)) san.AddDnsName(name);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(ca, true, false));

        var now = DateTimeOffset.UtcNow;
        var notAfter = now + SiteValidity < ca.NotAfter ? now + SiteValidity : new DateTimeOffset(ca.NotAfter.ToUniversalTime());
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F; // número de serie positivo
        using var cert = request.Create(ca, now.AddDays(-1), notAfter, serial);

        WritePrivate(keyFile, key.ExportPkcs8PrivateKeyPem());
        File.WriteAllText(certFile, cert.ExportCertificatePem());
        return (certFile, keyFile);
    }

    public void RemoveSiteCert(string site)
    {
        foreach (var file in new[] { SiteCertFile(site), SiteKeyFile(site) })
        {
            try { File.Delete(file); } catch { /* lo que quede se reemplaza al volver a emitir */ }
        }
    }

    private ITrustStore? _store;

    /// <summary>
    /// Dónde se confía en la CA (<see cref="ITrustStore"/>): el almacén raíz del usuario en Windows,
    /// los NSS del usuario en Linux (Chrome y cada perfil de Firefox, con certutil) o el de Windows
    /// desde WSL. Los tests ponen el suyo.
    /// </summary>
    public ITrustStore Store
    {
        get => _store ??= TrustStores.Default();
        init => _store = value;
    }

    /// <summary>true si la CA actual está en el almacén de confianza del usuario (<see cref="Store"/>).</summary>
    public bool IsTrusted()
    {
        if (!File.Exists(CaCertFile)) return false;
        using var ca = X509Certificate2.CreateFromPem(File.ReadAllText(CaCertFile));
        return Store.IsTrusted(ca);
    }

    /// <summary>
    /// Añade la CA al almacén del USUARIO (<see cref="Store"/>; nunca al de la máquina: no hace falta
    /// ser administrador). En Windows sale un aviso que el usuario tiene que aceptar; si lo rechaza,
    /// lanza <see cref="RoostException"/>. Devuelve dónde ha quedado (en Linux, qué navegadores y perfiles).
    /// </summary>
    public TrustReport Trust()
    {
        using var ca = LoadOrCreate();
        return Store.Trust(ca);
    }

    /// <summary>
    /// Quita la CA del almacén del usuario (Windows vuelve a preguntar). Los certificados de los
    /// sitios siguen ahí; el navegador vuelve a avisar de que no son de confianza.
    /// </summary>
    public TrustReport Untrust()
    {
        if (!File.Exists(CaCertFile)) return TrustReport.Empty;
        using var ca = X509Certificate2.CreateFromPem(File.ReadAllText(CaCertFile));
        return Store.Untrust(ca);
    }

    // Linux: la CA del SISTEMA (curl, wget, el PHP de la distro, git), opcional y por el helper (root):
    // /usr/local/share/ca-certificates/roost-<uid>.crt + update-ca-certificates. Es «roost cert trust --system».

    public const string SystemCaDirectory = "/usr/local/share/ca-certificates";

    /// <summary>El fichero de la CA de este usuario en el sistema (<c>roost-&lt;uid&gt;.crt</c>; uno por usuario); null fuera de Linux.</summary>
    public static string? SystemCaFile() => HelperChecks.CurrentUid() is { } uid ? $"{SystemCaDirectory}/roost-{uid}.crt" : null;

    /// <summary>true si el sistema tiene esta misma CA (el fichero existe y es el mismo PEM).</summary>
    public bool IsTrustedBySystem()
    {
        if (SystemCaFile() is not { } file || !File.Exists(file) || !File.Exists(CaCertFile)) return false;
        try
        {
            return Pem(File.ReadAllText(file)) == Pem(File.ReadAllText(CaCertFile));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        static string Pem(string text) => text.Replace("\r\n", "\n").Trim();
    }

    /// <summary>Pone la CA en el sistema con el helper (<c>ca-trust</c>, el PEM por stdin). Lanza <see cref="RoostException"/> si se cancela o falla.</summary>
    public HelperRun TrustSystem(string helperExe, Elevation? elevation = null)
    {
        using var ca = LoadOrCreate();
        var run = (elevation ?? Elevation.Default).Run(helperExe, ["ca-trust"], stdin: ca.ExportCertificatePem(), stdinFile: CaCertFile);
        if (run.Status == ElevationStatus.Cancelled)
            throw new RoostException("Has cancelado el permiso de administrador: la CA del sistema no se ha tocado.");
        if (run.ExitCode != 0)
            throw new RoostException($"roost-helper no pudo anadir la CA al sistema (codigo {run.ExitCode}). Detalle en {HostsUpdater.HelperLog}.{Detail(run)}");
        return run;
    }

    /// <summary>Quita la CA de este usuario del sistema con el helper (<c>ca-untrust</c>). Lanza <see cref="RoostException"/> si se cancela o falla.</summary>
    public HelperRun UntrustSystem(string helperExe, Elevation? elevation = null)
    {
        var run = (elevation ?? Elevation.Default).Run(helperExe, ["ca-untrust"]);
        if (run.Status == ElevationStatus.Cancelled)
            throw new RoostException("Has cancelado el permiso de administrador: la CA del sistema se queda como estaba.");
        if (run.ExitCode != 0)
            throw new RoostException($"roost-helper no pudo quitar la CA del sistema (codigo {run.ExitCode}). Detalle en {HostsUpdater.HelperLog}.{Detail(run)}");
        return run;
    }

    private static string Detail(HelperRun run) => run.Output.Length == 0 ? "" : $" ({run.Output})";

    /// <summary>Nombre, huella SHA-1 y caducidad de la CA, para enseñarlos; null si aún no existe.</summary>
    public (string Subject, string Thumbprint, DateTime NotAfter)? Describe()
    {
        if (!File.Exists(CaCertFile)) return null;
        using var ca = X509Certificate2.CreateFromPem(File.ReadAllText(CaCertFile));
        var subject = ca.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        var thumb = string.Join(':', Enumerable.Range(0, ca.Thumbprint.Length / 2).Select(i => ca.Thumbprint.Substring(i * 2, 2)));
        return (subject, thumb, ca.NotAfter);
    }

    // names: los nombres DNS que tiene que llevar; null = los del sitio (SiteAliases.CertNames). El de la
    // red local solo lleva el nombre del sitio (y la IP, que aquí no cuenta).
    private static bool IsUsable(string certFile, string keyFile, Site site, X509Certificate2 ca, IReadOnlyList<string>? names = null)
    {
        if (!File.Exists(certFile) || !File.Exists(keyFile)) return false;
        try
        {
            using var cert = X509Certificate2.CreateFromPem(File.ReadAllText(certFile));
            if (cert.NotAfter.ToUniversalTime() - DateTime.UtcNow < RenewBefore) return false;
            if (cert.Issuer != ca.Subject) return false;
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(ca);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            return chain.Build(cert) && cert.MatchesHostname(site.Host) && HasNames(cert, names ?? SiteAliases.CertNames(site));
        }
        catch
        {
            return false; // ilegible: se vuelve a emitir
        }
    }

    /// <summary>
    /// true si los nombres DNS del certificado son exactamente <paramref name="wanted"/>: un alias nuevo
    /// o quitado (C7) hace que se vuelva a emitir. Las IP del SAN, si las hay, no cuentan aquí.
    /// </summary>
    private static bool HasNames(X509Certificate2 cert, IReadOnlyList<string> wanted) =>
        DnsNames(cert).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(wanted);

    /// <summary>Los nombres DNS del SAN de un certificado (vacío si no lo tiene).</summary>
    public static IReadOnlyList<string> DnsNames(X509Certificate2 cert)
    {
        var ext = cert.Extensions.FirstOrDefault(e => e.Oid?.Value == "2.5.29.17");
        return ext is null ? [] : new X509SubjectAlternativeNameExtension(ext.RawData, ext.Critical).EnumerateDnsNames().ToList();
    }

    /// <summary>Los nombres DNS del certificado de un sitio, para enseñarlos (vacío si no hay o no se lee).</summary>
    public IReadOnlyList<string> SiteCertNames(string site)
    {
        try
        {
            if (!File.Exists(SiteCertFile(site))) return [];
            using var cert = X509Certificate2.CreateFromPem(File.ReadAllText(SiteCertFile(site)));
            return DnsNames(cert);
        }
        catch (Exception ex) when (ex is IOException or CryptographicException or ArgumentException)
        {
            return [];
        }
    }

    /// <summary>Escribe una clave privada que solo puede leer el usuario actual (la misma ACL que las claves SSH de los sitios).</summary>
    private static void WritePrivate(string path, string content) => PrivateFile.Write(path, content);

    // Compartir (oleada 6): el certificado de la red local, con la IP.

    public string LanCertFile(string site) => Path.Combine(paths.Certs, "lan", site + ".crt");
    public string LanKeyFile(string site) => Path.Combine(paths.Certs, "lan", site + ".key");

    /// <summary>
    /// El certificado de <c>roost share --lan</c>, en <c>certs/lan</c>: el nombre del sitio
    /// (<c>&lt;n&gt;.test</c>) y además la IP de la red local como SAN, porque el móvil entra por la IP
    /// (<c>https://192.168.1.20:8443</c>), y un certificado sin ella daría un error de nombre aunque
    /// se confiara en la CA. Mismas reglas que el del sitio (ECDSA P-256, serverAuth,
    /// <see cref="SiteValidity"/>); se reemite si falta, si caduca en menos de <see cref="RenewBefore"/>,
    /// si no es de la CA actual o si no lleva esa IP.
    /// </summary>
    public (string CertFile, string KeyFile) EnsureLanCert(Site site, System.Net.IPAddress address)
    {
        var certFile = LanCertFile(site.Name);
        var keyFile = LanKeyFile(site.Name);
        using var ca = LoadOrCreate();
        if (IsUsableLan(certFile, keyFile, site, address, ca)) return (certFile, keyFile);

        Directory.CreateDirectory(Path.GetDirectoryName(certFile)!);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={site.Host}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false)); // serverAuth
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(site.Host);
        san.AddIpAddress(address);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(ca, true, false));

        var now = DateTimeOffset.UtcNow;
        var notAfter = now + SiteValidity < ca.NotAfter ? now + SiteValidity : new DateTimeOffset(ca.NotAfter.ToUniversalTime());
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F; // número de serie positivo
        using var cert = request.Create(ca, now.AddDays(-1), notAfter, serial);

        WritePrivate(keyFile, key.ExportPkcs8PrivateKeyPem());
        File.WriteAllText(certFile, cert.ExportCertificatePem());
        return (certFile, keyFile);
    }

    /// <summary>Borra el certificado de la red local de un sitio (al dejar de compartirlo; se vuelve a emitir la próxima vez).</summary>
    public void RemoveLanCert(string site)
    {
        foreach (var file in new[] { LanCertFile(site), LanKeyFile(site) })
        {
            try { File.Delete(file); } catch { /* lo que quede se reemplaza al volver a emitir */ }
        }
    }

    private static bool IsUsableLan(string certFile, string keyFile, Site site, System.Net.IPAddress address, X509Certificate2 ca)
    {
        if (!IsUsable(certFile, keyFile, site, ca, [site.Host])) return false;
        try
        {
            using var cert = X509Certificate2.CreateFromPem(File.ReadAllText(certFile));
            return cert.MatchesHostname(address.ToString());
        }
        catch
        {
            return false;
        }
    }
}
