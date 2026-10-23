using Roost.Core.Models;

namespace Roost.Core.Audit;

/// <summary>Veredicto de una instalacion. Es la comparacion por numero frente al dataset.</summary>
public enum AuditVerdict
{
    Patched,     // PARCHEADA
    Supported,   // SOPORTADA (rama viva, pero el dataset no trae CVE de este producto)
    Vulnerable,  // VULNERABLE
    Eol,         // EOL (fin de vida, sin parche oficial)
    Unknown,     // DESCONOCIDA (rama no reconocida)
}

/// <summary>Semaforo del panel (ROOST.md seccion 7.2).</summary>
public enum AuditColor
{
    Green,  // al dia, con soporte, sin avisos
    Amber,  // incierto: backport de distro, desconocida, o aviso mitigable
    Red,    // vulnerable o EOL con certeza
}

/// <summary>
/// Resultado de auditar UNA instalacion. Lo produce <see cref="AuditEvaluator"/>
/// de forma pura (sin escanear ni tocar disco), para poder testearlo aislado.
/// </summary>
public sealed record AuditFinding
{
    public required DetectedComponent Component { get; init; }
    public required AuditVerdict Verdict { get; init; }
    public required AuditColor Color { get; init; }

    /// <summary>
    /// true cuando la comparacion por numero NO es fiable: paquete de distro con
    /// backport (+deb, +esm, -ubuntu...) o instalado por el gestor del sistema.
    /// Es el "ambar honesto": no gritamos rojo sobre algo que no podemos verificar.
    /// </summary>
    public bool Uncertain { get; init; }

    public required string Headline { get; init; }

    public string? BranchLatest { get; init; }
    public DateOnly? Eol { get; init; }

    public IReadOnlyList<Advisory> MatchedAdvisories { get; init; } = Array.Empty<Advisory>();
    public IReadOnlyList<Mitigation> Mitigations { get; init; } = Array.Empty<Mitigation>();
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    /// <summary>Etiqueta en espanol del veredicto.</summary>
    public string VerdictLabel => Verdict switch
    {
        AuditVerdict.Patched => "PARCHEADA",
        AuditVerdict.Supported => "SOPORTADA",
        AuditVerdict.Vulnerable => "VULNERABLE",
        AuditVerdict.Eol => "EOL",
        _ => "DESCONOCIDA",
    };

    /// <summary>
    /// true si debe hacer fallar el proceso (exit code != 0): vulnerable o EOL CON
    /// certeza. Lo incierto (ambar) no bloquea: un falso rojo quema mas que un ambar.
    /// </summary>
    public bool IsBlocking => !Uncertain
        && Verdict is AuditVerdict.Vulnerable or AuditVerdict.Eol;
}
