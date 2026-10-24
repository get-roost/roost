using Roost.Core.Audit;
using Roost.Core.Config;
using Roost.Core.Platform;
using Roost.Core.Scanning;
using Roost.Core.Services;
using Spectre.Console.Cli;

namespace Roost.Extensibility;

/// <summary>
/// La versión del contrato. Sube cuando cambia algo que rompe a las extensiones ya compiladas.
/// 2: el supervisor para y arranca grupos, las configuraciones del servidor web y el DNS llevan
/// IPv6, y <c>StatusResponse</c> trae los puertos del stack.
/// </summary>
public static class ExtensionApi
{
    public const int Current = 2;
}

/// <summary>
/// Una extensión de Roost. Vive en <c>bin/extensions/&lt;Ensamblado&gt;/&lt;Ensamblado&gt;.dll</c> con sus
/// dependencias al lado, y Roost crea una instancia de cada tipo público que implemente esto (con
/// un constructor sin parámetros). Lo que aporta lo dicen las otras interfaces de este fichero,
/// que puede implementar a la vez. Si necesita licencia, la comprueba ella misma.
/// </summary>
public interface IRoostExtension
{
    /// <summary>Identificador estable: minúsculas, números y guiones («roost-pro»).</summary>
    string Id { get; }

    /// <summary>Nombre para enseñar («Roost Pro»).</summary>
    string Name { get; }

    string Version { get; }

    /// <summary>La versión del contrato con la que se compiló; Roost no carga las que piden una más nueva.</summary>
    int ApiVersion => ExtensionApi.Current;
}

/// <summary>Lo que una extensión ve al preparar el stack.</summary>
public sealed record StackContext(RoostPaths Paths, RoostConfig Config);

/// <summary>
/// Servicios que arrancan con el stack (un Redis, un Mailpit): van después de la base de datos y
/// antes de PHP, sus puertos se comprueban antes de arrancar, se espera a que respondan como el
/// resto y salen en el estado, en el reinicio por servicio y en los registros. Se pide en cada
/// <c>up</c>. Los nombres no pueden repetir los de Roost.
/// </summary>
public interface IStackExtension : IRoostExtension
{
    IEnumerable<ServiceDefinition> Services(StackContext context);
}

/// <summary>Más auditoría: avisos que se mezclan con los de Roost y más cosas que inventariar.</summary>
public interface IAuditExtension : IRoostExtension
{
    /// <summary>
    /// Un dataset con el mismo formato que <c>advisories.json</c> (el feed ampliado). Sus avisos se
    /// suman a los de Roost y uno con el mismo id sustituye al de Roost. null = ninguno.
    /// </summary>
    AuditDataset? Advisories(RoostPaths paths);

    /// <summary>Scanners que se añaden a los de Roost en <c>scan</c> y <c>audit</c>.</summary>
    IEnumerable<IComponentScanner> Scanners(RoostPaths paths) => [];
}

/// <summary>Comandos o ramas nuevas para la CLI (<c>roost &lt;lo-que-sea&gt;</c>), con Spectre.Console.Cli.</summary>
public interface ICliExtension : IRoostExtension
{
    void AddCommands(IConfigurator config);
}
