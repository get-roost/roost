using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Roost.Core.Install;

/// <summary>
/// Las reglas de ROOST.md §5.4, comunes a todo lo que instala Roost (PHP, nginx…):
/// descarga con SHA-256 verificado ANTES de extraer y con caché, extracción a una carpeta
/// temporal que se renombra al final, y borrado que primero renombra (si algo está en uso,
/// falla sin tocar nada). Las carpetas de trabajo empiezan por punto.
/// </summary>
internal static class InstallFiles
{
    public const string TempPrefix = ".tmp-";
    public const string TrashPrefix = ".trash-";

    /// <summary>
    /// Devuelve la ruta del zip verificado en <paramref name="downloadsDir"/>: lo reutiliza si
    /// ya estaba y su SHA-256 cuadra, y si no lo descarga a un <c>.part-…</c> y lo verifica.
    /// <paramref name="urls"/> se prueban en orden: si una da 404 se pasa a la siguiente (el CDN
    /// de MySQL mueve cada release de Downloads/ a archives/ cuando sale la siguiente).
    /// </summary>
    public static async Task<string> DownloadVerifiedAsync(
        HttpClient http,
        IReadOnlyList<Uri> urls,
        string fileName,
        string sha256,
        string downloadsDir,
        IProgress<long>? downloaded,
        CancellationToken cancellation)
    {
        Directory.CreateDirectory(downloadsDir);
        // GetFileName: el nombre viene de un catálogo y no debe poder escribir fuera de la caché.
        var final = Path.Combine(downloadsDir, Path.GetFileName(fileName));

        if (File.Exists(final))
        {
            if (await Sha256OfAsync(final, cancellation) == sha256) return final;
            TryDeleteFile(final);
        }

        var part = final + ".part-" + Guid.NewGuid().ToString("N");
        var url = urls[0];
        try
        {
            HttpResponseMessage? found = null;
            foreach (var candidate in urls)
            {
                found?.Dispose();
                url = candidate;
                found = await http.GetAsync(candidate, HttpCompletionOption.ResponseHeadersRead, cancellation);
                if (found.StatusCode != System.Net.HttpStatusCode.NotFound) break;
            }

            using (var response = found!)
            {
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync(cancellation);
                await using var file = File.Create(part);

                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellation)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellation);
                    total += read;
                    downloaded?.Report(total);
                }
            }

            var actual = await Sha256OfAsync(part, cancellation);
            if (actual != sha256)
            {
                throw new RoostException(
                    $"El SHA-256 de {fileName} no coincide con el del catalogo "
                    + $"(esperado {sha256}, obtenido {actual}). No se instala nada.");
            }

            File.Move(part, final, overwrite: true);
            return final;
        }
        catch (Exception ex)
        {
            TryDeleteFile(part);
            if (ex is RoostException or OperationCanceledException) throw;
            throw new RoostException($"No se pudo descargar {url}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Extrae <paramref name="zip"/> (un .zip o, en Linux, un .tar.gz) en <paramref name="target"/> sin
    /// dejar nunca una carpeta a medias. Con <paramref name="stripSingleRoot"/>, si el zip lo mete todo
    /// en una única carpeta (nginx-1.30.6/…), se instala su contenido. Con <paramref name="fromFolder"/>, solo
    /// se instala esa carpeta del zip (el de Apache Lounge trae <c>Apache24/</c> y, al lado, un
    /// ReadMe). Exige <paramref name="requiredFile"/>.
    /// </summary>
    public static void ExtractAtomic(string zip, string target, string requiredFile, bool stripSingleRoot, string label, string? fromFolder = null)
    {
        var parent = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(parent);
        var temp = Path.Combine(parent, TempPrefix + Guid.NewGuid().ToString("N"));
        try
        {
            Extract(zip, temp);

            var source = temp;
            if (fromFolder is not null)
            {
                source = Path.Combine(temp, fromFolder);
                if (!Directory.Exists(source))
                    throw new RoostException($"{Kind(zip)} {label} no trae la carpeta {fromFolder}; no se instala.");
            }
            else if (stripSingleRoot
                && Directory.GetFiles(temp).Length == 0
                && Directory.GetDirectories(temp) is [var only])
            {
                source = only;
            }

            if (!File.Exists(Path.Combine(source, requiredFile)))
                throw new RoostException($"{Kind(zip)} {label} no trae {requiredFile}; no se instala.");

            // Renombrar es atómico dentro del mismo volumen: o queda instalado entero o no queda.
            MoveDirectory(source, target);
        }
        catch (Exception ex)
        {
            if (ex is RoostException) throw;
            throw new RoostException($"No se pudo extraer {label} en {parent}: {ex.Message}", ex);
        }
        finally
        {
            TryDeleteDirectory(temp);
        }
    }

    /// <summary>¿Es un .tar.gz (los paquetes de Linux)? Si no, se trata como un zip.</summary>
    public static bool IsTarGz(string archive) =>
        archive.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) || archive.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase);

    private static string Kind(string archive) => IsTarGz(archive) ? "El tar.gz" : "El zip";

    // Los dos rechazan entradas que se salgan de la carpeta (zip slip); el tar conserva además los
    // permisos y los enlaces simbólicos, que en Linux hacen falta (bin/php ejecutable, lib/*.so.N).
    private static void Extract(string archive, string destination)
    {
        if (!IsTarGz(archive))
        {
            ZipFile.ExtractToDirectory(archive, destination);
            return;
        }
        Directory.CreateDirectory(destination);
        using var file = File.OpenRead(archive);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        TarFile.ExtractToDirectory(gzip, destination, overwriteFiles: false);
    }

    /// <summary>
    /// En Linux y macOS, deja <paramref name="path"/> ejecutable (rwxr-xr-x): para los binarios que se
    /// bajan sueltos, sin un tar que traiga sus permisos (cloudflared). En Windows no hace nada.
    /// </summary>
    public static void MarkExecutable(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                                   | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                                   | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    /// <summary>
    /// <c>Directory.Move</c> con reintentos. Justo después de extraer, el antivirus (o el indexador)
    /// puede tener abierto un fichero recién escrito, y Windows deniega el renombrado unos instantes
    /// («Access to the path … is denied»). Se reintenta hasta ~4,5 s; si el destino ya existe, no.
    /// </summary>
    internal static void MoveDirectory(string source, string target)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Move(source, target);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 10 && !Directory.Exists(target))
            {
                Thread.Sleep(100 * attempt);
            }
        }
    }

    /// <summary>Quita <paramref name="dir"/>, que tiene que estar bajo <paramref name="parent"/>.</summary>
    public static void RemoveAtomic(string dir, string parent, string label)
    {
        dir = Path.GetFullPath(dir);
        var root = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!dir.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new RoostException($"{dir} no esta bajo {parent}: Roost solo quita lo que instalo el.");

        var trash = Path.Combine(parent, TrashPrefix + Guid.NewGuid().ToString("N"));
        try
        {
            MoveDirectory(dir, trash);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new RoostException(
                $"No se pudo quitar {label}: {ex.Message} Comprueba que ningun proceso lo este usando (roost down).", ex);
        }

        // Si el borrado deja algo, es una carpeta con punto que se limpia en la siguiente operación.
        TryDeleteDirectory(trash);
    }

    /// <summary>Borra temporales y papeleras de operaciones anteriores que no terminaron.</summary>
    public static void CleanLeftovers(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var child in Directory.EnumerateDirectories(dir))
        {
            var name = Path.GetFileName(child);
            if (name.StartsWith(TempPrefix, StringComparison.Ordinal) || name.StartsWith(TrashPrefix, StringComparison.Ordinal))
                TryDeleteDirectory(child);
        }
    }

    private static async Task<string> Sha256OfAsync(string path, CancellationToken cancellation)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellation));
    }

    public static void TryDeleteDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return;
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (UnauthorizedAccessException)
            {
                // Un clon a medias deja ficheros de solo lectura (.git/objects): se les quita el
                // atributo y se vuelve a intentar.
                foreach (var file in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(path, recursive: true);
            }
        }
        catch { /* se reintenta en la siguiente operación (CleanLeftovers) */ }
    }

    public static void TryDeleteFile(string path)
    {
        try { File.Delete(path); }
        catch { /* best effort */ }
    }
}
