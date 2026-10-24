using Roost.Core.Platform;
using Roost.Core.Sites;

namespace Roost.Core.Tests;

/// <summary>
/// sites.json leído y escrito a la vez desde varios hilos (la app pone al día el git de un sitio
/// mientras comprueba novedades): ninguna lectura falla por pillar el fichero mientras se sustituye,
/// y ningún alta se pierde.
/// </summary>
public class SiteRegistryConcurrencyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "roost-registro-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task LeerYEscribirALaVez_NiFallaNiPierdeAltas()
    {
        var registry = new SiteRegistry(new RoostPaths(_root, Path.Combine(_root, "cache")));
        var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        using var stop = new CancellationTokenSource();

        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try { registry.Load(); }
                catch (Exception ex) { errors.Add(ex); }
            }
        })).ToList();

        var writers = Enumerable.Range(0, 4).Select(w => Task.Run(() =>
        {
            for (var i = 0; i < 25; i++)
            {
                try { registry.Upsert(new Site { Name = $"s{w}-{i}", Folder = Path.Combine(_root, "www", $"s{w}-{i}") }); }
                catch (Exception ex) { errors.Add(ex); }
            }
        })).ToList();

        await Task.WhenAll(writers);
        stop.Cancel();
        await Task.WhenAll(readers);

        Assert.Empty(errors);
        Assert.Equal(100, registry.Load().Count);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(registry.FilePath)!, "*.tmp"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
