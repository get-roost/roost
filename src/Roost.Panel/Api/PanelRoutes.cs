using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Roost.Core;

namespace Roost.WebPanel.Api;

/// <summary>
/// La API JSON del panel, bajo <c>/api</c>. Cada pantalla tiene su grupo (<see cref="StackApi"/>,
/// <see cref="SitesApi"/>, <see cref="PhpApi"/>…); aquí lo común: el mapeo, los errores (un
/// <see cref="RoostException"/> es un 400 con su texto, el mismo que la CLI y la app) y las tareas largas.
/// </summary>
public static class PanelRoutes
{
    public static void Map(IEndpointRouteBuilder web, PanelApp app)
    {
        var api = web.MapGroup("/api");
        api.MapGet("/ping", () => Results.Json(new { ok = true, version = Core.Updates.RoostVersion.Current }));

        // Tareas largas: instalar, importar, escanear…
        api.MapGet("/jobs", () => Results.Json(app.Jobs.Running()));
        api.MapGet("/jobs/{id}", (string id) => app.Jobs.Find(id) is { } job ? Results.Json(job.View()) : Results.NotFound());
        api.MapPost("/jobs/{id}/cancel", (string id) => app.Jobs.Cancel(id) ? Results.Json(new { ok = true }) : Results.NotFound());

        StackApi.Map(api, app);
        SitesApi.Map(api, app);
        PhpApi.Map(api, app);
        WebApi.Map(api, app);
        DbApi.Map(api, app);
        AuditApi.Map(api, app);
        LogsApi.Map(api, app);
        SettingsApi.Map(api, app);
    }

    /// <summary>Ejecuta <paramref name="action"/> y convierte sus errores en una respuesta con su texto.</summary>
    public static async Task<IResult> Guard(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (RoostException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException or InvalidOperationException or ArgumentException)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    public static Task<IResult> Guard(Func<IResult> action) => Guard(() => Task.FromResult(action()));

    public static IResult Ok(object? value = null) => Results.Json(value ?? new { ok = true });

    public static IResult Fail(string message, int status = StatusCodes.Status400BadRequest) =>
        Results.Json(new { error = message }, statusCode: status);

    /// <summary>Una tarea larga: contesta con su id para que la página la sondee.</summary>
    public static IResult Started(Job job) => Results.Json(new { job = job.View() });
}
