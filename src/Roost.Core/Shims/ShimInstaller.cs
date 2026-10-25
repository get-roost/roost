using System.Text.Json;
using Roost.Core.Install;
using Roost.Core.Platform;

namespace Roost.Core.Shims;

/// <summary>Lo que hizo <see cref="ShimInstaller.Enable"/>.</summary>
/// <param name="Tools">Los shims que llegan a la terminal (en Linux, los enlazados en <c>~/.local/bin</c>).</param>
/// <param name="Skipped">En Linux y macOS: los que no se enlazaron porque en <c>~/.local/bin</c> ya había algo del usuario con ese nombre.</param>
/// <param name="PathChanged">Windows: si el PATH del usuario cambió (false si ya lo tenía). Linux: si se puso algún enlace.</param>
public sealed record ShimActivation(IReadOnlyList<string> Tools, IReadOnlyList<string> Skipped, bool PathChanged);

/// <summary>
/// Pone y quita los shims en <see cref="RoostPaths.Shims"/>. El shim es <c>roost-shim.exe</c>
/// (va junto a <c>roost.exe</c>) copiado como <c>php.exe</c>, <c>composer.exe</c>, <c>node.exe</c>,
/// <c>npm.exe</c> y <c>npx.exe</c>. Publicado
/// con NativeAOT es un solo fichero; en una compilación normal lleva al lado su dll y sus
/// dependencias, y se copian también. Se cambia la carpeta entera de una vez: si un <c>php.exe</c>
/// está en uso, falla sin tocar nada.
/// <para>
/// En Linux y macOS, <c>bin/shims</c> lleva una sola copia de <c>roost-shim</c> y cada herramienta es
/// un enlace simbólico a ella (<c>php -&gt; roost-shim</c>): el shim sabe qué es por su <c>argv[0]</c>
/// (<see cref="TerminalPhp.ToolName"/>) y dónde está la raíz por su ubicación real. Lo que llega a la
/// terminal son otros enlaces en <c>~/.local/bin</c> (<see cref="UnixPath"/>), que hacen de PATH del usuario.
/// </para>
/// </summary>
public sealed class ShimInstaller(RoostPaths paths)
{
    public const string ShimName = "roost-shim";

    /// <summary>Los enlaces de <c>~/.local/bin</c> (Linux y macOS). En Windows no se usa.</summary>
    public UnixPath Links { get; init; } = UnixPath.Current;

    /// <summary>
    /// Los nombres con los que se instala el shim (decide qué ejecutar por su propio nombre):
    /// <c>php</c>; <c>composer</c>, que es <c>php composer.phar</c> con el PHP de la carpeta; y
    /// <c>node</c>, <c>npm</c> y <c>npx</c>, con el Node de la carpeta (<c>.nvmrc</c>, <c>.node-version</c>…).
    /// </summary>
    public static readonly IReadOnlyList<string> Tools = ["php", "composer", "node", "npm", "npx"];

    /// <summary>Los shims de Node: van juntos, y solo con un Node de Roost instalado.</summary>
    public static readonly IReadOnlyList<string> NodeTools = ["node", "npm", "npx"];

    /// <summary>
    /// Los que se ponen aquí: <c>composer</c> solo si el Composer de Roost está instalado, y
    /// <c>node</c>, <c>npm</c> y <c>npx</c> solo si hay algún Node de Roost. Si no, el shim taparía (va
    /// el primero en el PATH del usuario) el composer o el node que ya tuviera el usuario (el de Scoop,
    /// el de nvm-windows), con un error. Al instalar el de Roost, <see cref="AddMissing"/> los añade.
    /// </summary>
    public IReadOnlyList<string> Wanted
    {
        get
        {
            var composer = Composer.ComposerTool.Locate(paths) is not null;
            var node = new NodeInstaller(paths).ListInstalled().Count > 0;
            return Tools.Where(tool => tool == "composer" ? composer : !NodeTools.Contains(tool) || node).ToList();
        }
    }

    public string Directory => paths.Shims;

    /// <summary>
    /// La carpeta que tiene que estar en el PATH para que la terminal encuentre los shims: en Windows,
    /// <see cref="Directory"/> (va en el PATH del usuario); en Linux y macOS, <c>~/.local/bin</c>, con los
    /// enlaces a <see cref="Directory"/>.
    /// </summary>
    public string PathDirectory => OperatingSystem.IsWindows() ? Directory : Links.LinkDirectory;

    public bool IsInstalled => File.Exists(Path.Combine(Directory, "php.exe")) || File.Exists(Path.Combine(Directory, "php"));

    /// <summary>
    /// true si, además de puestos, llegan a la terminal: en Windows, su carpeta en el PATH del usuario; en
    /// Linux y macOS, el enlace <c>~/.local/bin/php</c> apunta a ellos. Lo que miran la casilla de Ajustes
    /// y <c>roost shims status</c>.
    /// </summary>
    public bool IsEnabled => IsInstalled && (OperatingSystem.IsWindows() ? UserPath.Contains(UserPath.Read(), Directory) : Links.IsRoostLink("php", Directory));

    /// <summary>
    /// Pone los shims y los hace llegar a la terminal: <see cref="Install"/> y, en Windows, la carpeta
    /// la primera en el PATH del usuario; en Linux y macOS, los enlaces de <c>~/.local/bin</c> (lo que
    /// ya hubiera ahí del usuario se respeta y vuelve en <c>Skipped</c>). Lo que usa la casilla de Ajustes.
    /// </summary>
    public ShimActivation Enable(string sourceDirectory)
    {
        Install(sourceDirectory);
        if (OperatingSystem.IsWindows())
            return new ShimActivation(Wanted, [], UserPath.AddFirst(Directory));
        var (linked, skipped) = Links.Link(Wanted, Directory);
        return new ShimActivation(linked, skipped, linked.Count > 0);
    }

    /// <summary>Lo contrario de <see cref="Enable"/>: quita los enlaces (o la carpeta del PATH), el bloque del perfil y los shims.</summary>
    public void Disable()
    {
        if (OperatingSystem.IsWindows())
        {
            UserPath.Remove(Directory);
        }
        else
        {
            Links.Unlink(Directory, Tools);
            Links.RemoveProfile();
        }
        Remove();
    }

    /// <summary>El fichero de un shim (<c>composer.exe</c> en Windows).</summary>
    public string ToolPath(string tool) => Path.Combine(Directory, OperatingSystem.IsWindows() ? tool + ".exe" : tool);

    /// <summary>true si el shim de <paramref name="tool"/> está puesto.</summary>
    public bool Has(string tool) => File.Exists(ToolPath(tool));

    /// <summary>En Linux y macOS, la copia única de <c>roost-shim</c> a la que apuntan los enlaces de <see cref="Directory"/>.</summary>
    public string UnixShim => Path.Combine(Directory, ShimName);

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
            if (OperatingSystem.IsWindows())
            {
                foreach (var tool in missing) File.Copy(exe, ToolPath(tool));
                // En una compilación normal, el shim necesita su dll al lado (con NativeAOT no hay ninguna).
                foreach (var file in Companions(sourceDirectory))
                {
                    var target = Path.Combine(Directory, file);
                    if (!File.Exists(target)) File.Copy(Path.Combine(sourceDirectory, file), target);
                }
            }
            else
            {
                if (!File.Exists(UnixShim)) File.Copy(exe, UnixShim);
                foreach (var tool in missing) File.CreateSymbolicLink(ToolPath(tool), ShimName);
                // Si los enlaces de ~/.local/bin están puestos, los nuevos también llegan a la terminal.
                if (Links.IsRoostLink("php", Directory)) Links.Link(missing, Directory);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new RoostException($"No se pudo poner el shim en {Directory}: {ex.Message}");
        }
        return missing;
    }

    /// <summary>
    /// Con los shims puestos, quita los que ya no tocan (los de Node al quitar el último Node de Roost:
    /// si no, <c>node</c> en la terminal sería un shim que solo da un error, delante del node del
    /// usuario). No toca el PATH ni los demás. Devuelve los que ha quitado.
    /// </summary>
    public IReadOnlyList<string> RemoveUnwanted()
    {
        if (!IsInstalled) return [];
        var wanted = Wanted;
        var removed = new List<string>();
        foreach (var tool in Tools.Where(t => t != "php" && !wanted.Contains(t) && Has(t)))
        {
            try
            {
                if (!OperatingSystem.IsWindows()) Links.Unlink(Directory, [tool]);
                File.Delete(ToolPath(tool));
                removed.Add(tool);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new RoostException($"No se pudo quitar el shim de {tool} ({ToolPath(tool)}): {ex.Message}. Cierra lo que lo este usando y repite.");
            }
        }
        return removed;
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
            if (OperatingSystem.IsWindows())
            {
                foreach (var tool in Wanted)
                    File.Copy(exe, Path.Combine(temp, tool + ".exe"));
                foreach (var file in Companions(sourceDirectory))
                    File.Copy(Path.Combine(sourceDirectory, file), Path.Combine(temp, file));
            }
            else
            {
                // Una copia y un enlace por herramienta (relativo: la carpeta se puede mover con la raíz).
                File.Copy(exe, Path.Combine(temp, ShimName));
                InstallFiles.MarkExecutable(Path.Combine(temp, ShimName));
                // En una compilación normal (sin NativeAOT), el apphost busca su dll junto a su ruta real.
                foreach (var file in Companions(sourceDirectory))
                    File.Copy(Path.Combine(sourceDirectory, file), Path.Combine(temp, file));
                foreach (var tool in Wanted)
                    File.CreateSymbolicLink(Path.Combine(temp, tool), ShimName);
            }

            if (System.IO.Directory.Exists(Directory))
                InstallFiles.RemoveAtomic(Directory, parent, "los shims");
            System.IO.Directory.Move(temp, Directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            InstallFiles.TryDeleteDirectory(temp);
            throw new RoostException($"No se pudieron poner los shims en {Directory}: {ex.Message}. Cierra las terminales que esten usando php o node y repite.");
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
            // En Linux, php es un enlace: se compara la copia a la que apunta (o el php de una version anterior, que era una copia).
            var installed = OperatingSystem.IsWindows() ? Path.Combine(Directory, "php.exe") : File.Exists(UnixShim) ? UnixShim : Path.Combine(Directory, "php");
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
