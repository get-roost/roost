using Roost.Core.Scanning;

namespace Roost.Core.Platform;

/// <summary>
/// Los servicios del sistema que se pelean con Roost por los puertos, en el sistema que sea: en Windows,
/// los servicios de Windows (<see cref="WindowsServices"/>); en Linux, las unidades de systemd
/// (<see cref="SystemdServices"/>). Lo usan <c>roost up</c> (el aviso de antes de arrancar) y el daemon
/// (quién tiene el puerto cuando no se puede arrancar). Solo leen: Roost nunca cambia, para ni arranca un
/// servicio del sistema. Los textos van sin tildes (son de la CLI y del daemon).
/// </summary>
public interface ISystemServices
{
    /// <summary>Los que hay en esta máquina (vacío si no se pueden leer o este sistema no tiene de eso).</summary>
    IReadOnlyList<WindowsService> Find(IProcessRunner runner);

    /// <summary>PURO: el servicio en marcha que tiene un puerto, por el proceso que escucha en él; null si no es ninguno.</summary>
    WindowsService? OwnerOf(PortOwner? owner, IEnumerable<WindowsService> services);

    /// <summary>PURO: «el servicio de Windows "MySQL80" arranca con Windows y esta en marcha» (sin el punto final).</summary>
    string Describe(WindowsService service);

    /// <summary>PURO: qué hacer para que no se quede el puerto, en imperativo y sin punto final («Ponlo en Manual en services.msc (…) y paralo (…)»).</summary>
    string HowToFree(WindowsService service);

    /// <summary>PURO: «Roost no toca servicios de Windows.»</summary>
    string Disclaimer { get; }

    /// <summary>PURO: la frase entera para el error de un puerto ocupado por este servicio (el daemon la añade al mensaje).</summary>
    string PortHint(PortOwner owner, WindowsService service);
}

/// <summary>La implementación de este sistema.</summary>
public static class SystemServices
{
    public static ISystemServices Current { get; } =
        OperatingSystem.IsWindows() ? new WindowsSystemServices()
        : OperatingSystem.IsLinux() ? new SystemdServices()
        : new NoSystemServices();
}

/// <summary>Windows: la fachada sobre <see cref="WindowsServices"/>, con sus mismos textos.</summary>
internal sealed class WindowsSystemServices : ISystemServices
{
    public IReadOnlyList<WindowsService> Find(IProcessRunner runner) => WindowsServices.Find(runner);

    public WindowsService? OwnerOf(PortOwner? owner, IEnumerable<WindowsService> services) => WindowsServices.OwnerOf(owner, services);

    public string Describe(WindowsService service) => $"el servicio de Windows {WindowsServices.Label(service)} {WindowsServices.StateText(service)}";

    public string HowToFree(WindowsService service) =>
        $"Ponlo en Manual en services.msc (o, en una consola de administrador: {WindowsServices.ManualCommand(service)}) y paralo ({WindowsServices.StopCommand(service)})";

    public string Disclaimer => "Roost no toca servicios de Windows.";

    public string PortHint(PortOwner owner, WindowsService service) =>
        $"El puerto {owner.Port} lo tiene el servicio de Windows {WindowsServices.Label(service)}, que {WindowsServices.StateText(service)}. "
        + $"Para que no se lo quede, ponlo en Manual en services.msc (o, en una consola de administrador: {WindowsServices.ManualCommand(service)}) y paralo.";
}

/// <summary>macOS y lo demás: de momento, sin servicios que mirar.</summary>
internal sealed class NoSystemServices : ISystemServices
{
    public IReadOnlyList<WindowsService> Find(IProcessRunner runner) => [];

    public WindowsService? OwnerOf(PortOwner? owner, IEnumerable<WindowsService> services) => null;

    public string Describe(WindowsService service) => $"el servicio {service.Name}";

    public string HowToFree(WindowsService service) => "Paralo";

    public string Disclaimer => "Roost no toca los servicios del sistema.";

    public string PortHint(PortOwner owner, WindowsService service) => $"El puerto {owner.Port} lo tiene el servicio {service.Name}.";
}
