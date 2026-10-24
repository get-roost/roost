using Avalonia.Controls;
using Roost.Desktop.ViewModels;
using Roost.Extensibility;

namespace Roost.Desktop.Extensibility;

/// <summary>Dónde sale una pantalla de extensión en la barra lateral.</summary>
public enum DesktopSection
{
    /// <summary>Con PHP, Servidor web, Bases de datos, Redis y Correo (un Memcached, un Meilisearch).</summary>
    Components,

    /// <summary>Con Auditoría, Registros y Ajustes.</summary>
    Roost,
}

/// <summary>
/// Una pantalla de una extensión. <paramref name="IconPath"/> es una geometría en una caja de 24×24,
/// en el formato de los iconos de Roost (Theme/Icons.axaml). <paramref name="CreateView"/> crea la
/// vista de <paramref name="Page"/>; la app la enseña como las suyas.
/// </summary>
public sealed record DesktopPage(string Label, DesktopSection Section, string IconPath, PageViewModel Page, Func<Control> CreateView);

/// <summary>
/// Pantallas nuevas para la app (ROOST.md §12.4). La extensión recibe el <see cref="MainViewModel"/>
/// para usar lo mismo que las pantallas de Roost: los diálogos, <c>RunAsync</c>, el estado del stack.
/// </summary>
public interface IDesktopExtension : IRoostExtension
{
    IEnumerable<DesktopPage> Pages(MainViewModel main);
}
