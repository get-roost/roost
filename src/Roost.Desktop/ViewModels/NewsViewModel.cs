using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Roost.Core.Updates;
using Roost.Desktop.Services;

namespace Roost.Desktop.ViewModels;

/// <summary>Un punto de las novedades: «**Sitios:** lo que cambia» → título en negrita y el resto.</summary>
public sealed record NewsItem(string Title, string Body)
{
    public bool HasTitle => Title.Length > 0;
}

/// <summary>Una versión en Novedades, con sus puntos.</summary>
public sealed record NewsRelease(string Version, string? Date, bool IsCurrent, IReadOnlyList<NewsItem> Items)
{
    public bool HasDate => Date is not null;
    public bool IsOlder => !IsCurrent;
}

/// <summary>
/// Novedades: lo que cambia en cada versión de Roost, de <c>CHANGELOG.md</c> (empaquetado: se ve
/// sin red). Marca la versión que tienes y, si hay una nueva, lo dice con su acceso a actualizar.
/// </summary>
public sealed partial class NewsViewModel : PageViewModel
{
    public NewsViewModel(MainViewModel main) : base(main)
    {
        Releases = Changelog.Embedded()
            .Select(e => new NewsRelease(e.Version, e.Date, SameVersion(e.Version, RoostVersion.Current), e.Items.Select(Item).ToList()))
            .ToList();
        Subtitle = $"Lo que cambia en cada versión. Tienes Roost {RoostVersion.Current}.";
        main.Updates.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(UpdateState.HasUpdate) or nameof(UpdateState.LatestVersion)) NotifyUpdate();
        };
    }

    public override string Title => "Novedades";

    public IReadOnlyList<NewsRelease> Releases { get; }

    public bool HasReleases => Releases.Count > 0;

    public bool HasUpdate => Main.Updates.HasUpdate;

    public string UpdateText => $"Roost {Main.Updates.LatestVersion} ya está disponible: sus novedades las verás aquí al actualizar.";

    private void NotifyUpdate()
    {
        OnPropertyChanged(nameof(HasUpdate));
        OnPropertyChanged(nameof(UpdateText));
    }

    [RelayCommand]
    private Task OpenUpdatesAsync() => Main.Updates.OpenAsync(Main);

    [RelayCommand]
    private Task OpenReleasesAsync() => Main.RunAsync("No se pudo abrir el navegador", () =>
    {
        Shell.OpenUrl("https://github.com/get-roost/roost/releases");
        return Task.CompletedTask;
    });

    private static bool SameVersion(string a, string b) =>
        SemanticVersion.TryParse(a, out var x) && SemanticVersion.TryParse(b, out var y) && x == y;

    // «**Sitios:** cambiar la raíz web (`docroot`)» → («Sitios», «cambiar la raíz web (docroot)»).
    private static NewsItem Item(string markdown)
    {
        var match = Regex.Match(markdown, @"^\*\*(.+?)\*\*\s*(.*)$");
        var (title, body) = match.Success ? (match.Groups[1].Value.Trim() + " ", match.Groups[2].Value) : ("", markdown);
        return new NewsItem(Plain(title), Plain(body));
    }

    private static string Plain(string markdown) => markdown.Replace("**", "").Replace("`", "");
}
