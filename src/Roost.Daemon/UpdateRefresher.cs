using Roost.Core.Platform;
using Roost.Core.Updates;

namespace Roost.Daemon;

/// <summary>
/// Mientras roostd está en marcha, refresca el aviso de versión nueva de Roost (la caché de
/// <see cref="UpdateChecker"/>) para quien usa solo la CLI: <c>roost status</c> y <c>roost up</c>
/// enseñan su línea con lo que haya en esa caché, y sin esto solo se rellenaba con
/// <c>roost update</c> o desde la app. Comprueba al minuto de arrancar, para no frenar
/// <c>up</c>, y después cada 6 h; la caché de 24 h decide si de verdad va a la red. Apagado con
/// <c>ROOST_NO_UPDATE_CHECK</c> y en CI. Nunca lanza: lo que pase, al log.
/// </summary>
internal static class UpdateRefresher
{
    public static readonly TimeSpan FirstCheckAfter = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan CheckEvery = TimeSpan.FromHours(6);

    public static async Task RunAsync(RoostPaths paths, FileLog log, CancellationToken stop)
    {
        if (UpdateChecker.AutomaticChecksOff)
        {
            log.Write("aviso de version: apagado (ROOST_NO_UPDATE_CHECK o CI)");
            return;
        }
        try
        {
            using var http = new HttpClient();
            await Task.Delay(FirstCheckAfter, stop);
            while (!stop.IsCancellationRequested)
            {
                var check = await new UpdateChecker(paths, http).CheckAsync(cancellation: stop);
                // Con la caché de hoy no se ha ido a la red: no hay nada nuevo que apuntar.
                if (!check.FromCache || check.Status == UpdateStatus.Failed)
                    log.Write("aviso de version: " + Describe(check));
                await Task.Delay(CheckEvery, stop);
            }
        }
        catch (OperationCanceledException)
        {
            // roostd se para.
        }
        catch (Exception ex)
        {
            log.Write("aviso de version: " + ex.Message);
        }
    }

    private static string Describe(UpdateCheck check) => check switch
    {
        { Status: UpdateStatus.Failed } => $"no se pudo comprobar ({check.Error})",
        { UpdateAvailable: true, Latest: { } latest } => $"hay una Roost nueva, {latest.Version} (instalada la {check.Installed})",
        { Latest: null } => "aun no hay ninguna version publicada",
        _ => $"al dia ({check.Installed})",
    };
}
