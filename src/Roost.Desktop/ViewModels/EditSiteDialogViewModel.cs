using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Roost.Core.Sites;
using Roost.Core.Stack;

namespace Roost.Desktop.ViewModels;

/// <summary>
/// Lo que devuelve «Editar…»: el nombre nuevo (null si no cambia), la carpeta nueva (null si no
/// cambia), si se mueve la carpeta o solo se apunta a otra, y si se copia la base de datos.
/// </summary>
public sealed record EditSiteRequest(string? NewName, string? NewFolder, bool MoveFiles, bool CopyDatabase);

/// <summary>
/// Sitios avanzados (C2): «Editar…» del panel de un sitio: renombrarlo (tienda → shop) y servirlo
/// desde otra carpeta, moviendo la del proyecto allí o solo apuntando a una que ya existe. Si al
/// renombrar cambiaría el nombre de su base de datos, pregunta si dejarla o copiarla al nuevo. No
/// toca nada: devuelve <see cref="EditSiteRequest"/> y lo hace <c>SitesViewModel</c>.
/// </summary>
public sealed partial class EditSiteDialogViewModel : DialogViewModel
{
    private readonly MainViewModel _main;
    private readonly Site _site;
    private readonly IReadOnlyList<Site> _all;

    public EditSiteDialogViewModel(MainViewModel main, Site site, IReadOnlyList<Site> all, bool dbRunning)
    {
        _main = main;
        _site = site;
        _all = all;
        DbRunning = dbRunning;
        Name = site.Name;
        Folder = site.Folder;
    }

    public string Title => $"Editar {_site.Host}";

    public string Subtitle => _site.ParkedIn is { } parked
        ? $"Sale de la carpeta aparcada {parked}: renombrado o movido, deja de ser de ella si sale de esa carpeta."
        : "El nombre es el de https://<nombre>.test; la carpeta, la del proyecto.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NameHint), nameof(NameIsError), nameof(NameChanged), nameof(ShowDatabase), nameof(OldDb), nameof(NewDb), nameof(CanConfirm), nameof(Summary), nameof(CopyLabel))]
    public partial string Name { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FolderChanged), nameof(FolderHint), nameof(FolderIsError), nameof(CanConfirm), nameof(Summary))]
    public partial string Folder { get; set; }

    /// <summary>true: Roost mueve la carpeta del proyecto a la nueva ruta; false: solo apunta a ella.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FolderHint), nameof(FolderIsError), nameof(CanConfirm), nameof(Summary), nameof(PointOnly))]
    public partial bool MoveFiles { get; set; } = true;

    public bool PointOnly
    {
        get => !MoveFiles;
        set => MoveFiles = !value;
    }

    /// <summary>Copiar la base de datos al nombre nuevo (si no, se queda la de antes).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary), nameof(KeepDatabase))]
    public partial bool CopyDatabase { get; set; }

    public bool KeepDatabase
    {
        get => !CopyDatabase;
        set => CopyDatabase = !value;
    }

    public bool DbRunning { get; }

    private string NewNameValue
    {
        get
        {
            var name = Name.Trim().ToLowerInvariant();
            return name.EndsWith(".test", StringComparison.Ordinal) ? name[..^".test".Length] : name;
        }
    }

    public bool NameChanged => NewNameValue != _site.Name;

    private string? NameProblem =>
        !NameChanged ? null
        : !Site.IsValidName(NewNameValue) ? "Solo minúsculas, números y guiones, sin guion al principio ni al final."
        : _all.FirstOrDefault(s => s.Name == NewNameValue) is { } other ? $"Ya hay un sitio {other.Host}."
        : SiteAliases.WhyNotName(NewNameValue, _all, except: _site.Name) is not null ? $"{NewNameValue}.test ya es un alias de otro sitio."
        : _site.Aliases?.Contains($"{NewNameValue}.test", StringComparer.Ordinal) == true ? $"{NewNameValue}.test es un alias de este sitio: quítalo antes en «Alias…»."
        : null;

    public bool NameIsError => NameProblem is not null;

    public string NameHint => NameProblem
        ?? (NameChanged
            ? $"Pasará a https://{NewNameValue}.test: su vhost, su certificado y su línea del hosts{(SiteAliases.Others(_site).Count < (_site.Aliases?.Count ?? 0) ? ", y sus subdominios" : "")}. Su git y sus registros van con él."
            : $"Ahora: https://{_site.Host}");

    private string FullFolder
    {
        get
        {
            try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(Folder.Trim(), _main.App.Paths.Www)); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return ""; }
        }
    }

    public bool FolderChanged => FullFolder.Length > 0 && !RoostPathsSame(FullFolder, _site.Folder);

    private static bool RoostPathsSame(string a, string b) => Roost.Core.Platform.RoostPaths.SameFolder(a, b);

    private string? FolderProblem
    {
        get
        {
            if (!FolderChanged) return FullFolder.Length == 0 ? "Esa ruta no vale." : null;
            var target = FullFolder;
            var exists = Directory.Exists(target);
            if (!MoveFiles) return exists ? null : "No existe: elige una carpeta que ya tenga el proyecto, o «Mover la carpeta».";
            if (!Directory.Exists(_site.Folder)) return "La carpeta de ahora ya no existe: no hay nada que mover. Usa «Solo apuntar».";
            if (exists && Directory.EnumerateFileSystemEntries(target).Any()) return "Ya existe y no está vacía: Roost no mezcla carpetas. Elige otra, o «Solo apuntar» si el proyecto ya está ahí.";
            if (!string.Equals(Path.GetPathRoot(target), Path.GetPathRoot(_site.Folder), StringComparison.OrdinalIgnoreCase))
                return "Está en otra unidad: Roost solo mueve dentro de la misma. Muévela tú y usa «Solo apuntar».";
            var relative = Path.GetRelativePath(_site.Folder, target);
            if (relative != "." && !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative))
                return "Está dentro de la carpeta del proyecto.";
            return null;
        }
    }

    public bool FolderIsError => FolderProblem is not null;

    public string FolderHint => FolderProblem
        ?? (!FolderChanged ? "La de ahora."
            : MoveFiles ? $"Roost moverá {_site.Folder} aquí (no puede tenerla abierta nadie: el editor, una terminal, npm run dev…)."
            : Directory.Exists(Path.Combine(FullFolder, "public")) ? "Existe y tiene public/: se servirá esa. La carpeta de antes no se toca."
            : "Existe. La carpeta de antes no se toca.");

    public string OldDb => SiteManager.DatabaseOf(_site);

    public string NewDb => DbAdmin.NameFor(NewNameValue.Length == 0 ? _site.Name : NewNameValue);

    /// <summary>La pregunta de la base de datos: solo si el nombre nuevo cambiaría el de la suya.</summary>
    public bool ShowDatabase => NameChanged && !NameIsError && _site.Database is null && OldDb != NewDb;

    public string KeepLabel => $"Dejar {OldDb}: el sitio la sigue usando";

    public string CopyLabel => $"Copiar a {NewDb} y usar la copia ({OldDb} se queda, por si acaso)";

    public bool CanConfirm => (NameChanged || FolderChanged) && !NameIsError && !FolderIsError;

    /// <summary>Lo que va a pasar, en una frase.</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (NameChanged && !NameIsError) parts.Add($"pasa a llamarse {NewNameValue}.test");
            if (FolderChanged && !FolderIsError) parts.Add(MoveFiles ? $"su carpeta se mueve a {FullFolder}" : $"se sirve desde {FullFolder}");
            if (ShowDatabase) parts.Add(CopyDatabase ? $"su base de datos se copia a {NewDb}" : $"sigue con la base de datos {OldDb}");
            return parts.Count == 0 ? "No hay nada que cambiar." : char.ToUpperInvariant(parts[0][0]) + string.Join("; ", parts)[1..] + ".";
        }
    }

    [RelayCommand]
    private async Task PickFolderAsync()
    {
        var start = Directory.Exists(FullFolder) ? FullFolder : Path.GetDirectoryName(_site.Folder);
        if (await SitesViewModel.PickFolderAsync(_main, $"Carpeta de {_site.Host}", start) is { } picked)
        {
            Folder = picked;
            // Una carpeta que ya existe y tiene algo: lo normal es que el proyecto ya esté ahí.
            if (Directory.Exists(picked) && Directory.EnumerateFileSystemEntries(picked).Any()) MoveFiles = false;
        }
    }

    [RelayCommand]
    private void Confirm()
    {
        if (!CanConfirm) return;
        Close(new EditSiteRequest(NameChanged ? NewNameValue : null, FolderChanged ? FullFolder : null, MoveFiles, ShowDatabase && CopyDatabase && DbRunning));
    }
}
