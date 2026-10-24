using System.Globalization;
using Avalonia;
using CommunityToolkit.Mvvm.Input;
using Roost.Core.Updates;
using Roost.Desktop.Services;

namespace Roost.Desktop.ViewModels;

/// <summary>Una línea de las novedades, lista para pintarla: título, viñeta (con su sangría), párrafo o código.</summary>
public sealed record NotesLine(string Text, NotesBlockKind Kind, int Level)
{
    public bool IsHeading => Kind == NotesBlockKind.Heading;
    public bool IsBullet => Kind == NotesBlockKind.Bullet;
    public bool IsCode => Kind == NotesBlockKind.Code;
    public bool IsParagraph => Kind == NotesBlockKind.Paragraph;
    public Thickness Margin => Kind switch
    {
        NotesBlockKind.Heading => new Thickness(0, Level <= 1 ? 6 : 8, 0, 0),
        NotesBlockKind.Bullet => new Thickness(14 * Level, 0, 0, 0),
        _ => new Thickness(0),
    };
}

/// <summary>
/// El modal «Roost X disponible»: la versión que tienes y la nueva, las novedades (el cuerpo de la
/// release, con formato), el fichero que se bajaría con su tamaño, qué hará «Actualizar ahora»
/// según el canal, y los botones. Devuelve true si se pulsa el principal.
/// </summary>
public sealed partial class UpdateDialogViewModel : DialogViewModel
{
    private static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-ES");

    private readonly ReleaseInfo _latest;

    public UpdateDialogViewModel(UpdateState updates)
    {
        _latest = updates.Latest ?? throw new InvalidOperationException("No hay ninguna versión nueva que enseñar.");
        Installed = updates.Installed;
        Channel = updates.Channel;
        Asset = UpdateInstaller.AssetFor(_latest, Channel, OperatingSystem.IsWindows());
        var blocks = ReleaseNotes.Parse(_latest.Notes).ToList();
        // El modal ya pone «Novedades» encima: el «## Novedades» con el que empiezan las notas sobra.
        if (blocks.Count > 0 && blocks[0].Kind == NotesBlockKind.Heading && blocks[0].Text.StartsWith("Novedades", StringComparison.OrdinalIgnoreCase))
            blocks.RemoveAt(0);
        Notes = blocks.Select(b => new NotesLine(b.Text, b.Kind, b.Level)).ToList();
    }

    public string Installed { get; }

    public InstallChannel Channel { get; }

    /// <summary>El fichero de la release para este canal (el instalador, el zip), si lo trae.</summary>
    public ReleaseAsset? Asset { get; }

    public string Version => _latest.Version.ToString();

    public string Title => $"Roost {Version} disponible";

    public string Subtitle => _latest.PublishedAt is { } at
        ? $"Publicada el {at.ToLocalTime().ToString("d-MMM-yyyy", Es)} · tienes la {Installed}"
        : $"Tienes la {Installed}";

    /// <summary>«61,2 MB»: lo que se descarga en este canal; null si no se baja nada o la release no dice el tamaño.</summary>
    public string? SizeText => Asset is { Size: > 0 } a && Channel is InstallChannel.Installer or InstallChannel.Manual
        ? string.Format(Es, "{0:0.0} MB", a.Size / 1048576.0)
        : null;

    public bool HasSize => SizeText is not null;

    public IReadOnlyList<NotesLine> Notes { get; }

    public bool HasNotes => Notes.Count > 0;

    /// <summary>«roost-0.2.0-win-x64-setup.exe · se verifica con el SHA-256 de la release».</summary>
    public string? DownloadText => Asset is { } a && Channel is InstallChannel.Installer or InstallChannel.Manual
        ? $"{a.Name} · se verifica con el SHA-256 de la release antes de ejecutar nada"
        : null;

    public bool HasDownloadText => DownloadText is not null;

    /// <summary>Qué hará el botón principal en este canal.</summary>
    public string Explanation => Channel switch
    {
        InstallChannel.Installer when Asset is not null =>
            "Roost descarga el instalador, comprueba su SHA-256 con el de la release y lo ejecuta en silencio. "
            + "Se cerrará un momento, Windows pedirá permiso una vez y, al terminar, Roost vuelve solo (con el stack en marcha si lo estaba). "
            + "Tus sitios, tus PHP, tus bases de datos y tus datos no se tocan.",
        InstallChannel.Installer =>
            "Esta versión no trae instalador para Windows. Ábrela en GitHub para ver qué ha pasado.",
        InstallChannel.Scoop =>
            "La instalaste con Scoop. Roost parará el stack, se cerrará y abrirá una terminal que ejecuta «scoop update roost» y vuelve a abrir Roost cuando termine.",
        InstallChannel.Winget =>
            "La instalaste con winget. Roost parará el stack, se cerrará y abrirá una terminal que ejecuta «winget upgrade Roost.Roost» y vuelve a abrir Roost cuando termine.",
        InstallChannel.Manual when Asset is not null =>
            "La descomprimiste de un zip. Roost descarga el zip nuevo y comprueba su SHA-256; después te dice dónde está para que sustituyas con él la carpeta de Roost, "
            + "con Roost cerrado y el stack parado. La actualización automática de este canal llegará más adelante.",
        InstallChannel.Manual =>
            "Esta versión no trae el zip de Windows. Ábrela en GitHub para ver qué ha pasado.",
        _ => "La compilaste desde el código: para actualizar, para el stack, actualiza el repositorio y recompila (git pull && dotnet build Roost.slnx).",
    };

    /// <summary>El botón principal; null si en este canal no hay nada que la app pueda hacer.</summary>
    public string? PrimaryLabel => Channel switch
    {
        InstallChannel.Installer when Asset is not null => "Actualizar ahora",
        InstallChannel.Scoop or InstallChannel.Winget => "Actualizar ahora",
        InstallChannel.Manual when Asset is not null => "Descargar y verificar",
        _ => null,
    };

    public bool HasPrimary => PrimaryLabel is not null;

    public Uri Page => _latest.Page;

    [RelayCommand]
    private void Primary() => Close(true);

    [RelayCommand]
    private void Later() => Close(false);

    [RelayCommand]
    private void OpenPage()
    {
        try { Shell.OpenUrl(Page.ToString()); }
        catch (Roost.Core.RoostException) { /* sin navegador: el enlace sigue en Ajustes */ }
    }
}
