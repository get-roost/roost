using Roost.Core.Audit;
using Roost.Core.Models;
using Roost.Core.Platform;
using Roost.Core.Scanning;
using Roost.Core.Services;
using Roost.Extensibility;
using Spectre.Console.Cli;

namespace Roost.Extensibility.Sample;

/// <summary>La muestra: un poco de cada cosa que puede aportar una extensión.</summary>
public sealed class SampleExtension : IStackExtension, IAuditExtension, ICliExtension
{
    public string Id => "muestra";
    public string Name => "Extension de muestra";
    public string Version => "1.2.3";

    public IEnumerable<ServiceDefinition> Services(StackContext context) =>
    [
        new ServiceDefinition
        {
            Name = "muestra",
            Executable = Path.Combine(context.Paths.Root, "bin", "muestra", "muestra.exe"),
            Port = 6399,
        },
    ];

    public AuditDataset? Advisories(RoostPaths paths) => new()
    {
        Source = "muestra",
        Advisories =
        [
            new Advisory { Id = "MUESTRA-1", Product = "php", Title = "Un aviso que solo trae la extension", Affected = ["<99.0.0"], Cvss = 5.0 },
        ],
    };

    public IEnumerable<IComponentScanner> Scanners(RoostPaths paths) => [new SampleScanner()];

    public void AddCommands(IConfigurator config) => config.AddCommand<SampleCommand>("muestra");
}

/// <summary>Un scanner que siempre «encuentra» un nginx (sin ejecutar nada).</summary>
public sealed class SampleScanner : IComponentScanner
{
    public ComponentKind Kind => ComponentKind.Nginx;
    public bool SupportsCurrentOs => true;

    public IEnumerable<DetectedComponent> Scan() =>
    [
        new DetectedComponent
        {
            Kind = ComponentKind.Nginx,
            Version = ComponentVersion.TryParse("1.30.6", out var v) ? v : default,
            ExecutablePath = @"C:\muestra\nginx.exe",
            Source = "muestra",
        },
    ];
}

public sealed class SampleCommand : Command<EmptyCommandSettings>
{
    public override int Execute(CommandContext context, EmptyCommandSettings settings, CancellationToken cancellation) => 42;
}

/// <summary>Una extensión compilada para un contrato que este Roost aún no tiene: no se carga.</summary>
public sealed class FutureExtension : IRoostExtension
{
    public string Id => "del-futuro";
    public string Name => "Extension del futuro";
    public string Version => "9.0.0";
    public int ApiVersion => ExtensionApi.Current + 1;
}
