using System.Text.Json;
using Roost.Core.Install;
using Roost.Core.Platform;

namespace Roost.Core.Shims;

/// <summary>
/// Pone y quita los shims en <see cref="RoostPaths.Shims"/>. El shim es <c>roost-shim.exe</c>
/// (va junto a <c>roost.exe</c>) copiado como <c>php.exe</c> y como <c>composer.exe</c>. Publicado
/// con NativeAOT es un solo fichero; en una compilación normal lleva al lado su dll y sus
/// dependencias, y se copian también. Se cambia la carpeta entera de una vez: si un <c>php.exe</c>
/// está en uso, falla sin tocar nada.
/// </summary>
public sealed class ShimInstaller(RoostPaths paths)
{
    public const string ShimName = "roost-shim";

    /// <summary>
    /// Los nombres con los que se instala el shim (decide qué ejecutar por su propio nombre):
    /// <c>php</c> y <c>composer</c>, que es <c>php composer.phar</c> con el PHP de la carpeta.
    /// </summary>
    public static readonly IReadOnlyList<string> Tools = ["php", "composer"];

    /// <summary>
    /// Los que se ponen aquí: <c>composer</c> solo si el Composer de Roost está instalado. Si no, el
    /// shim taparía (va el primero en el PATH del usuario) el composer que ya tuviera el usuario, por
    /// ejemplo el de Scoop, con un error. Al instalar el de Roost, <see cref="AddMissing"/> lo añade.
    /// </summary>
    public IReadOnlyList<string> Wanted =>
        Composer.ComposerTool.Locate(paths) is null ? Tools.Where(tool => tool != "composer").ToList() : Tools;

    public string Directory => paths.Shims;

    public bool IsInstalled => File.Exists(Path.Combine(Directory, "php.exe")) || File.Exists(Path.Combine(Directory, "php"));

    /// <summary>El fichero de un shim (<c>composer.exe</c> en Windows).</summary>
    public string ToolPath(string tool) => Path.Combine(Directory, OperatingSystem.IsWindows() ? tool + ".exe" : tool);

    /// <summary>true si el shim de <paramref name="tool"/> está puesto.</summary>
    public bool Has(string tool) => File.Exists(ToolPath(tool));

    /// <summary>
    /// Con los shims ya puestos, añade los que falten de <see cref="Wanted"/> (los de una versión de
    /// Roost anterior no tenían <c>composer</c>) sin tocar los demás ni el PATH. Sin shims puestos no
    /// hace nada: ponerlos es decisión del usuario (<c>roost shims install</c>). Devuelve los que ha añadido.
    /// </summary>
    public IReadOnlyList<string> AddMissing(string sourceDirectory)
    {
        if (!IsInstalled) return [];
        var missing = Wanted.Where(tool => !Has(tool)).ToList();
        if (missing.Count == 0) return [];

        var exe = Path.Combine(sourceDirectory, OperatingSystem.IsWindows() ? ShimName + ".exe" : ShimName);
        if (!File.Exists(exe))
            throw new RoostException($"No se encuentra {exe}, que es el shim. Vuelve a instalar Roost.");
        try
        {
            foreach (var tool in missing) File.Copy(exe, ToolPath(tool));
            // En una compilación normal, el shim necesita su dll al lado (con NativeAOT no hay ninguna).
            foreach (var file in Companions(sourceDirectory))
            {
                var target = Path.Combine(Directory, file);
                if (!File.Exists(target)) File.Copy(Path.Combine(sourceDirectory, file), target);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new RoostException($"No se pudo poner el shim en {Directory}: {ex.Message}");
        }
        return missing;
    }

    /// <summary>Copia el shim de <paramref name="sourceDirectory"/> (la carpeta de roost.exe).</summary>
    public void Install(string sourceDirectory)
    {
        var exeName = OperatingSystem.IsWindows() ? ShimName + ".exe" : ShimName;
        var exe = Path.Combine(sourceDirectory, exeName);
        if (!File.Exists(exe))
            throw new RoostException($"No se encuentra {exe}, que es el shim de php. Vuelve a instalar Roost.");

        var parent = Path.GetDirectoryName(Directory)!;
        System.IO.Directory.CreateDirectory(parent);
        InstallFiles.CleanLeftovers(parent);
        var temp = Path.Combine(parent, InstallFiles.TempPrefix + "shims-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(temp);
        try
        {
            foreach (var tool in Wanted)
                File.Copy(exe, Path.Combine(temp, OperatingSystem.IsWindows() ? tool + ".exe" : tool));
            foreach (var file in Companions(sourceDirectory))
                File.Copy(Path.Combine(sourceDirectory, file), Path.Combine(temp, file));

            if (System.IO.Directory.Exists(Directory))
                InstallFiles.RemoveAtomic(Directory, parent, "los shims");
            System.IO.Directory.Move(temp, Directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            InstallFiles.TryDeleteDirectory(temp);
            throw new RoostException($"No se pudieron poner los shims en {Directory}: {ex.Message}. Cierra las terminales que esten usando php y repite.");
        }
        catch
        {
            InstallFiles.TryDeleteDirectory(temp);
            throw;
        }
    }

    public void Remove()
    {
        if (!System.IO.Directory.Exists(Directory)) return;
        InstallFiles.RemoveAtomic(Directory, Path.GetDirectoryName(Directory)!, "los shims");
    }

    /// <summary>
    /// Si los shims puestos son de otra Roost: el <c>php.exe</c> instalado no es byte a byte el
    /// <c>roost-shim.exe</c> de <paramref name="sourceDirectory"/>. Tras actualizar Roost, la app
    /// los renueva al abrirse. False si no están puestos o falta el shim de origen. Nunca lanza.
    /// </summary>
    public bool IsOutdated(string sourceDirectory)
    {
        try
        {
            var installed = Path.Combine(Directory, OperatingSystem.IsWindows() ? "php.exe" : "php");
            var source = Path.Combine(sourceDirectory, OperatingSystem.IsWindows() ? ShimName + ".exe" : ShimName);
            if (!File.Exists(installed) || !File.Exists(source)) return false;
            if (new FileInfo(installed).Length != new FileInfo(source).Length) return true;
            return !Sha256(installed).SequenceEqual(Sha256(source));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static byte[] Sha256(string file)
    {
        using var stream = File.OpenRead(file);
        return System.Security.Cryptography.SHA256.HashData(stream);
    }

    /// <summary>
    /// Lo que necesita al lado un shim sin NativeAOT: su dll, su runtimeconfig, su deps.json y
    /// las dll que este nombra. Con NativeAOT no hay deps.json y la lista sale vacía.
    /// </summary>
    public static IReadOnlyList<string> Companions(string sourceDirectory)
    {
        var deps = Path.Combine(sourceDirectory, ShimName + ".deps.json");
        if (!File.Exists(Path.Combine(sourceDirectory, ShimName + ".dll")) || !File.Exists(deps)) return [];

        var files = new List<string> { ShimName + ".dll", ShimName + ".deps.json" };
        if (File.Exists(Path.Combine(sourceDirectory, ShimName + ".runtimeconfig.json"))) files.Add(ShimName + ".runtimeconfig.json");

        using var doc = JsonDocument.Parse(File.ReadAllText(deps));
        foreach (var target in doc.RootElement.GetProperty("targets").EnumerateObject())
        {
            foreach (var library in target.Value.EnumerateObject())
            {
                if (!library.Value.TryGetProperty("runtime", out var runtime)) continue;
                foreach (var asset in runtime.EnumerateObject())
                {
                    var name = Path.GetFileName(asset.Name);
                    if (!files.Contains(name, StringComparer.OrdinalIgnoreCase) && File.Exists(Path.Combine(sourceDirectory, name)))
                        files.Add(name);
                }
            }
        }
        return files;
    }
}
