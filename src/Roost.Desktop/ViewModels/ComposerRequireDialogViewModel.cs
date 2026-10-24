using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Roost.Core.Composer;

namespace Roost.Desktop.ViewModels;

/// <summary>Lo que devuelve «Añadir paquete…»: el paquete ya normalizado y si es solo para desarrollo.</summary>
public sealed record ComposerRequireRequest(string Package, bool Dev);

/// <summary>
/// «Añadir paquete…» del bloque Composer: <c>vendor/paquete[:versión]</c>, validado como lo valida
/// Composer (<see cref="ComposerTool.NormalizePackage"/>), y la casilla de <c>--dev</c>. Devuelve
/// <see cref="ComposerRequireRequest"/> o null.
/// </summary>
public sealed partial class ComposerRequireDialogViewModel(string host, string phpLabel) : DialogViewModel
{
    public string Subtitle => $"En {host} · con PHP {phpLabel}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Hint), nameof(HintIsError), nameof(CanConfirm))]
    public partial string Package { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Hint))]
    public partial bool Dev { get; set; }

    public bool CanConfirm => ComposerTool.IsValidPackage(Package);

    public bool HintIsError => Package.Trim().Length > 0 && !CanConfirm;

    /// <summary>Lo que se va a ejecutar o, si no vale, por qué.</summary>
    public string Hint =>
        Package.Trim().Length == 0 ? "vendor/paquete y, si quieres, su versión: monolog/monolog o monolog/monolog:^3."
        : !CanConfirm ? "Tiene que ser vendor/paquete (minúsculas, números, . _ -) y, si quieres, :versión (^3.0, ~2.1, 3.*, dev-main)."
        : ComposerTool.Display(ComposerTool.Arguments(ComposerAction.Require, Package, Dev));

    [RelayCommand]
    private void Confirm()
    {
        if (CanConfirm) Close(new ComposerRequireRequest(ComposerTool.NormalizePackage(Package), Dev));
    }
}
