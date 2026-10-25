using System.Runtime.CompilerServices;

namespace Roost.Desktop.Tests;

/// <summary>
/// Lo que vale para toda la batería de la app, antes de que corra ningún test: las vistas se montan sobre
/// una raíz de pega (<see cref="FakeRoost"/>) y no pueden depender de lo que tenga la máquina.
/// </summary>
internal static class TestEnvironment
{
    /// <summary>
    /// <c>ROOST_APACHE=none</c>: en Linux «Apache» es el de la distro (<c>apache2</c>, Servicios L2), y
    /// <c>ApacheInstaller.Find</c> lo daría por instalado en cualquier máquina que lo tenga (la Ubuntu de pruebas
    /// lo tiene). Con la variable, Roost lo ignora y las pantallas de Inicio, Servidor web y la bandeja ven lo
    /// mismo en todas partes. En Windows no cambia nada.
    /// </summary>
    [ModuleInitializer]
    internal static void Init() => Environment.SetEnvironmentVariable("ROOST_APACHE", "none");
}
