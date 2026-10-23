using Roost.Core.Audit;
using Roost.Core.Models;

namespace Roost.Cli.Commands;

/// <summary>
/// Pasa una version suelta (de un catalogo o de algo instalado por Roost) por el mismo
/// evaluador que <c>roost audit</c>, para que <c>php</c> y <c>db</c> digan al listar o instalar
/// si esta al dia o ya es EOL (instalar una 5.7 para un proyecto viejo es legitimo, pero que se sepa).
/// </summary>
internal static class CatalogAudit
{
    // Los mismos avisos que roost audit (el feed de la cache o el empaquetado), sin ir a la red.
    private static readonly Lazy<AuditEvaluator> Evaluator = new(() =>
    {
        var paths = Core.Platform.RoostPaths.Default();
        return new AuditEvaluator(CliExtensions.Current.WithAdvisories(CliFeed.Create(paths).LoadBest().Dataset, paths));
    });

    public static AuditFinding Evaluate(ComponentVersion version, string path, string source) =>
        Evaluate(ComponentKind.Php, version, path, source);

    public static AuditFinding Evaluate(ComponentKind kind, ComponentVersion version, string path, string source) =>
        Evaluator.Value.Evaluate(new DetectedComponent
        {
            Kind = kind,
            Version = version,
            ExecutablePath = path,
            Source = source,
        });

    public static string Dot(AuditColor color) => color switch
    {
        AuditColor.Green => "[green]●[/]",
        AuditColor.Amber => "[yellow]●[/]",
        _ => "[red]●[/]",
    };
}
