using Roost.Core;
using Roost.WebPanel;

namespace Roost.Panel.Tests;

/// <summary>Las tareas largas del panel: pasos, progreso, resultado, fallo (con el texto de Roost) y cancelación.</summary>
public class JobsTests
{
    [Fact]
    public async Task UnaTarea_PasaPorSusPasos_YAcabaConSuResultado()
    {
        var jobs = new Jobs();
        var gate = new TaskCompletionSource();
        var job = jobs.Start("Instalando PHP 8.4", "NTS · x64", [("Descargar", null), ("Verificar", "SHA-256"), ("Dejarla en su carpeta", null)], async ctx =>
        {
            ctx.Begin(0, "Descargando…");
            ctx.Progress(0.5);
            await gate.Task;
            ctx.Finish("Listo.");
            return new { name = "8.4.27-nts-x64" };
        });

        var view = job.View();
        Assert.Equal("running", view.State);
        Assert.True(view.CanCancel);
        Assert.Equal(3, view.Steps.Count);
        Assert.Single(jobs.Running());
        await WaitUntil(() => job.View().Steps[0].State == "active");
        Assert.Equal("Descargando…", job.View().Message);
        Assert.Equal(0.5, job.View().Progress);

        gate.SetResult();
        await WaitUntil(() => !job.IsRunning);
        view = job.View();
        Assert.Equal("done", view.State);
        Assert.All(view.Steps, s => Assert.Equal("done", s.State));
        Assert.Equal(1, view.Progress);
        Assert.False(view.CanCancel);
        Assert.NotNull(view.Result);
        Assert.Empty(jobs.Running());
        Assert.Same(job, jobs.Find(job.Id));
    }

    [Fact]
    public async Task UnFallo_DeRoost_SaleConSuTexto_YElPasoEnCursoFallido()
    {
        var jobs = new Jobs();
        var job = jobs.Start("Instalando", null, [("Uno", null), ("Dos", null)], ctx =>
        {
            ctx.Begin(1);
            throw new RoostException("El puerto 8080 esta ocupado.");
        });
        await WaitUntil(() => !job.IsRunning);
        var view = job.View();
        Assert.Equal("failed", view.State);
        Assert.Equal("El puerto 8080 esta ocupado.", view.Error);
        Assert.Equal("done", view.Steps[0].State);
        Assert.Equal("failed", view.Steps[1].State);
    }

    [Fact]
    public async Task Cancelar_ParaLaTarea_YLoDice()
    {
        var jobs = new Jobs();
        var job = jobs.Start("Instalando", null, [("Descargar", null)], async ctx =>
        {
            ctx.Begin(0);
            await Task.Delay(Timeout.Infinite, ctx.Cancellation);
            return null;
        });
        await WaitUntil(() => job.View().Steps[0].State == "active");
        Assert.True(jobs.Cancel(job.Id));
        await WaitUntil(() => !job.IsRunning);
        Assert.Equal("cancelled", job.View().State);
        Assert.Contains("Cancelado", job.View().Error);
        Assert.False(jobs.Cancel(job.Id));   // ya no corre
        Assert.False(jobs.Cancel("no-existe"));
    }

    [Fact]
    public async Task UnaTarea_QueNoSePuedeCancelar_NoSeCancela()
    {
        var jobs = new Jobs();
        var gate = new TaskCompletionSource();
        var job = jobs.Start("Escaneando", null, [("Buscar", null)], async _ => { await gate.Task; return null; }, canCancel: false);
        Assert.False(job.View().CanCancel);
        Assert.False(jobs.Cancel(job.Id));
        gate.SetResult();
        await WaitUntil(() => !job.IsRunning);
        Assert.Equal("done", job.View().State);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("La tarea no llego a ese estado.");
            await Task.Delay(20);
        }
    }
}
