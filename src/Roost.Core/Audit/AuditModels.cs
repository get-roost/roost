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

    /// <summary>
    /// K4: cada mitigación (sin repetir) frente al php.ini efectivo, si se ha comprobado
    /// (<see cref="AuditMitigations"/>); vacío si no.
    /// </summary>
    public IReadOnlyList<MitigationState> MitigationStates { get; init; } = Array.Empty<MitigationState>();

    /// <summary>Lo que lee PHP, en orden (el php.ini y los de conf.d), según <c>php -i</c>; vacío si no se ha comprobado.</summary>
    public IReadOnlyList<string> IniFiles { get; init; } = Array.Empty<string>();

    /// <summary>
    /// true si un rojo ha bajado a ámbar porque todos sus avisos están mitigados en el php.ini que
    /// lee el pool de Roost (ROOST.md §7.2: «CVE mitigado por configuración» es ámbar).
    /// </summary>
    public bool Mitigated { get; init; }

    /// <summary>
    /// El hallazgo tal como lo dio el evaluador, antes de mirar las mitigaciones; null si no se han
    /// mirado. Volver a comprobar parte siempre de él (lo de antes de aplicar o deshacer no se arrastra).
    /// </summary>
    public AuditFinding? BeforeMitigations { get; init; }

    /// <summary>El estado de una de <see cref="Mitigations"/> (sin comprobar si no se ha mirado).</summary>
    public MitigationState StateOf(Mitigation mitigation) =>
        MitigationStates.FirstOrDefault(s => s.Mitigation.SameAs(mitigation)) ?? new MitigationState(mitigation, MitigationStatus.Unchecked);

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
    /// certeza. Lo incierto (ambar) no bloquea: un falso rojo quema mas que un ambar. Lo mitigado
    /// por configuracion, tampoco (es ambar).
    /// </summary>
    public bool IsBlocking => !Uncertain && !Mitigated
        && Verdict is AuditVerdict.Vulnerable or AuditVerdict.Eol;
}

/// <summary>Si una mitigación está puesta en el php.ini efectivo (K4).</summary>
public enum MitigationStatus
{
    /// <summary>No se ha mirado (no es un PHP, o no se ha podido ejecutar).</summary>
    Unchecked,

    /// <summary>PHP aplica un valor que la cumple.</summary>
    Mitigated,

    /// <summary>PHP aplica un valor que no la cumple.</summary>
    Unmitigated,

    /// <summary>Se ha mirado, pero no se puede saber (la directiva no existe, o la terminal la fija siempre).</summary>
    Unknown,
}

/// <summary>
/// Una mitigación frente al php.ini efectivo: lo que dice PHP (<see cref="Effective"/>) y si la cumple.
/// </summary>
/// <param name="Effective">El valor que aplica PHP, como lo enseña <c>php -i</c> («On», «512M», «» si no tiene).</param>
/// <param name="Detail">Por qué no se sabe, o quién la pisa (texto para el usuario, sin tildes).</param>
public sealed record MitigationState(Mitigation Mitigation, MitigationStatus Status, string? Effective = null, string? Detail = null)
{
    /// <summary>Está en el fichero de mitigaciones de Roost de su rama (<c>conf.d/&lt;rama&gt;/50-roost-mitigaciones.ini</c>).</summary>
    public bool AppliedByRoost { get; init; }

    /// <summary>Roost puede escribirla en ese fichero: es un PHP de Roost, no está cumplida y el valor se puede escribir.</summary>
    public bool CanApply { get; init; }

    /// <summary>El fichero que PHP lee después del de Roost y la pisa (aplicada, pero sin cumplir); null si no hay.</summary>
    public string? OverriddenBy { get; init; }

    /// <summary>«mitigated», «unmitigated», «unknown» o «unchecked», para el JSON.</summary>
    public string StatusKey => Status switch
    {
        MitigationStatus.Mitigated => "mitigated",
        MitigationStatus.Unmitigated => "unmitigated",
        MitigationStatus.Unknown => "unknown",
        _ => "unchecked",
    };

    /// <summary>«mitigada», «sin mitigar», «sin saber» o «sin comprobar», para la CLI (sin tildes).</summary>
    public string StatusLabel => Status switch
    {
        MitigationStatus.Mitigated => "mitigada",
        MitigationStatus.Unmitigated => "sin mitigar",
        MitigationStatus.Unknown => "sin saber",
        _ => "sin comprobar",
    };
}
