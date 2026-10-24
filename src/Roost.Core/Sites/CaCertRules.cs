using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Roost.Core.Sites;

/// <summary>
/// Lo que tiene que cumplir el PEM que recibe <c>roost-helper ca-trust</c> por stdin para que lo
/// instale como CA del sistema en Linux: que sea una CA local de Roost (<see cref="LocalCa"/>) y no
/// cualquier otro certificado que alguien le cuele. Como <c>HostsBlock</c>, se compila también dentro
/// del helper (enlazado como fuente), así que solo usa la BCL. PURO: no toca disco ni red.
/// </summary>
internal static class CaCertRules
{
    /// <summary>Un PEM de la CA de Roost ocupa menos de 1 KB; más de esto no se lee.</summary>
    public const int MaxPemLength = 16 * 1024;

    /// <summary>Cómo empieza el CN de toda CA de Roost: «Roost Local CA (usuario@equipo)».</summary>
    public const string SubjectPrefix = "Roost Local CA (";

    /// <summary>La CA de Roost dura 10 años (y un día de margen hacia atrás): nada más largo.</summary>
    public static readonly TimeSpan MaxValidity = TimeSpan.FromDays(366 * 10 + 2);

    /// <summary>
    /// El certificado, si <paramref name="pem"/> es una CA de Roost; si no, null y en
    /// <paramref name="problem"/> el porqué (sin tildes: acaba en el registro del helper). Quien
    /// lo recibe lo libera. Las reglas, en orden:
    /// 1) tamaño: 16 KB como mucho;
    /// 2) un solo <c>BEGIN CERTIFICATE</c> y ninguna clave privada;
    /// 3) que sea X.509 legible;
    /// 4) CN que empiece por «Roost Local CA (»;
    /// 5) <c>BasicConstraints</c> con <c>CA:TRUE</c>;
    /// 6) <c>KeyUsage</c> con <c>KeyCertSign</c>;
    /// 7) autofirmado y en vigor (emisor = sujeto y su propia firma valida la cadena);
    /// 8) validez de 10 años como mucho.
    /// </summary>
    public static X509Certificate2? Parse(string pem, out string problem)
    {
        if (pem.Length > MaxPemLength)
        {
            problem = $"el PEM ocupa mas de {MaxPemLength / 1024} KB";
            return null;
        }
        if (CountOf(pem, "-----BEGIN CERTIFICATE-----") != 1 || pem.Contains("PRIVATE KEY", StringComparison.Ordinal))
        {
            problem = "el PEM tiene que traer exactamente un certificado y ninguna clave";
            return null;
        }

        X509Certificate2 cert;
        try
        {
            cert = X509Certificate2.CreateFromPem(pem);
        }
        catch (CryptographicException)
        {
            problem = "el PEM no es un certificado X.509 legible";
            return null;
        }

        var why = WhyNotRoostCa(cert);
        if (why is null)
        {
            problem = "";
            return cert;
        }
        cert.Dispose();
        problem = why;
        return null;
    }

    /// <summary>PURO: por qué <paramref name="cert"/> no es una CA de Roost, o null si lo es (las reglas 4 a 8 de <see cref="Parse"/>).</summary>
    public static string? WhyNotRoostCa(X509Certificate2 cert)
    {
        var cn = cert.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        if (!cn.StartsWith(SubjectPrefix, StringComparison.Ordinal))
            return $"el CN no es el de una CA de Roost ('{SubjectPrefix}...')";

        var basic = cert.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault();
        if (basic is not { CertificateAuthority: true })
            return "no es una CA (BasicConstraints sin CA:TRUE)";

        var usage = cert.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault();
        if (usage is null || !usage.KeyUsages.HasFlag(X509KeyUsageFlags.KeyCertSign))
            return "no puede firmar certificados (KeyUsage sin KeyCertSign)";

        if (cert.Subject != cert.Issuer || !IsSelfSignedAndValid(cert))
            return "no es autofirmado o no esta en vigor";

        if (cert.NotAfter - cert.NotBefore > MaxValidity)
            return "dura mas de 10 anos";

        return null;
    }

    /// <summary>La cadena del certificado con el propio certificado como única raíz: solo pasa si está firmado por sí mismo y en fecha.</summary>
    private static bool IsSelfSignedAndValid(X509Certificate2 cert)
    {
        try
        {
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(cert);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            return chain.Build(cert);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static int CountOf(string text, string needle)
    {
        var count = 0;
        for (var at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}
