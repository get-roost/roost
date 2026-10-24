using Roost.Core.Install;
using Roost.Desktop.Controls;

namespace Roost.Desktop.ViewModels;

/// <summary>
/// Instalar Redis, Mailpit o Adminer desde la app: el diálogo «Instalando» de siempre (descarga,
/// SHA-256 antes de nada, instalación atómica), que también se puede pasar a segundo plano.
/// </summary>
internal static class ServiceInstall
{
    /// <summary>Redis de esta versión de Roost. true si ha quedado instalado.</summary>
    public static Task<bool> RedisAsync(MainViewModel main)
    {
        var build = RedisCatalog.Current;
        var installer = new RedisInstaller(main.App.Paths, main.App.Http);
        var target = Path.Combine(main.App.Paths.RedisBin, build.Version);
        return RunAsync(main, build.Label, target, new InstallDialogViewModel(
            $"Instalando {build.Label}",
            $"build MSYS2 de redis-windows · desde {build.Url.Host}",
            13_938_657,
            [
                new("Versión fijada en Roost", "· SHA-256 comprobado con la release y con su run de Actions"),
                new("Descargando el zip", null),
                new("Verificar el SHA-256", "· antes de extraer nada"),
                new($"Extraer en {target}", null),
            ],
            async (d, cancel) =>
            {
                d.Step(1, "Descargando");
                await installer.InstallAsync(build, new Progress<long>(d.Downloaded), cancel);
                d.Step(3, "Extrayendo");
            }));
    }

    /// <summary>Mailpit de esta versión de Roost. true si ha quedado instalado.</summary>
    public static Task<bool> MailpitAsync(MainViewModel main)
    {
        var build = MailpitCatalog.Current;
        var installer = new MailpitInstaller(main.App.Paths, main.App.Http);
        var target = Path.Combine(main.App.Paths.MailpitBin, build.Version);
        return RunAsync(main, build.Label, target, new InstallDialogViewModel(
            $"Instalando {build.Label}",
            $"Windows amd64 · desde {build.Url.Host}",
            10_632_291,
            [
                new("Versión fijada en Roost", "· SHA-256 comprobado con la release"),
                new("Descargando el zip", null),
                new("Verificar el SHA-256", "· antes de extraer nada"),
                new($"Extraer en {target}", null),
            ],
            async (d, cancel) =>
            {
                d.Step(1, "Descargando");
                await installer.InstallAsync(build, new Progress<long>(d.Downloaded), cancel);
                d.Step(3, "Extrayendo");
            }));
    }

    /// <summary>Adminer de esta versión de Roost. true si ha quedado instalado.</summary>
    public static Task<bool> AdminerAsync(MainViewModel main)
    {
        var build = AdminerCatalog.Current;
        var installer = new AdminerInstaller(main.App.Paths, main.App.Http);
        var target = installer.InstallDirectory(build);
        return RunAsync(main, build.Label, target, new InstallDialogViewModel(
            $"Instalando {build.Label}",
            $"un solo fichero PHP, solo MySQL y MariaDB · desde {build.Url.Host}",
            439_622,
            [
                new("Versión fijada en Roost", "· el mismo SHA-256 en GitHub y en adminer.org"),
                new("Descargando adminer.php", null),
                new("Verificar el SHA-256", "· antes de copiar nada"),
                new($"Copiar a {target}", "· fuera de la www, que es tuya"),
            ],
            async (d, cancel) =>
            {
                d.Step(1, "Descargando");
                await installer.InstallAsync(build, new Progress<long>(d.Downloaded), cancel);
                d.Step(3, "Copiando");
            }));
    }

    /// <summary>
    /// Enseña el diálogo y espera al resultado aunque se haya pasado a segundo plano: entonces el
    /// error, o el éxito, no se ha visto aún y se dice.
    /// </summary>
    private static async Task<bool> RunAsync(MainViewModel main, string what, string where, InstallDialogViewModel dialog)
    {
        var run = dialog.RunAsync();
        var shown = await main.ShowDialogAsync(dialog);
        await run;
        main.NotifyConfigChanged();
        if (shown is null && dialog.Failed)
            await main.InfoAsync($"No se instaló {what}", dialog.Error!, Tone.Bad);
        else if (shown is null && !dialog.Failed)
            await main.InfoAsync($"{what} instalado", $"Ya está en {where}.", Tone.Ok);
        return await dialog.Finished;
    }
}
