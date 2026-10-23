using Roost.Core.Platform;
using Roost.Desktop.Controls;
using Roost.Desktop.Services;

namespace Roost.Desktop.ViewModels;

/// <summary>
/// Antes de instalar PHP o el servidor web: si falta el Visual C++ Redistributable (o, para
/// Apache, es anterior al 14.50), avisar con el enlace. Se puede seguir igualmente: la
/// instalación no depende de él, pero el binario no arrancará hasta que esté.
/// </summary>
internal static class VcRedistGuard
{
    // «Instalar igualmente» vale para el resto de la sesión: la bienvenida instala varios PHP
    // seguidos y no tiene que preguntar en cada uno. Se recuerda lo aceptado para Apache (14.50)
    // aparte, porque pide más.
    private static bool _acceptedAny;
    private static bool _acceptedApache;

    /// <param name="what">Lo que se va a instalar («PHP 8.4.27», «Apache 2.4.69», «nginx 1.30.6»).</param>
    /// <param name="forApache">Exigir además el 14.50 (Apache de Apache Lounge).</param>
    /// <param name="who">Quién lo necesita, si no es <paramref name="what"/> (nginx no, pero sí el PHP que sirve).</param>
    /// <returns>true para seguir con la instalación; false si el usuario prefiere parar (o ir a descargarlo).</returns>
    public static async Task<bool> ConfirmAsync(MainViewModel main, string what, bool forApache, string? who = null)
    {
        var status = await Task.Run(VcRedist.Detect);
        if (VcRedist.Warning(status, forApache) is null) return true;
        if (forApache ? _acceptedApache : _acceptedAny) return true;

        var missing = !status.Installed;
        var choice = await main.ShowDialogAsync(new MessageDialogViewModel
        {
            Title = missing ? "Falta el Visual C++ Redistributable" : $"El Visual C++ Redistributable es antiguo ({status.Describe()})",
            Subtitle = missing ? $"{who ?? what} lo necesita para arrancar" : $"{who ?? what} necesita el {VcRedistStatus.ApacheMinimum} o posterior",
            Text = "Es un componente gratuito de Microsoft (x64) que usan PHP, MariaDB, MySQL y Apache. Descárgalo, instálalo y vuelve. "
                   + $"También puedes instalar {what} ahora, pero no arrancará hasta que esté.",
            Detail = VcRedistStatus.DownloadUrl,
            Tone = Tone.Warn,
            Buttons =
            [
                new DialogButton("Cancelar", null, Left: true),
                new DialogButton("Instalar igualmente", "continue"),
                new DialogButton("Descargar vc_redist.x64.exe", "download", "primary"),
            ],
        });
        if (choice is "download")
        {
            await main.RunAsync("No se pudo abrir el navegador", () => { Shell.OpenUrl(VcRedistStatus.DownloadUrl); return Task.CompletedTask; });
            return false;
        }
        if (choice is not "continue") return false;
        if (forApache) _acceptedApache = true;
        // Lo que vale para Apache vale para todo; lo que vale para PHP, si falta del todo, también para Apache.
        _acceptedAny = true;
        if (!status.Installed) _acceptedApache = true;
        return true;
    }
}
