using Roost.Core.Platform;
using Roost.Core.Sites;

namespace Roost.Core.Stack;

/// <summary>
/// Las CA en las que confía la extensión curl de PHP (<c>curl.cainfo</c> del php.ini generado).
/// Los zips de PHP para Windows no traen ninguna: sin esto, <c>curl_exec</c> (y Guzzle, Laravel o
/// WordPress) falla con «self-signed certificate in certificate chain» contra cualquier https, y
/// tampoco se fía de los <c>https://*.test</c> de Roost. Comprobado con PHP 8.4 y 7.4. Los streams
/// (<c>file_get_contents</c>) no lo necesitan: sin <c>openssl.cafile</c>, PHP en Windows comprueba
/// contra el almacén del sistema. Es la lista de Mozilla que publica curl.se (<c>Stack/cacert.pem</c>,
/// empaquetada en el ensamblado, MPL 2.0) más la CA local de Roost si ya existe.
/// </summary>
/// <remarks>
/// Para actualizar la lista: bajar <c>https://curl.se/ca/cacert.pem</c>, comprobarlo con su
/// <c>cacert.pem.sha256</c> y sustituir <c>src/Roost.Core/Stack/cacert.pem</c>. La de ahora es de
/// «Certificate data from Mozilla as of: Tue Oct 20 13:32:40 2026 GMT», con SHA-256
/// 796f1389bfea13c0ed78f663b765baaff54a508e5483786bd6ece73c5e3e3cbe.
/// </remarks>
public static class CaBundle
{
    /// <summary><c>etc/php/cacert.pem</c>: uno para todas las versiones de PHP.</summary>
    public static string FilePath(RoostPaths paths) => Path.Combine(paths.Etc, "php", "cacert.pem");

    /// <summary>
    /// Escribe el bundle (solo si cambia: lo llama cada php.ini) y devuelve su ruta. Si la CA de
    /// Roost se crea o se renueva después, el siguiente <c>up</c> la mete.
    /// </summary>
    public static string Write(RoostPaths paths)
    {
        var file = FilePath(paths);
        var text = Compose(Mozilla(), new LocalCa(paths).CaCertFile);
        if (!File.Exists(file) || File.ReadAllText(file) != text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, text);
        }
        return file;
    }

    /// <summary>PURO salvo leer la CA: la lista de Mozilla y, al final, la CA de Roost si existe.</summary>
    public static string Compose(string mozilla, string roostCaFile)
    {
        var text = "## Generado por Roost: NO lo edites, se regenera en cada `roost up`.\n"
                   + "## Las CA de Mozilla (curl.se) y, al final, la CA local de Roost.\n\n"
                   + mozilla.TrimEnd() + "\n";
        if (File.Exists(roostCaFile))
            text += "\nRoost Local CA\n==============\n" + File.ReadAllText(roostCaFile).Trim() + "\n";
        return text;
    }

    /// <summary>La lista de Mozilla empaquetada en Roost.Core.</summary>
    public static string Mozilla()
    {
        var asm = typeof(CaBundle).Assembly;
        var name = asm.GetManifestResourceNames().First(n => n.EndsWith("cacert.pem", StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("\r\n", "\n");
    }
}
