using Roost.Core;
using Roost.Core.Config;
using Roost.Core.Sites;

namespace Roost.Daemon;

/// <summary>
/// Sitios avanzados (oleada 6): lo que el daemon hace por los sitios fuera de <c>up</c>. Cada
/// <c>up</c> pone antes al día el registro con el <c>.roost.toml</c> de cada proyecto (C11) y con las
/// carpetas aparcadas (C3); y, con el stack en marcha, <see cref="ParkWatch"/> avisa de una
/// subcarpeta nueva o borrada y <see cref="ParkedChangedAsync"/> la aplica (E2: solo se recarga el
/// servidor web). El daemon nunca toca el hosts (pediría permiso de administrador sin que nadie lo
/// vea): con el hosts, lo apunta en el log y la app los añade todos de una vez.
/// </summary>
internal sealed partial class StackService
{
    /// <summary>El <c>.roost.toml</c> de cada sitio y las carpetas aparcadas, al registro. Nunca lanza. Con el cerrojo cogido.</summary>
    private void SyncSiteSources()
    {
        try
        {
            var sites = new SiteManager(paths);
            var project = sites.SyncProjectFiles();
            foreach (var change in project.Changes)
                log($".roost.toml: {change.Site}.test pasa a {change.What}");
            foreach (var warning in project.Warnings)
                log($".roost.toml: {warning}");
            LogParked(sites.SyncParked(), project.NamesChanged);
        }
        catch (Exception ex) when (ex is RoostException or IOException or UnauthorizedAccessException)
        {
            log("sitios: no se pudieron poner al dia (" + ex.Message + ")");
        }
    }

    private void LogParked(ParkSync sync, bool namesChanged = false)
    {
        foreach (var site in sync.Added)
            log($"aparcado: {site.Host} nuevo ({site.Folder})");
        foreach (var site in sync.Removed)
            log($"aparcado: {site.Host} quitado (su carpeta ya no esta)");
        foreach (var skip in sync.Skipped)
            log($"aparcado: se salta {skip.Folder}: {skip.Reason}");

        if (sync.Added.Count == 0 && !namesChanged) return;
        try
        {
            if (RoostConfig.Load(paths.ConfigFile).UsesLocalDns) return;
            var missing = SiteManager.HostNames(new SiteRegistry(paths).Load()).Except(new HostsUpdater().Current()).ToList();
            if (missing.Count > 0)
                log($"faltan en el hosts: {string.Join(", ", missing)} (la app los anade de una vez, con un solo permiso; o roost site hosts)");
        }
        catch (Exception ex) when (ex is RoostException or IOException or UnauthorizedAccessException)
        {
            // solo era el aviso
        }
    }

    /// <summary>
    /// Ha cambiado algo en una carpeta aparcada: con el stack en marcha, se sincroniza y, si hay
    /// sitios nuevos o quitados, se aplica como un <c>up</c> (que con solo sitios recarga el servidor
    /// web en caliente). Parado, no hace nada: ya lo hará el siguiente <c>up</c>, o la app. true si
    /// se ha aplicado algo. Nunca lanza.
    /// </summary>
    public async Task<bool> ParkedChangedAsync()
    {
        ParkSync sync;
        await _gate.WaitAsync();
        try
        {
            if (_supervisor is null) return false;
            sync = new SiteManager(paths).SyncParked();
            LogParked(sync);
        }
        catch (Exception ex) when (ex is RoostException or IOException or UnauthorizedAccessException)
        {
            log("aparcado: no se pudo sincronizar (" + ex.Message + ")");
            return false;
        }
        finally
        {
            _gate.Release();
        }

        if (!sync.Changed) return false;
        var response = await UpAsync();
        return response.Ok;
    }
}

/// <summary>
/// Mientras roostd está en marcha, vigila las carpetas aparcadas (<see cref="ParkWatcher"/>) y pasa
/// cada cambio a <see cref="StackService.ParkedChangedAsync"/>. La lista de carpetas se vuelve a leer
/// cada pocos segundos (<c>roost park</c> la cambia desde otro proceso). Nunca lanza.
/// </summary>
internal static class ParkWatch
{
    public static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(10);

    public static async Task RunAsync(StackService stack, Core.Platform.RoostPaths paths, Action<string> log, CancellationToken stop)
    {
        using var watcher = new ParkWatcher();
        var changed = new SemaphoreSlim(0);
        watcher.Changed += () => changed.Release();
        try
        {
            while (!stop.IsCancellationRequested)
            {
                watcher.Watch(new ParkedFolders(paths).Load());
                // Espera a un cambio o al siguiente repaso de la lista, lo que llegue antes.
                if (await changed.WaitAsync(RefreshEvery, stop))
                {
                    while (changed.CurrentCount > 0) await changed.WaitAsync(stop);
                    await stack.ParkedChangedAsync();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // roostd se para.
        }
        catch (Exception ex)
        {
            log("aparcado: el vigilante se ha parado (" + ex.Message + ")");
        }
    }
}
