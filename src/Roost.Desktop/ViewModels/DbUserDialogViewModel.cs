using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Roost.Core.Stack;

namespace Roost.Desktop.ViewModels;

/// <summary>Lo que devuelve «Nuevo usuario»: nombre, contraseña y la base de datos (null = todas).</summary>
public sealed record NewDbUser(string Name, string Password, string? Database);

/// <summary>Una opción del desplegable «Acceso a»: todas, o una base de datos.</summary>
public sealed record DbAccessOption(string Label, string? Database)
{
    public override string ToString() => Label;
}

/// <summary>
/// «Nuevo usuario»: usuario, contraseña (dos veces, ver u ocultar) y a qué llega. Se crea como
/// <c>'usuario'@'%'</c>, que vale porque el servidor solo escucha en este equipo.
/// </summary>
public sealed partial class DbUserDialogViewModel : DialogViewModel
{
    public DbUserDialogViewModel(string dbLabel, IReadOnlyList<string> databases, string? preselected = null)
    {
        Subtitle = $"En {dbLabel}, como 'usuario'@'%': vale porque solo escucha en este equipo.";
        Options = [new DbAccessOption("Todas las bases de datos", null), .. databases.Select(d => new DbAccessOption(d, d))];
        Access = Options.FirstOrDefault(o => o.Database == preselected) ?? Options[0];
    }

    public string Subtitle { get; }

    public IReadOnlyList<DbAccessOption> Options { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NameHint), nameof(CanConfirm), nameof(HasNameHint))]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PasswordHint), nameof(CanConfirm), nameof(HasPasswordHint))]
    public partial string Password { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PasswordHint), nameof(CanConfirm), nameof(HasPasswordHint))]
    public partial string Repeat { get; set; } = "";

    [ObservableProperty] public partial bool Show { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessHint))]
    public partial DbAccessOption Access { get; set; }

    public string NameHint
    {
        get
        {
            var name = Name.Trim();
            if (name.Length == 0) return "";
            if (!DbAdmin.IsValidUserName(name)) return "Solo letras, números y _ (hasta 32).";
            if (DbAdmin.SystemUsers.Contains(name)) return $"{name} es una cuenta del servidor: elige otro nombre.";
            return "";
        }
    }

    public string PasswordHint
    {
        get
        {
            if (!DbAdmin.IsValidPassword(Password)) return $"Solo {DbPasswordDialogViewModel.Rule}.";
            if (Repeat.Length > 0 && Repeat != Password) return "No coinciden.";
            return "";
        }
    }

    public bool HasNameHint => NameHint.Length > 0;
    public bool HasPasswordHint => PasswordHint.Length > 0;

    public string AccessHint => Access.Database is null
        ? "Todos los privilegios sobre todas las bases de datos, también las que crees después."
        : $"Todos los privilegios sobre {Access.Database} y nada más: lo normal para un proyecto.";

    public bool CanConfirm =>
        Name.Trim().Length > 0 && NameHint.Length == 0 && Password.Length > 0 && PasswordHint.Length == 0 && Repeat == Password;

    [RelayCommand]
    private void ToggleShow() => Show = !Show;

    [RelayCommand]
    private void Confirm()
    {
        if (CanConfirm) Close(new NewDbUser(Name.Trim(), Password, Access.Database));
    }
}
