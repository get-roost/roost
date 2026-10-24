using System.Text.Json;
using Roost.Core.Install;
using Roost.Core.Platform;

namespace Roost.Core.Shims;

/// <summary>
/// Pone y quita los shims en <see cref="RoostPaths.Shims"/>. El shim es <c>roost-shim.exe</c>
/// (va junto a <c>roost.exe</c>) copiado como <c>php.exe</c>. Publicado con NativeAOT es un solo
/// fichero; en una compilación normal lleva al lado su dll y sus dependencias, y se copian también.
/// Se cambia la carpeta entera de una vez: si un <c>php.exe</c> está en uso, falla sin tocar nada.
/// </summary>
public sealed class ShimInstaller(RoostPaths paths)
{
    public const string ShimName = "roost-shim";

    /// <summary>Los nombres con los que se instala el shim (decide qué ejecutar por su propio nombre).</summary>
    public static readonly IReadOnlyList<string> Tools = ["php"];

    public string Directory => paths.Shims;

    public bool IsInstalled => File.Exists(Path.Combine(Directory, "php.exe")) || File.Exists(Path.Combine(Directory, "php"));

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
            foreach (var tool in Tools)
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
