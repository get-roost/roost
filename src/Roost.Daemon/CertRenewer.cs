namespace Roost.Daemon;

/// <summary>
/// Mientras roostd está en marcha, renueva los certificados de los sitios que caducan pronto
/// (<c>LocalCa.RenewBefore</c>, 30 días) sin esperar al siguiente <c>up</c>: cada <c>up</c> ya
/// los renueva al preparar el plan, pero un daemon puede llevar semanas en marcha. Mira una vez
/// al día; si renueva alguno, <see cref="StackService.RenewCertificatesAsync"/> reinicia solo el
/// servidor web (la recarga en caliente llegará con la recarga selectiva). Nunca lanza: lo que
/// pase, al log.
/// </summary>
internal static class CertRenewer
{
    public static readonly TimeSpan FirstCheckAfter = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan CheckEvery = TimeSpan.FromDays(1);

    public static async Task RunAsync(StackService stack, Action<string> log, CancellationToken stop)
    {
        try
        {
            await Task.Delay(FirstCheckAfter, stop);
            while (!stop.IsCancellationRequested)
            {
                await stack.RenewCertificatesAsync();
                await Task.Delay(CheckEvery, stop);
            }
        }
        catch (OperationCanceledException)
        {
            // roostd se para.
        }
        catch (Exception ex)
        {
            log("certificados: " + ex.Message);
        }
    }
}
