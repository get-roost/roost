using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Roost.Core.Platform;
using Roost.Core.Sites;

namespace Roost.Desktop.ViewModels;

/// <summary>
/// «Que el navegador confíe en tus sitios .test»: qué es la CA local, su huella y dónde está su
/// clave, y el aviso de Windows que viene a continuación. true = seguir y confiar; con
/// <see cref="ConfigureFirefox"/>, también en Firefox, que no mira el almacén de Windows por defecto.
/// </summary>
public sealed partial class TrustCaDialogViewModel : DialogViewModel
{
    public TrustCaDialogViewModel(LocalCa ca, int firefoxProfiles)
    {
        var info = ca.Describe();
        Subject = info?.Subject ?? "Roost Local CA";
        Thumbprint = info is { } i ? $"SHA-1 {i.Thumbprint}" : "";
        KeyFile = ca.CaKeyFile;
        HasFirefox = firefoxProfiles > 0;
        ConfigureFirefox = HasFirefox;
        FirefoxText = firefoxProfiles == 1
            ? "Configurar también Firefox (usa su propio almacén; tienes 1 perfil)"
            : $"Configurar también Firefox (usa su propio almacén; tienes {firefoxProfiles} perfiles)";
    }

    public string Subject { get; }

    public string Thumbprint { get; }

    public string KeyFile { get; }

    public bool HasFirefox { get; }

    public string FirefoxText { get; }

    [ObservableProperty] public partial bool ConfigureFirefox { get; set; }

    [RelayCommand]
    private void Continue() => Close(true);
}
