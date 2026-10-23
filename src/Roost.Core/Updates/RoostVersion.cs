using System.Reflection;

namespace Roost.Core.Updates;

/// <summary>
/// La versión de Roost que se está ejecutando. Sale de <c>Directory.Build.props</c> (una sola
/// fuente para todos los ensamblados) y se lee del <see cref="AssemblyInformationalVersionAttribute"/>
/// sin lo que va tras <c>+</c> (el SDK le añade el commit: <c>0.1.0+918291e…</c>).
/// </summary>
public static class RoostVersion
{
    /// <summary>La de este Roost: <c>0.1.0</c>, <c>0.2.0-beta.1</c>…</summary>
    public static string Current { get; } = Of(typeof(RoostVersion).Assembly);

    /// <summary>La versión de un ensamblado, en el mismo formato.</summary>
    public static string Of(Assembly assembly)
    {
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational)) return informational.Split('+')[0].Trim();
        return assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}" : "0.0.0";
    }
}
