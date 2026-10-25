using CommunityToolkit.Mvvm.ComponentModel;
using Roost.Core.Config;
using Roost.Core.Platform;
using Roost.Core.Scanning;

namespace Roost.Desktop.Services;

/// <summary>
/// Los servicios del sistema que pueden quitarle un puerto a Roost (E7): en Windows, un MySQL o una
/// MariaDB de su MSI, Apache, IIS, otro Redis (el registro y <c>sc query</c>); en Linux, las unidades
/// de systemd de la distro (mariadb, mysql, apache2, nginx, redis). Se leen en segundo plano y solo
/// leyendo (<see cref="SystemServices.Current"/>), la primera vez que una pantalla los pide. Roost
/// nunca los cambia: como mucho, abre «Servicios» de Windows para que lo haga el usuario.
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
            Found = await Task.Run(() => SystemServices.Current.Find(ProcessRunner.Default));
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

    /// <summary>En Windows hay un panel («Servicios», services.msc) donde el usuario los deja en Manual; en Linux es <c>systemctl</c>.</summary>
    public static bool HasServicesPanel => OperatingSystem.IsWindows();

    /// <summary>«Servicios» de Windows (services.msc), donde el usuario los deja en Manual. En Linux no hay panel: no hace nada.</summary>
    public static void OpenServicesPanel()
    {
        if (HasServicesPanel) Shell.OpenUrl("services.msc");
    }

    /// <summary>Lo que hay que ejecutar para que un servicio no se quede el puerto (para «Copiar»).</summary>
    public static string ManualCommand(WindowsService service) =>
        OperatingSystem.IsWindows() ? WindowsServices.ManualCommand(service) : $"sudo systemctl disable --now {service.Name}";

    /// <summary>«Arranca con Windows y está en marcha»: el estado, con tildes, para la app.</summary>
    public static string StateText(WindowsService service) => (service.StartsWithWindows, service.Running) switch
    {
        (true, true) => $"{OsTexts.StartsWithSystem} y está en marcha",
        (true, false) => $"{OsTexts.StartsWithSystem} (ahora está parado)",
        (false, true) => "Está en marcha",
        _ => service.Start == ServiceStartMode.Disabled ? "Deshabilitado" : "Manual y parado",
    };

    /// <summary>El mismo estado para ir en medio de una frase («…: arranca con Windows…»).</summary>
    public static string StateInSentence(WindowsService service)
    {
        var text = StateText(service);
        return char.ToLowerInvariant(text[0]) + text[1..];
    }
}
