using System.Globalization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Roost.Core.Stack;

namespace Roost.Desktop.ViewModels;

/// <summary>Lo que devuelve «Importar…»: a qué base de datos, si hay que crearla y si se adapta al vuelo.</summary>
public sealed record DbImportChoice(string Target, bool Create, bool Compat);

/// <summary>
/// BD-2, «Importar…»: el fichero elegido (qué es y de dónde viene), si va a una base de datos nueva
/// o a una que ya existe y, si trae cosas que este servidor no entiende (un volcado de MySQL 8 en
/// una MariaDB vieja, o de MariaDB en MySQL), la casilla para adaptarlo al vuelo con su aviso. Si
/// la elegida ya tiene tablas, lo pregunta después la pantalla, con cuántas son.
/// </summary>
public sealed partial class DbImportDialogViewModel : DialogViewModel
{
    private readonly IReadOnlyList<string> _existing;

    public DbImportDialogViewModel(string file, long bytes, DumpInfo info, string serverLabel, IReadOnlyList<string> existing,
        IReadOnlyList<CompatIssue> issues, string? preselected = null)
    {
        _existing = existing;
        File = file;
        FileName = Path.GetFileName(file);
        var es = CultureInfo.GetCultureInfo("es-ES");
        var parts = new List<string> { DbTransferDialogViewModel.Size(bytes, es) };
        if (FileName.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)) parts.Add("comprimido (.gz)");
        else if (FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) parts.Add("el .sql de un .zip");
        if (info.Origin is { } origin) parts.Add(origin);
        if (info.Databases.Count > 0) parts.Add($"trae CREATE DATABASE/USE de {info.Databases[0]}, que no se usan");
        FileDetail = string.Join(" · ", parts);
        Subtitle = $"En {serverLabel}, por el cliente y en streaming: vale para ficheros de varios GB.";
        Existing = existing;
        Issues = issues.Select(i => $"{i.What} ({i.Fix}).").ToList();
        Compat = issues.Count > 0;

        var suggested = preselected ?? SuggestName(info.SourceDatabase ?? FileName);
        if (existing.Contains(suggested, StringComparer.OrdinalIgnoreCase))
        {
            ToExisting = true;
            SelectedExisting = existing.First(e => e.Equals(suggested, StringComparison.OrdinalIgnoreCase));
            NewName = suggested + "_2";
        }
        else
        {
            ToNew = true;
            NewName = suggested;
            SelectedExisting = existing.FirstOrDefault();
        }
    }

    public string File { get; }

    public string FileName { get; }

    public string FileDetail { get; }

    public string Subtitle { get; }

    public IReadOnlyList<string> Existing { get; }

    public bool HasExisting => Existing.Count > 0;

    public IReadOnlyList<string> Issues { get; }

    public bool HasIssues => Issues.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm), nameof(NameHint), nameof(HasNameHint), nameof(TargetHint))]
    public partial bool ToNew { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm), nameof(NameHint), nameof(HasNameHint), nameof(TargetHint))]
    public partial bool ToExisting { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm), nameof(NameHint), nameof(HasNameHint), nameof(TargetHint))]
    public partial string NewName { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm), nameof(TargetHint))]
    public partial string? SelectedExisting { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CompatHint))]
    public partial bool Compat { get; set; }

    public string NameHint
    {
        get
        {
            if (!ToNew) return "";
            var name = NewName.Trim();
            if (name.Length == 0) return "";
            if (!DbAdmin.IsValidName(name)) return "Solo letras, números y _ (hasta 64).";
            if (DbAdmin.SystemSchemas.Contains(name)) return $"{name} es del propio servidor: elige otro nombre.";
            if (_existing.Contains(name, StringComparer.OrdinalIgnoreCase)) return $"{name} ya existe: elige «Una que ya existe» o cambia el nombre.";
            return "";
        }
    }

    public bool HasNameHint => NameHint.Length > 0;

    public string TargetHint => ToNew
        ? $"Se crea {(NewName.Trim().Length > 0 ? NewName.Trim() : "la base de datos")} (utf8mb4) y se importa en ella. Si falla o cancelas, se borra: no queda nada a medias."
        : $"Se importa encima de {SelectedExisting ?? "la elegida"}: las tablas que también vengan en el fichero se sustituyen y el resto se queda.";

    public string CompatHint => Compat
        ? "Se cambian al vuelo, solo en la estructura (nunca en los datos): las colaciones que no conoce por utf8mb4_unicode_ci (o _bin) y lo que no admite del sql_mode. El fichero no se toca."
        : "Sin adaptarlo, lo más probable es que falle en la primera tabla que lo use.";

    public bool CanConfirm => ToNew
        ? NewName.Trim().Length > 0 && NameHint.Length == 0
        : ToExisting && SelectedExisting is not null;

    [RelayCommand]
    private void Confirm()
    {
        if (!CanConfirm) return;
        Close(ToNew ? new DbImportChoice(NewName.Trim(), true, Compat) : new DbImportChoice(SelectedExisting!, false, Compat));
    }

    /// <summary>
    /// PURO: el nombre que se propone: el de la base de datos de la cabecera o el del fichero, sin
    /// extensiones ni la fecha que pone Exportar (<c>tienda-20261024-1830.sql.gz</c> → <c>tienda</c>).
    /// </summary>
    public static string SuggestName(string source)
    {
        var name = source;
        foreach (var ext in new[] { ".gz", ".zip", ".sql" })
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) name = name[..^ext.Length];
        name = DateSuffix().Replace(name, "");
        return DbAdmin.NameFor(name);
    }

    [GeneratedRegex(@"[-_]\d{8}([-_]\d{4,6})?$")]
    private static partial Regex DateSuffix();
}
