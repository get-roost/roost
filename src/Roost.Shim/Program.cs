using System.Diagnostics;
using Roost.Core;
using Roost.Core.Composer;
using Roost.Core.Install;
using Roost.Core.Node;
using Roost.Core.Platform;
using Roost.Core.Shims;

namespace Roost.Shim;

/// <summary>
/// El shim: elige el PHP de esta carpeta y lo ejecuta con los argumentos tal cual llegaron,
/// heredando la consola, y devuelve su código de salida. Ctrl+C le llega al PHP (comparten
/// consola); el shim solo espera a que acabe. Copiado como <c>composer.exe</c>, ejecuta
/// <c>&lt;ese php&gt; &lt;composer.phar de Roost&gt; &lt;argumentos&gt;</c>. Copiado como
/// <c>node.exe</c>, <c>npm.exe</c> o <c>npx.exe</c>, hace lo mismo con el Node de la carpeta
/// (<see cref="TerminalNode"/>: <c>.nvmrc</c>, <c>.node-version</c>, <c>engines.node</c> o el global).
/// <para>
/// Qué herramienta es lo dice <c>argv[0]</c> (<see cref="TerminalPhp.ToolName"/>), no la ruta del
/// ejecutable: en Linux los shims son enlaces simbólicos y <c>Environment.ProcessPath</c> resuelve el
/// enlace al <c>roost-shim</c> de verdad. La raíz de Roost sí sale de la ubicación real
/// (<see cref="PathsFor"/>). Los argumentos: en Windows, la línea de órdenes tal cual (volver a
/// entrecomillar argv no es reversible); en Linux, el <c>argv</c> de verdad por <c>ArgumentList</c>
/// (<c>Environment.CommandLine</c> ahí es una reconstrucción que pierde las comillas).
/// </para>
/// </summary>
internal static class Program
{
    public static int Main(string[] args)
    {
        var argv = Environment.GetCommandLineArgs();
        var self = Environment.ProcessPath ?? (argv.Length > 0 ? argv[0] : "php");
        var tool = TerminalPhp.ToolName(argv.Length > 0 ? argv[0] : null);
        var node = ShimInstaller.NodeTools.Contains(tool);
        try
        {
            var paths = PathsFor(self);
            var start = node ? NodeStart(paths, tool, args) : PhpStart(paths, tool, args);

            Console.CancelKeyPress += (_, e) => e.Cancel = true;
            using var child = Process.Start(start) ?? throw new RoostException($"No se pudo lanzar {start.FileName}.");
            child.WaitForExit();
            return child.ExitCode;
        }
        catch (RoostException ex)
        {
            Console.Error.WriteLine($"roost: {ex.Message}");
            return 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine($"roost: no se pudo ejecutar {(node ? "Node" : "PHP")}: {ex.Message}");
            return 1;
        }
    }

    /// <summary><c>php</c>, <c>php-cgi</c> o <c>composer</c>, con el PHP de la carpeta y su php.ini.</summary>
    private static ProcessStartInfo PhpStart(RoostPaths paths, string tool, string[] args)
    {
        var choice = TerminalPhp.Resolve(paths, Environment.CurrentDirectory, Environment.GetEnvironmentVariable(TerminalPhp.VersionVariable));
        string target;
        string[] leading = [];
        if (tool == "composer")
        {
            // Desde la terminal Composer puede preguntar (no se pone COMPOSER_NO_INTERACTION).
            var composer = ComposerTool.Locate(paths)
                ?? throw new RoostException("Composer no esta instalado en Roost. Instalalo con: roost composer install");
            ComposerTool.CheckPhp(choice.Php);
            target = choice.Php.Executable;
            leading = [composer.Phar];
        }
        else
        {
            target = tool == "php-cgi" ? choice.Php.CgiExecutable : choice.Php.Executable;
        }

        var start = new ProcessStartInfo(target) { UseShellExecute = false };
        WithArguments(start, leading, args);
        foreach (var (name, value) in TerminalPhp.Environment(paths, choice.Php))
            start.Environment[name] = value;
        return start;
    }

    /// <summary>
    /// <c>node</c>, o <c>npm</c> y <c>npx</c> como hace el <c>npm.cmd</c> del zip: <c>node.exe</c> con
    /// el <c>npm-cli.js</c> (o el <c>npx-cli.js</c>) que trae ese Node. A diferencia de <c>npm.cmd</c>, no
    /// busca antes un npm en el prefix global (<c>npm-prefix.js</c>): con un <c>prefix</c> en el
    /// <c>.npmrc</c> del usuario, ese sería el npm de otro Node. Un <c>npm install -g npm</c> con el Node
    /// de Roost actualiza el de su carpeta, que es el que usa el shim.
    /// </summary>
    private static ProcessStartInfo NodeStart(RoostPaths paths, string tool, string[] args)
    {
        var choice = TerminalNode.Resolve(paths, Environment.CurrentDirectory, Environment.GetEnvironmentVariable(TerminalNode.VersionVariable));
        var node = choice.Node;
        string[] leading = tool switch
        {
            "npm" => [Script(node, node.NpmCli, "npm")],
            "npx" => [Script(node, node.NpxCli, "npx")],
            _ => [],
        };
        var start = new ProcessStartInfo(node.Executable) { UseShellExecute = false };
        WithArguments(start, leading, args);
        foreach (var (name, value) in TerminalNode.Environment(paths, node))
            start.Environment[name] = value;
        return start;
    }

    /// <summary>
    /// Los argumentos del hijo: lo que va delante (el phar de Composer, el <c>npm-cli.js</c>) y lo que
    /// llegó al shim. En Windows, la línea de órdenes tal cual (<see cref="TerminalPhp.ArgumentsAfterProgram"/>:
    /// comillas dentro de <c>-r</c>, rutas con espacios…); en Linux, cada argumento por separado.
    /// </summary>
    private static void WithArguments(ProcessStartInfo start, string[] leading, string[] args)
    {
        if (OperatingSystem.IsWindows())
        {
            var rest = TerminalPhp.ArgumentsAfterProgram(Environment.CommandLine);
            start.Arguments = leading.Length == 0 ? rest : ComposerTool.ShimArguments(leading[0], rest);
            return;
        }
        foreach (var arg in leading) start.ArgumentList.Add(arg);
        foreach (var arg in args) start.ArgumentList.Add(arg);
    }

    private static string Script(InstalledNode node, string script, string tool) =>
        File.Exists(script) ? script : throw new RoostException($"{node.Label} no trae {tool} ({script}). Vuelve a instalarlo: roost node remove {node.Version} y roost node install {node.Version}");

    /// <summary>
    /// Las rutas de Roost: si el shim está en <c>&lt;raíz&gt;/bin/shims</c> (su ubicación real, resuelto
    /// cualquier enlace), las de esa raíz (así un Roost de pruebas con ROOST_HOME tiene sus propios
    /// shims); si no, las de siempre.
    /// </summary>
    private static RoostPaths PathsFor(string self)
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(RoostPaths.HomeVariable))) return RoostPaths.Default();
        var shims = new DirectoryInfo(Path.GetDirectoryName(self)!);
        return shims is { Name: "shims", Parent: { Name: "bin", Parent: { } root } } ? RoostPaths.ForRoot(root.FullName) : RoostPaths.Default();
    }
}
