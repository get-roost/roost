using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Roost.Core.Sites;

namespace Roost.Desktop.ViewModels;

/// <summary>
/// «PHP 7.4.33 está en uso»: antes de quitar una versión que usan sitios, decidir a qué PHP
/// pasan (el global u otra instalada) o no quitarla todavía. Devuelve true si hay que quitarla.
/// </summary>
public sealed partial class RemovePhpDialogViewModel : DialogViewModel
{
    public RemovePhpDialogViewModel(PhpCard card, PhpCard global, IReadOnlyList<PhpCard> others, IReadOnlyList<Site> sites)
    {
        Card = card;
        Global = global;
        Others = others.Where(o => o != global).ToList();
        Sites = sites;
        Other = Others.FirstOrDefault();
    }

    public PhpCard Card { get; }

    public PhpCard Global { get; }

    public IReadOnlyList<PhpCard> Others { get; }

    public IReadOnlyList<Site> Sites { get; }

    public string Title => $"PHP {Card.Version} está en uso";

    public string Subtitle => $"Lo usa{(Sites.Count == 1 ? " 1 sitio" : $"n {Sites.Count} sitios")}. Antes de quitarlo hay que decidir qué PHP usará{(Sites.Count == 1 ? "" : "n")}.";

    public string Question => Sites.Count == 1 ? $"¿Qué hacemos con {Sites[0].Host}?" : "¿Qué hacemos con estos sitios?";

    public string GlobalLabel => $"Pasarlo al PHP global {Global.Version}";

    public string GlobalHint => Card.Php.Version is { Major: < 8 } && Global.Php.Version is { Major: >= 8 }
        ? "Revisa que el proyecto funciona con PHP 8: entre la 7 y la 8 hay cambios que rompen."
        : "Lo usará también cuando cambies el PHP global.";

    public string RemoveNote => $"Quitar una versión borra solo su carpeta ({Card.Php.Directory}). Tus ajustes de conf.d se quedan.";

    public bool HasOthers => Others.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConfirmLabel), nameof(Removes))]
    public partial bool ToGlobal { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConfirmLabel), nameof(Removes))]
    public partial bool ToOther { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConfirmLabel), nameof(Removes))]
    public partial bool Keep { get; set; }

    [ObservableProperty]
    public partial PhpCard? Other { get; set; }

    public bool Removes => !Keep;

    public string ConfirmLabel => Keep ? "No quitar" : $"Pasar y quitar {Card.Version}";

    /// <summary>Lo que se guarda en el sitio: null (el global) o la rama de la otra versión.</summary>
    public string? TargetSpec => ToOther && Other is not null ? Other.Branch : null;

    [RelayCommand]
    private void Confirm() => Close(!Keep);
}
