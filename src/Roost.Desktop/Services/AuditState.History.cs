using CommunityToolkit.Mvvm.ComponentModel;
using Roost.Core.Audit;

namespace Roost.Desktop.Services;

/// <summary>
/// K1 y K5: lo ignorado (no cuenta en rojo, ni en el contador de la barra lateral, ni avisa en la
/// bandeja; se ve aparte en Auditoría) y qué ha cambiado desde la auditoría anterior, guardado en
/// <c>state/</c> (<see cref="AuditHistory"/>) para que se sepa también entre sesiones.
/// </summary>
public sealed partial class AuditState
{
    private List<IgnoredFinding>? _ignored;

    private List<IgnoredFinding> IgnoredList => _ignored ??= _app is null ? [] : AuditHistory.LoadIgnored(_app.Paths);

    /// <summary>Lo ignorado, lo más nuevo primero.</summary>
    public IReadOnlyList<IgnoredFinding> Ignored => IgnoredList.OrderByDescending(i => i.When).ToList();

    /// <summary>Qué ha cambiado desde la auditoría anterior; null la primera vez (no hay con qué comparar).</summary>
    [ObservableProperty]
    public partial AuditChanges? Changes { get; private set; }

    public bool IsIgnored(AuditFinding finding)
    {
        var id = AuditHistory.IgnoreIdOf(finding);
        return IgnoredList.Any(i => i.Id == id);
    }

    /// <summary>Ignorar ese componente en esa versión. Se guarda y se puede deshacer.</summary>
    public void Ignore(AuditFinding finding)
    {
        if (IsIgnored(finding)) return;
        var c = finding.Component;
        IgnoredList.Add(new IgnoredFinding(AuditHistory.IgnoreIdOf(finding), c.Kind.ToString(), c.Version.ToString(), c.Source, c.ExecutablePath, DateTime.Now));
        SaveIgnored();
    }

    /// <summary>Dejar de ignorarlo: vuelve al inventario y a contar.</summary>
    public void Unignore(string id)
    {
        if (IgnoredList.RemoveAll(i => i.Id == id) == 0) return;
        SaveIgnored();
    }

    private void SaveIgnored()
    {
        if (_app is not null) AuditHistory.SaveIgnored(_app.Paths, IgnoredList);
        // Los contadores, la barra lateral, la bandeja y la pantalla se rehacen como tras un escaneo.
        OnPropertyChanged(nameof(Ignored));
        OnPropertyChanged(nameof(Findings));
        OnPropertyChanged(nameof(RedCount));
        OnPropertyChanged(nameof(AmberCount));
        OnPropertyChanged(nameof(GreenCount));
        OnPropertyChanged(nameof(Summary));
    }

    /// <summary>Tras cada escaneo: compara con el anterior guardado y guarda este.</summary>
    private void Record()
    {
        if (_app is null) return;
        var previous = AuditHistory.LoadLast(_app.Paths);
        Changes = AuditHistory.Compare(previous, Findings, IsIgnored);
        AuditHistory.SaveLast(_app.Paths, new AuditRecord(LastScan ?? DateTime.Now, Findings.Select(AuditHistory.MarkOf).ToList()));
    }

    /// <summary>Lo que cuenta: lo que no está ignorado.</summary>
    private int Count(AuditColor color) => Findings.Count(f => f.Color == color && !IsIgnored(f));
}
