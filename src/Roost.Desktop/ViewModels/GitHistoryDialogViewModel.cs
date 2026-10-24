using CommunityToolkit.Mvvm.Input;
using Roost.Core.Git;
using Roost.Desktop.Controls;

namespace Roost.Desktop.ViewModels;

/// <summary>Una línea del historial: cuándo, qué, de qué commit a cuál, cómo acabó y cuánto tardó.</summary>
public sealed record GitHistoryRow(string When, string What, string Range, bool Ok, string Took, string? Error)
{
    public string Mark => Ok ? "✓" : "✗";

    public Tone Tone => Ok ? Tone.Ok : Tone.Bad;

    public bool HasError => Error is not null;

    public bool HasRange => Range.Length > 0;
}

/// <summary>
/// «Historial de tienda.test» (maqueta G): lo que Roost ha hecho con el repositorio (clonar,
/// actualizar, cambiar de rama, adoptar), de lo más nuevo a lo más viejo, con «Abrir el registro
/// completo» (Registros › git-&lt;sitio&gt;.log).
/// </summary>
public sealed partial class GitHistoryDialogViewModel : DialogViewModel
{
    private readonly Func<Task> _openLog;

    public GitHistoryDialogViewModel(string host, IReadOnlyList<GitHistoryEntry> entries, Func<Task> openLog)
    {
        Title = $"Historial de {host}";
        _openLog = openLog;
        Rows = entries.Select(Row).ToList();
    }

    public override bool Wide => true;

    public string Title { get; }

    public IReadOnlyList<GitHistoryRow> Rows { get; }

    public bool IsEmpty => Rows.Count == 0;

    public bool HasRows => Rows.Count > 0;

    /// <summary>PURO: una entrada del historial como se enseña.</summary>
    public static GitHistoryRow Row(GitHistoryEntry entry)
    {
        var what = entry.Kind switch
        {
            GitHistory.Clone => "Clonar",
            GitHistory.Pull => "Actualizar",
            GitHistory.Checkout => entry.Branch is { } b ? $"Cambiar rama → {b}" : "Cambiar rama",
            GitHistory.Adopt => "Adoptar",
            _ => entry.Kind,
        };
        var range = entry.From is not null && entry.To is not null && entry.From != entry.To ? $"{entry.From} → {entry.To}"
            : entry.To ?? "";
        if (entry.Branch is { } branch && entry.Kind != GitHistory.Checkout)
            range = range.Length == 0 ? $"({branch})" : $"{range} ({branch})";
        if (entry.Kind == GitHistory.Pull && entry.Ok && entry.From is not null && entry.From == entry.To) range = $"ya al día ({entry.Branch})";
        var es = System.Globalization.CultureInfo.GetCultureInfo("es-ES");
        var took = entry.Seconds switch
        {
            < 0.05 => "",
            < 10 => entry.Seconds.ToString("0.0", es) + " s",
            < 60 => entry.Seconds.ToString("0", es) + " s",
            _ => $"{(int)(entry.Seconds / 60)} min {(int)entry.Seconds % 60} s",
        };
        return new GitHistoryRow(GitTexts.When(entry.When), what, range, entry.Ok, took, entry.Ok ? null : entry.Error ?? "Falló sin decir por qué.");
    }

    [RelayCommand]
    private async Task OpenLogAsync()
    {
        Close();
        await _openLog();
    }

    [RelayCommand]
    private void Finish() => Close();
}
