using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Roost.Core.Updates;
using Roost.Desktop.Controls;

namespace Roost.Desktop.Services;

/// <summary>
/// Si hay una Roost nueva: lo mismo que <c>roost update</c>, comprobado en segundo plano al abrir
/// la app y cada pocas horas (con la caché de 24 h, si no se ha desactivado en Ajustes ni con
/// <c>ROOST_NO_UPDATE_CHECK</c>) y cuando se pide. De aquí salen el aviso de la barra lateral, la
/// línea de la bandeja y lo que cuenta Ajustes. No descarga ni sustituye nada: solo avisa.
/// </summary>
public sealed partial class UpdateState : ObservableObject
{
    private static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-ES");

    private readonly AppServices _app;
    private readonly UpdateChecker _checker;

    public UpdateState(AppServices app)
    {
        _app = app;
        _checker = new UpdateChecker(app.Paths, app.Http, app.Version);
        Channel = UpdateInstructions.DetectCurrent();
        // Lo que ya se sabía (sin red): el aviso sale nada más abrir, aunque luego no haya conexión.
        Result = _checker.Cached();
    }

    /// <summary>La versión que se está ejecutando.</summary>
    public string Installed => _checker.Installed;

    /// <summary>Cómo se instaló esta Roost (Scoop, winget, un zip, compilada). Los tests de vistas lo cambian.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Advice), nameof(HowText), nameof(Command), nameof(HasCommand),
        nameof(DownloadUrl), nameof(DownloadText), nameof(HasDownload))]
    public partial InstallChannel Channel { get; internal set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate), nameof(Latest), nameof(LatestVersion), nameof(SideText), nameof(BannerTitle),
        nameof(NotesLines), nameof(HasNotes), nameof(Advice), nameof(HowText), nameof(Command), nameof(HasCommand),
        nameof(DownloadUrl), nameof(DownloadText), nameof(HasDownload), nameof(StatusText), nameof(StatusTone))]
    public partial UpdateCheck? Result { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(StatusTone), nameof(CheckLabel))]
    public partial bool Checking { get; private set; }

    public bool HasUpdate => Result?.UpdateAvailable == true;

    public ReleaseInfo? Latest => Result?.Latest;

    public string? LatestVersion => Latest?.Version.ToString();

    /// <summary>El aviso de la barra lateral.</summary>
    public string SideText => $"Roost {LatestVersion} disponible";

    public string BannerTitle => Latest is { } l
        ? $"Roost {l.Version} ya está disponible{(l.PublishedAt is { } at ? $" desde el {at.ToLocalTime().ToString("d-MMM-yyyy", Es)}" : "")}"
        : "";

    /// <summary>Las notas de la release en corto (las enteras, en su página).</summary>
    public IReadOnlyList<string> NotesLines => HasUpdate ? GitHubReleases.Summarize(Latest?.Notes, maxLines: 4) : [];

    public bool HasNotes => NotesLines.Count > 0;

    public UpdateAdvice Advice => UpdateInstructions.For(Channel, Latest, OperatingSystem.IsWindows());

    /// <summary>Cómo actualizar, según cómo se instaló.</summary>
    public string HowText => Advice switch
    {
        { Channel: InstallChannel.Scoop } => "La instalaste con Scoop. Para actualizarla, sal de Roost (con el stack parado) y ejecuta en una terminal:",
        { Channel: InstallChannel.Winget } => "La instalaste con winget. Para actualizarla, sal de Roost (con el stack parado) y ejecuta en una terminal:",
        { Channel: InstallChannel.Source } => "La compilaste desde el código. Para actualizarla, para el stack y ejecuta en la carpeta del repositorio:",
        { Channel: InstallChannel.Installer, Package: { } setup } => $"La instalaste con el instalador. Descarga {setup} y ejecútalo: actualiza Roost sin tocar tus datos ni tus sitios.",
        { Channel: InstallChannel.Installer } => "La instalaste con el instalador. Descarga el nuevo de su página y ejecútalo: actualiza Roost sin tocar tus datos ni tus sitios.",
        { Package: { } package } => $"Descarga {package} y, con Roost cerrado y el stack parado, sustituye con su contenido la carpeta de Roost.",
        _ => "Descárgala de su página y, con Roost cerrado y el stack parado, sustituye con ella la carpeta de Roost.",
    };

    public string? Command => Advice.Command;

    public bool HasCommand => HasUpdate && Command is not null;

    /// <summary>Lo que hay que bajar: el instalador nuevo, o el zip de esta release si se instaló a mano.</summary>
    public Uri? DownloadUrl => Advice.Channel switch
    {
        InstallChannel.Manual => Latest?.PackageFor(OperatingSystem.IsWindows())?.Url,
        InstallChannel.Installer => Latest?.Installer?.Url,
        _ => null,
    };

    public string DownloadText => Advice.Package is { } package ? $"Descargar {package}" : "Descargar";

    public bool HasDownload => HasUpdate && DownloadUrl is not null;

    public string CheckLabel => Checking ? "Buscando…" : "Buscar actualizaciones";

    /// <summary>La línea de estado de «Acerca de Roost».</summary>
    public string StatusText
    {
        get
        {
            if (Checking) return "Buscando la última versión en GitHub…";
            if (Result is not { } r) return "Aún no se ha comprobado si hay una versión nueva.";
            var when = r.CheckedAt is { } at ? $" · comprobado {Ago(at, DateTimeOffset.Now)}" : "";
            return r switch
            {
                { Status: UpdateStatus.Failed } => $"No se pudo comprobar: {r.Error?.TrimEnd('.')}."
                    + (r.Latest is { } l ? $" La última que se conocía es la {l.Version}{when}." : ""),
                { Status: UpdateStatus.NoReleases } => $"Aún no hay ninguna versión publicada{when}",
                { UpdateAvailable: true } => $"Hay una versión nueva: {r.Latest!.Name}{when}",
                { InstalledIsNewer: true } => $"Esta build es más nueva que la última publicada ({r.Latest!.Version}){when}",
                _ => $"Estás en la última versión{when}",
            };
        }
    }

    public Tone StatusTone => Checking ? Tone.Neutral : Result switch
    {
        { UpdateAvailable: true } => Tone.Accent,
        { Status: UpdateStatus.Failed } => Tone.Warn,
        { Status: UpdateStatus.UpToDate } => Tone.Ok,
        _ => Tone.Neutral,
    };

    /// <summary>Rehace «comprobado hace…» (al entrar en Ajustes: la app puede llevar días en la bandeja).</summary>
    public void RefreshText() => OnPropertyChanged(nameof(StatusText));

    /// <summary>Al abrir la app y cada pocas horas: con la caché de 24 h, y solo si no se ha desactivado.</summary>
    public Task AutoCheckAsync() =>
        _app.Prefs.CheckUpdates && !UpdateChecker.DisabledByEnvironment ? CheckAsync(force: false) : Task.CompletedTask;

    private Task<UpdateCheck>? _check;

    /// <summary>Comprueba (nunca lanza). Si ya hay una comprobación en curso, espera a esa.</summary>
    public Task<UpdateCheck> CheckAsync(bool force) => Checking && _check is not null ? _check : _check = CheckCoreAsync(force);

    private async Task<UpdateCheck> CheckCoreAsync(bool force)
    {
        Checking = true;
        try
        {
            var result = await Task.Run(() => _checker.CheckAsync(force));
            Result = result;
            return result;
        }
        finally
        {
            Checking = false;
        }
    }

    /// <summary>«ahora mismo», «hace 5 min», «hace 3 h», «ayer», «hace 4 días».</summary>
    public static string Ago(DateTimeOffset at, DateTimeOffset now)
    {
        var age = now - at;
        if (age < TimeSpan.FromMinutes(1)) return "ahora mismo";
        if (age < TimeSpan.FromHours(1)) return $"hace {(int)age.TotalMinutes} min";
        if (age < TimeSpan.FromHours(24)) return $"hace {(int)age.TotalHours} h";
        if (age < TimeSpan.FromHours(48)) return "ayer";
        return $"hace {(int)age.TotalDays} días";
    }
}
