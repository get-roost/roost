using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Roost.Core.Git;

namespace Roost.Desktop.ViewModels;

/// <summary>Lo que devuelve <see cref="GitTokenDialogViewModel"/>: el token y, si se dio, el usuario.</summary>
public sealed record GitTokenEntry(string Token, string? User);

/// <summary>
/// «Sustituir token…» / «Guardar un token…» (Acceso… y Ajustes › Git): el token tapado, el usuario
/// opcional y dónde se guarda. Nunca enseña el que hay: solo lo sustituye.
/// </summary>
public sealed partial class GitTokenDialogViewModel : DialogViewModel
{
    private readonly string _host;

    public GitTokenDialogViewModel(string title, string where, string host, string? user, string? newTokenUrl, string permission, Action<string> openUrl)
    {
        Title = title;
        Where = where;
        _host = host;
        User = user ?? "";
        NewTokenUrl = newTokenUrl;
        Permission = permission;
        _openUrl = openUrl;
    }

    private readonly Action<string> _openUrl;

    public string Title { get; }

    /// <summary>Para qué vale: <c>github.com/acme/tienda</c>.</summary>
    public string Where { get; }

    public string? NewTokenUrl { get; }

    public bool HasNewTokenUrl => NewTokenUrl is not null;

    public string Permission { get; }

    public string StorageText => $"Se guarda en {Services.OsTexts.CredentialStore}, cifrado para tu usuario, y solo vale para {Where}. Nunca va en la dirección del repositorio ni en los registros.";

    [ObservableProperty] public partial string User { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave), nameof(Warning), nameof(HasWarning))]
    public partial string Token { get; set; } = "";

    [ObservableProperty] public partial bool ShowToken { get; set; }

    [RelayCommand]
    private void ToggleShowToken() => ShowToken = !ShowToken;

    public bool CanSave => Token.Trim().Length > 0 && !Token.Trim().Any(char.IsWhiteSpace);

    public string? Warning => Token.Trim().Length > 0 && GitCredentials.LooksLikePassword(_host, Token.Trim()) is not null
        ? $"Eso no parece un token de {_host}. Si es la contraseña de la cuenta, no servirá: {_host} solo acepta tokens."
        : null;

    public bool HasWarning => Warning is not null;

    [RelayCommand]
    private void CreateToken()
    {
        if (NewTokenUrl is { } page) _openUrl(page);
    }

    [RelayCommand]
    private void Save()
    {
        if (!CanSave) return;
        Close(new GitTokenEntry(Token.Trim(), User.Trim().Length == 0 ? null : User.Trim()));
    }
}
