using Roost.Core.Models;

namespace Roost.Core.Scanning;

/// <summary>
/// Contrato de un detector de UN tipo de componente. Cada implementación sabe
/// buscar en las rutas típicas de su componente en el SO actual y devolver las
/// instalaciones que encuentre. Tiene que ser seguro llamarlo aunque no haya nada.
/// </summary>
public interface IComponentScanner
{
    /// <summary>Qué tipo de componente detecta este scanner.</summary>
    ComponentKind Kind { get; }

    /// <summary>true si este scanner aplica al SO actual.</summary>
    bool SupportsCurrentOs { get; }

    /// <summary>
    /// Devuelve las instalaciones encontradas. NO debe lanzar por rutas
    /// inexistentes o binarios que no responden: se ignoran y se sigue.
    /// </summary>
    IEnumerable<DetectedComponent> Scan();
}
