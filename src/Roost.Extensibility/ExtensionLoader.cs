using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;

namespace Roost.Extensibility;

/// <summary>
/// Carga las extensiones de una carpeta: cada subcarpeta es una extensión, con
/// <c>&lt;subcarpeta&gt;.dll</c> dentro y sus dependencias al lado. Cada una va en su propio
/// <see cref="AssemblyLoadContext"/>, pero lo que ya tiene Roost (Roost.Core, Roost.Extensibility,
/// Spectre, Avalonia…) se comparte, para que sus tipos sean los mismos que ve Roost. Nunca lanza:
/// lo que no carga va a los errores.
/// </summary>
public static partial class ExtensionLoader
{
    [GeneratedRegex("^[a-z0-9]([a-z0-9-]*[a-z0-9])?$")]
    private static partial Regex IdPattern();

    public static ExtensionSet Load(string folder)
    {
        var loaded = new List<IRoostExtension>();
        var errors = new List<ExtensionError>();
        if (!Directory.Exists(folder)) return new ExtensionSet(loaded, errors);

        foreach (var dir in Directory.EnumerateDirectories(folder).Order(StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(dir);
            if (name.StartsWith('.')) continue; // carpetas de trabajo, como en bin/
            var main = Path.Combine(dir, name + ".dll");
            if (!File.Exists(main))
            {
                errors.Add(new ExtensionError(name, $"no hay {name}.dll en {dir}"));
                continue;
            }

            try
            {
                var context = new ExtensionLoadContext(main);
                var assembly = context.LoadFromAssemblyPath(main);
                var types = assembly.GetExportedTypes()
                    .Where(t => typeof(IRoostExtension).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false }
                                && t.GetConstructor(Type.EmptyTypes) is not null)
                    .ToList();
                if (types.Count == 0)
                {
                    errors.Add(new ExtensionError(name, "no tiene ninguna clase publica que implemente IRoostExtension"));
                    continue;
                }

                foreach (var type in types)
                {
                    var extension = (IRoostExtension)Activator.CreateInstance(type)!;
                    if (Validate(extension) is { } problem)
                        errors.Add(new ExtensionError(name, problem));
                    else if (loaded.Any(e => e.Id == extension.Id))
                        errors.Add(new ExtensionError(name, $"ya hay otra extension con el id '{extension.Id}'"));
                    else
                        loaded.Add(extension);
                }
            }
            catch (Exception ex)
            {
                // Una dll rota, de otra versión de .NET o que lanza en su constructor.
                var inner = ex is TargetInvocationException { InnerException: { } cause } ? cause : ex;
                errors.Add(new ExtensionError(name, $"no carga: {inner.Message}"));
            }
        }
        return new ExtensionSet(loaded, errors);
    }

    private static string? Validate(IRoostExtension extension)
    {
        if (extension.ApiVersion > ExtensionApi.Current)
            return $"{extension.Name} pide la version {extension.ApiVersion} del contrato y este Roost tiene la {ExtensionApi.Current}: actualiza Roost";
        if (string.IsNullOrWhiteSpace(extension.Id) || !IdPattern().IsMatch(extension.Id))
            return $"el id '{extension.Id}' no vale: minusculas, numeros y guiones";
        return null;
    }

    /// <summary>
    /// El contexto de una extensión: lo que ya tiene el contexto de Roost se comparte; lo demás se
    /// busca en la carpeta de la extensión (con su deps.json si lo trae).
    /// </summary>
    private sealed class ExtensionLoadContext(string mainAssembly) : AssemblyLoadContext(Path.GetFileNameWithoutExtension(mainAssembly))
    {
        private readonly AssemblyDependencyResolver _resolver = new(mainAssembly);
        private readonly string _folder = Path.GetDirectoryName(mainAssembly)!;

        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name == Path.GetFileNameWithoutExtension(mainAssembly)) return null;
            try
            {
                return Default.LoadFromAssemblyName(name);
            }
            catch (FileNotFoundException)
            {
                // No es de Roost: es una dependencia propia de la extensión.
            }
            var path = _resolver.ResolveAssemblyToPath(name) ?? Path.Combine(_folder, name.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
        }
    }
}
