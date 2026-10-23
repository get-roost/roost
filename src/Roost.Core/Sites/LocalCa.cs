using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
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
        san.AddDnsName(site.Host);
        san.AddDnsName("*." + site.Host);
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

    /// <summary>true si la CA actual está en el almacén raíz de confianza del usuario.</summary>
    public bool IsTrusted()
    {
        if (!File.Exists(CaCertFile)) return false;
        using var ca = X509Certificate2.CreateFromPem(File.ReadAllText(CaCertFile));
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        return store.Certificates.Find(X509FindType.FindByThumbprint, ca.Thumbprint, false).Count > 0;
    }

    /// <summary>
    /// Añade la CA al almacén raíz del USUARIO (no al de la máquina: no hace falta ser
    /// administrador). Windows enseña un aviso que el usuario tiene que aceptar; si lo rechaza,
    /// lanza <see cref="RoostException"/>. Chrome y Edge usan ese almacén.
    /// </summary>
    public void Trust()
    {
        using var ca = LoadOrCreate();
        using var publicOnly = X509Certificate2.CreateFromPem(ca.ExportCertificatePem());
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        try
        {
            store.Open(OpenFlags.ReadWrite);
            store.Add(publicOnly);
        }
        catch (CryptographicException ex)
        {
            throw new RoostException($"No se ha anadido la CA de Roost a los certificados de confianza: {ex.Message}");
        }
    }

    /// <summary>
    /// Quita la CA del almacén raíz del usuario (Windows vuelve a preguntar). Los certificados de
    /// los sitios siguen ahí; el navegador vuelve a avisar de que no son de confianza.
    /// </summary>
    public void Untrust()
    {
        if (!File.Exists(CaCertFile)) return;
        using var ca = X509Certificate2.CreateFromPem(File.ReadAllText(CaCertFile));
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        try
        {
            store.Open(OpenFlags.ReadWrite);
            foreach (var found in store.Certificates.Find(X509FindType.FindByThumbprint, ca.Thumbprint, false))
                store.Remove(found);
        }
        catch (CryptographicException ex)
        {
            throw new RoostException($"No se ha quitado la CA de Roost de los certificados de confianza: {ex.Message}");
        }
    }

    /// <summary>Nombre, huella SHA-1 y caducidad de la CA, para enseñarlos; null si aún no existe.</summary>
    public (string Subject, string Thumbprint, DateTime NotAfter)? Describe()
    {
        if (!File.Exists(CaCertFile)) return null;
        using var ca = X509Certificate2.CreateFromPem(File.ReadAllText(CaCertFile));
        var subject = ca.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        var thumb = string.Join(':', Enumerable.Range(0, ca.Thumbprint.Length / 2).Select(i => ca.Thumbprint.Substring(i * 2, 2)));
        return (subject, thumb, ca.NotAfter);
    }

    private static bool IsUsable(string certFile, string keyFile, Site site, X509Certificate2 ca)
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
            return chain.Build(cert) && cert.MatchesHostname(site.Host);
        }
        catch
        {
            return false; // ilegible: se vuelve a emitir
        }
    }

    /// <summary>Escribe una clave privada que solo puede leer el usuario actual.</summary>
    private static void WritePrivate(string path, string content)
    {
        File.WriteAllText(path, content);
        if (OperatingSystem.IsWindows()) RestrictToCurrentUser(path);
        else File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [SupportedOSPlatform("windows")]
    private static void RestrictToCurrentUser(string path)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false); // sin permisos heredados
        security.AddAccessRule(new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }
}
