using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Roost.Core.Sites;

namespace Roost.Desktop.ViewModels;

/// <summary>Un alias de la lista del diálogo: el nombre y si es un subdominio del sitio u otro nombre.</summary>
public sealed record AliasRow(string Name, bool IsSubdomain)
{
    public string Kind => IsSubdomain ? "subdominio" : "alias";
}

/// <summary>
/// Sitios avanzados (C7): «Alias…» del panel de un sitio: sus otros nombres <c>.test</c>
/// (<c>tienda-alias.test</c>) y sus subdominios con nombre (<c>api.tienda.test</c>), validados como
/// en Core (<see cref="SiteAliases.WhyNot"/>). Explica qué hace falta para que resuelvan: con el hosts,
/// una línea por nombre (un solo permiso al guardar); con el DNS local, nada. Con WordPress, avisa
/// de que el multisite con subdominios necesita el DNS local o una línea por subdominio. Devuelve
/// la lista final, o null si se cancela o no cambia.
/// </summary>
public sealed partial class SiteAliasesDialogViewModel : DialogViewModel
{
    private readonly Site _site;
    private readonly IReadOnlyList<Site> _all;
    private readonly IReadOnlyList<string> _initial;

    public SiteAliasesDialogViewModel(Site site, IReadOnlyList<Site> all, bool byDns, bool isWordPress)
    {
        _site = site;
        _all = all;
        ByDns = byDns;
        IsWordPress = isWordPress;
        _initial = site.Aliases ?? [];
        foreach (var alias in _initial) Aliases.Add(Row(alias));
        Aliases.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasAliases));
            OnPropertyChanged(nameof(Changed));
            OnPropertyChanged(nameof(ResolveText));
        };
    }

    public string Title => $"Alias de {_site.Host}";

    public string Host => _site.Host;

    public bool ByDns { get; }

    public bool IsWordPress { get; }

    public ObservableCollection<AliasRow> Aliases { get; } = [];

    public bool HasAliases => Aliases.Count > 0;

    public bool Changed => !Aliases.Select(a => a.Name).SequenceEqual(_initial, StringComparer.Ordinal);

    public string WildcardText => $"{_site.Host} ya responde también por *.{_site.Host}: el servidor web sirve cualquier subdominio y el certificado lo cubre.";

    public string ResolveText => ByDns
        ? "Con el DNS local, todos los .test resuelven solos: no hace falta tocar el hosts."
        : $"Con el hosts, cada nombre necesita su línea: al guardar, Roost añade {(Aliases.Count == 1 ? "la de este" : "las de estos")} de una vez ({Services.OsTexts.AsksPermission} una sola vez).";

    public string PlaceholderText => $"{_site.Name}-alias.test o api.{_site.Host}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Hint), nameof(HintIsError), nameof(CanAdd))]
    public partial string NewAlias { get; set; } = "";

    private string Normalized => SiteAliases.Normalize(NewAlias);

    private string? Problem
    {
        get
        {
            if (NewAlias.Trim().Length == 0) return null;
            var alias = Normalized;
            var (problem, other, name) = SiteAliases.Check(_site with { Aliases = Aliases.Select(a => a.Name).ToList() }, alias, _all);
            return problem switch
            {
                AliasProblem.None => null,
                AliasProblem.Invalid => "Tiene que acabar en .test y llevar solo minúsculas, números, guiones y puntos (hasta cuatro partes delante de .test).",
                AliasProblem.IsSiteName => "Es el nombre del propio sitio.",
                AliasProblem.AlreadyThere => "Ya está en la lista.",
                AliasProblem.TooMany => $"Ya tiene {SiteAliases.MaxPerSite} alias, que es el máximo.",
                AliasProblem.OtherSite => $"{alias} ya es de {other!.Host}.",
                AliasProblem.UnderOtherSite => $"Es un subdominio de {name}, que es de {other!.Host}: lo serviría ese sitio.",
                _ => $"Debajo de {alias} queda {name}, que es de {other!.Host}.",
            };
        }
    }

    public bool CanAdd => NewAlias.Trim().Length > 0 && Problem is null;

    public bool HintIsError => Problem is not null;

    public string Hint => Problem
        ?? (NewAlias.Trim().Length == 0
            ? "Otro nombre (tienda-alias o tienda-alias.test) o un subdominio (api." + _site.Host + "). Sin .test, se añade."
            : SiteAliases.IsSubdomainOf(Normalized, _site.Host)
                ? $"{Normalized}: un subdominio. Ya lo sirve *.{_site.Host}; le falta su línea en el hosts."
                : $"{Normalized}: otro nombre para el mismo sitio, también con *.{Normalized}.");

    [RelayCommand]
    private void Add()
    {
        if (!CanAdd) return;
        var name = Normalized;
        Aliases.Add(Row(name));
        NewAlias = "";
    }

    [RelayCommand]
    private void Remove(AliasRow row) => Aliases.Remove(row);

    [RelayCommand]
    private void Confirm() => Close(Changed ? Aliases.Select(a => a.Name).ToList() : null);

    private AliasRow Row(string alias) => new(alias, SiteAliases.IsSubdomainOf(alias, _site.Host));
}
