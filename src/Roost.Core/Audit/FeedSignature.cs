using System.Security.Cryptography;

namespace Roost.Core.Audit;

/// <summary>
/// La firma del feed de avisos (roost-catalog, ROOST.md §7.3): ECDSA P-256 sobre SHA-256, en DER,
/// sobre los bytes exactos de <c>advisories.json</c>. Va en la librería estándar de .NET (sin
/// dependencias) y se comprueba igual sin Roost:
/// <c>openssl dgst -sha256 -verify roost-catalog.pub.pem -signature advisories.json.sig advisories.json</c>.
/// </summary>
public static class FeedSignature
{
    /// <summary>
    /// Claves públicas en las que confía Roost (SubjectPublicKeyInfo en base64). La privada es el
    /// secreto <c>ROOST_CATALOG_KEY</c> del workflow del catálogo. Para rotarla, una versión de Roost
    /// lleva la nueva y la vieja a la vez, y solo después cambia el secreto.
    /// </summary>
    public static readonly IReadOnlyList<string> TrustedKeys =
    [
        // La del workflow del catálogo (23-oct-2026).
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEm7LNtFHeYPyRKoPvxzX3YiwgJdNgHd40OQ4EgJnD2VReMbF5sZn7XSGsUjqxU3/frKogKFDj4uLSBv7EZt7cjQ==",
    ];

    /// <summary>true si <paramref name="signature"/> es de <paramref name="data"/> con alguna de las claves (por defecto, las de confianza).</summary>
    public static bool Verify(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature, IEnumerable<string>? keys = null)
    {
        if (signature.Length is 0 or > 256) return false;
        foreach (var key in keys ?? TrustedKeys)
        {
            try
            {
                using var ecdsa = ECDsa.Create();
                ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(key), out _);
                if (ecdsa.KeySize != 256) continue;
                if (ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
                    return true;
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                // Una clave rota no invalida a las demás.
            }
        }
        return false;
    }

    /// <summary>Firma <paramref name="data"/> con la clave privada (lo usa el generador del catálogo).</summary>
    public static byte[] Sign(ReadOnlySpan<byte> data, ECDsa key)
        => key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

    /// <summary>La clave pública de <paramref name="key"/> como la guarda <see cref="TrustedKeys"/>.</summary>
    public static string PublicKeyOf(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

    /// <summary>Una clave de <see cref="TrustedKeys"/> en PEM, para verificar con OpenSSL.</summary>
    public static string ToPem(string publicKey)
        => PemEncoding.WriteString("PUBLIC KEY", Convert.FromBase64String(publicKey)) + "\n";
}
