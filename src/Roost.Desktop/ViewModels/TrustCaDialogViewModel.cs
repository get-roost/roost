using CommunityToolkit.Mvvm.Input;
using Roost.Core.Sites;

namespace Roost.Desktop.ViewModels;

/// <summary>
/// «Que el navegador confíe en tus sitios .test»: qué es la CA local, su huella y dónde está su
/// clave, y el aviso de Windows que viene a continuación. true = seguir y confiar.
/// </summary>
public sealed partial class TrustCaDialogViewModel : DialogViewModel
{
    public TrustCaDialogViewModel(LocalCa ca)
    {
        var info = ca.Describe();
        Subject = info?.Subject ?? "Roost Local CA";
        Thumbprint = info is { } i ? $"SHA-1 {i.Thumbprint}" : "";
        KeyFile = ca.CaKeyFile;
    }

    public string Subject { get; }

    public string Thumbprint { get; }

    public string KeyFile { get; }

    [RelayCommand]
    private void Continue() => Close(true);
}
