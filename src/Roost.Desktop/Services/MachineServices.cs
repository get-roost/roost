using CommunityToolkit.Mvvm.ComponentModel;
using Roost.Core.Config;
using Roost.Core.Platform;
using Roost.Core.Scanning;

namespace Roost.Desktop.Services;

/// <summary>
/// Los servicios de Windows que pueden quitarle un puerto a Roost (E7): un MySQL o una MariaDB de
/// su MSI, Apache, IIS, otro Redis. Se leen en segundo plano y solo leyendo (el registro y
/// <c>sc query</c>), la primera vez que una pantalla los pide. Roost nunca los cambia: como mucho,
/// abre «Servicios» de Windows para que lo haga el usuario.
/// </summary>
public sealed partial class MachineServices : ObservableObject
{
    private Task? _loading;

    /// <summary>Los que hay en la máquina (todos los que molestarían con los puertos de siempre).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<WindowsService> Found { get; private set; } = [];

    [ObservableProperty]
    public partial bool Loaded { get; private set; }

    /// <summary>Los lee si aún no se han leído (o, con <paramref name="force"/>, otra vez). Nunca lanza.</summary>
    public Task LoadAsync(bool force = false)
    {
        if (!force && (_loading is not null || Loaded)) return _loading ?? Task.CompletedTask;
        return _loading = LoadCoreAsync();
    }

    private async Task LoadCoreAsync()
    {
        try
        {
            Found = await Task.Run(() => WindowsServices.Find(ProcessRunner.Default));
        }
        catch (Exception)
        {
            Found = [];
        }
        Loaded = true;
        _loading = null;
    }

    /// <summary>Para los tests de vistas: unos servicios sin leer la máquina.</summary>
    internal void Show(IReadOnlyList<WindowsService> services)
    {
        Found = services;
        Loaded = true;
    }

    /// <summary>Los que molestan con los puertos que va a usar este stack.</summary>
    public IReadOnlyList<ServiceConflict> ConflictsWith(RoostConfig config, bool withSites, bool withDb) =>
        WindowsServices.Conflicts(Found, config.PlannedPorts(withSites, withDb));

    /// <summary>«Servicios» de Windows (services.msc), donde el usuario los deja en Manual.</summary>
    public static void OpenServicesPanel() => Shell.OpenUrl("services.msc");

    /// <summary>«arranca con Windows y está en marcha»: el estado, con tildes, para la app.</summary>
    public static string StateText(WindowsService service) => (service.StartsWithWindows, service.Running) switch
    {
        (true, true) => "Arranca con Windows y está en marcha",
        (true, false) => "Arranca con Windows (ahora está parado)",
        (false, true) => "Está en marcha",
        _ => service.Start == ServiceStartMode.Disabled ? "Deshabilitado" : "Manual y parado",
    };
}
