using System.Runtime.CompilerServices;
using Roost.Core.Install;
using Roost.Core.Stack;

namespace Roost.Daemon.Tests;

/// <summary>
/// En Linux, el Apache de Roost es el de la distro (<see cref="ApacheCatalog.Linux"/>, que
/// <c>ApacheInstaller.Find</c> busca en <c>/usr/sbin</c>): los tests no pueden depender de si la máquina
/// tiene <c>apache2</c> (la 24.04 de pruebas y ubuntu-latest lo tienen; un contenedor, no). Antes de
/// cualquier test se deja uno de pega en una carpeta temporal (un «apache2» que no es ejecutable y los
/// <c>mod_*.so</c> que exige <see cref="ApacheConf.RequiredModules"/>, vacíos) y se apunta
/// <c>ROOST_APACHE</c> y <c>ROOST_APACHE_MODULES</c> a él, que <c>LinuxApache.Locate</c> lee en cada
/// llamada. El <c>-t</c> y la recarga los fingen los runners de cada test, como siempre. En Windows no
/// hace nada: ahí el Apache es el zip de Roost en <c>bin/apache/2.4.69</c>, que cada test toca.
/// </summary>
internal static class FakeDistroApache
{
    public static string? Directory { get; private set; }

    [ModuleInitializer]
    internal static void Install()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "roost-apache-distro-" + Environment.ProcessId);
        var bin = Path.Combine(root, "bin");
        var modules = Path.Combine(root, "modules");
        System.IO.Directory.CreateDirectory(bin);
        System.IO.Directory.CreateDirectory(modules);
        File.WriteAllText(Path.Combine(bin, "apache2"), "");
        foreach (var module in ApacheConf.RequiredModules.Concat(["dir", "mime", "headers", "alias"]))
            File.WriteAllText(Path.Combine(modules, $"mod_{module}.so"), "");
        Environment.SetEnvironmentVariable(LinuxApache.ExecutableVariable, Path.Combine(bin, "apache2"));
        Environment.SetEnvironmentVariable(LinuxApache.ModulesVariable, modules);
        Directory = root;
    }
}
