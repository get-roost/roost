using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Roost.Core.Stack;

namespace Roost.Desktop.ViewModels;

/// <summary>Lo que devuelve el diálogo de contraseña: la nueva (vacía = sin contraseña, solo root) y si solo hay que guardarla.</summary>
public sealed record DbPasswordChoice(string Password, bool SaveOnly);

/// <summary>
/// «Contraseña de root» (o la de un usuario): la contraseña dos veces, ver u ocultar, y para root
/// la opción de solo guardarla si ya se cambió por fuera. Devuelve <see cref="DbPasswordChoice"/> o null.
/// </summary>
public sealed partial class DbPasswordDialogViewModel : DialogViewModel
{
    /// <summary>La regla de <see cref="DbAdmin.IsValidPassword"/>, con tildes para la app.</summary>
    public const string Rule = "letras, números y símbolos ASCII, sin espacios ni ' \" \\ ` (hasta 64)";

    public DbPasswordDialogViewModel(string title, string subtitle, bool forRoot)
    {
        Title = title;
        Subtitle = subtitle;
        ForRoot = forRoot;
    }

    public string Title { get; }

    public string Subtitle { get; }

    /// <summary>root admite la vacía (volver a sin contraseña) y «solo guardarla»; un usuario, no.</summary>
    public bool ForRoot { get; }

    public string Note => ForRoot
        ? "Roost la guarda en tu equipo para sus propios comandos: la parada limpia, la consola, crear y listar bases de datos."
        : "Los proyectos que usen este usuario tendrán que cambiar su DB_PASSWORD.";

    public string EmptyHint => ForRoot ? "Vacía = root sin contraseña, como el primer día." : "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Hint), nameof(CanConfirm), nameof(ConfirmLabel), nameof(HasHint))]
    public partial string Password { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Hint), nameof(CanConfirm), nameof(HasHint))]
    public partial string Repeat { get; set; } = "";

    /// <summary>Ver u ocultar lo tecleado (los dos campos a la vez).</summary>
    [ObservableProperty] public partial bool Show { get; set; }

    /// <summary>Solo decirle a Roost cuál es: sin ALTER USER (ya se cambió por fuera).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConfirmLabel))]
    public partial bool SaveOnly { get; set; }

    /// <summary>Lo que falla, o vacío si se puede confirmar.</summary>
    public string Hint
    {
        get
        {
            if (!DbAdmin.IsValidPassword(Password)) return $"Solo {Rule}.";
            if (Password.Length == 0 && !ForRoot) return "La contraseña no puede estar vacía.";
            if (Repeat.Length > 0 && Repeat != Password) return "No coinciden.";
            return "";
        }
    }

    public bool HasHint => Hint.Length > 0;

    public bool CanConfirm => Hint.Length == 0 && Repeat == Password && (ForRoot || Password.Length > 0);

    public string ConfirmLabel => ForRoot && Password.Length == 0 ? "Quitar la contraseña" : SaveOnly ? "Guardar en Roost" : "Cambiar";

    [RelayCommand]
    private void ToggleShow() => Show = !Show;

    [RelayCommand]
    private void Confirm()
    {
        if (CanConfirm) Close(new DbPasswordChoice(Password, SaveOnly));
    }
}
