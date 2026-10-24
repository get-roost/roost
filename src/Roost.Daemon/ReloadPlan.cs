using System.Text.RegularExpressions;
using Roost.Core.Services;

namespace Roost.Daemon;

/// <summary>Qué le pasa al servidor web al aplicar un cambio con el stack en marcha.</summary>
internal enum WebChange
{
    /// <summary>Nada: ni su definición ni lo que lee cambian.</summary>
    None,

    /// <summary>Misma definición, otros ficheros (un vhost, un certificado, los upstreams): se recarga en caliente.</summary>
    Reload,

    /// <summary>Otra definición (otro puerto, otra versión): se sustituye, parando el de antes.</summary>
    Restart,

    /// <summary>Otro servidor (nginx por Apache o al revés): se para el de antes y se arranca el nuevo.</summary>
    Switch,

    /// <summary>El de antes ya no está en marcha (se paró para poder validar): se arranca el nuevo.</summary>
    Start,
}

/// <summary>
/// PURO: qué hay que hacer para pasar del plan que está en marcha al nuevo sin pararlo todo (E2,
/// la recarga selectiva). Compara los servicios por nombre, por definición
/// (<see cref="ServiceDefinition.Equals(ServiceDefinition?)"/>) y, con la misma definición, por la
/// huella de lo que leen (<see cref="ServiceInputs"/>). Lo que no cambia no se toca: añadir un
/// sitio recarga el servidor web y nada más; cambiar el php.ini de una versión reinicia su pool.
/// El daemon lo ejecuta en este orden (<c>StackService.ApplyAsync</c>):
/// <list type="number">
/// <item><see cref="AddPhp"/>, antes de tocar nada: si no arrancan, se quitan y sigue lo de antes;</item>
/// <item><see cref="Stop"/> y luego <see cref="Start"/> (la base de datos, Redis, Mailpit y los de las extensiones);</item>
/// <item><see cref="RestartPhp"/>, uno a uno;</item>
/// <item>el servidor web (<see cref="Web"/>);</item>
/// <item><see cref="RemovePhp"/>, cuando el servidor web ya no les manda peticiones.</item>
/// </list>
/// </summary>
internal sealed class ReloadPlan
{
    /// <summary>php-cgi de un pool nuevo (una rama que pide un sitio) o de uno que crece: se arrancan antes de tocar nada.</summary>
    public List<ServiceDefinition> AddPhp { get; } = [];

    /// <summary>
    /// Lo que no es PHP ni el servidor web y se va o cambia (la definición de antes, en el orden de
    /// antes): se para antes de arrancar lo nuevo, que puede querer su puerto (MariaDB por MySQL).
    /// </summary>
    public List<ServiceDefinition> Stop { get; } = [];

    /// <summary>Lo nuevo que no es PHP ni el servidor web (la definición nueva, en el orden del plan).</summary>
    public List<ServiceDefinition> Start { get; } = [];

    /// <summary>php-cgi que cambian: se reinician uno a uno; con <c>Replace</c>, con su definición nueva.</summary>
    public List<(ServiceDefinition Service, bool Replace)> RestartPhp { get; } = [];

    /// <summary>php-cgi que sobran (la definición de antes): se quitan después de recargar el servidor web.</summary>
    public List<ServiceDefinition> RemovePhp { get; } = [];

    public WebChange Web { get; private set; }

    /// <summary>El servidor web del plan nuevo.</summary>
    public ServiceDefinition? NewWeb { get; private set; }

    /// <summary>El servidor web en marcha (null si no lo hay).</summary>
    public ServiceDefinition? OldWeb { get; private set; }

    public bool IsEmpty =>
        AddPhp.Count == 0 && Stop.Count == 0 && Start.Count == 0 && RestartPhp.Count == 0 && RemovePhp.Count == 0 && Web == WebChange.None;

    /// <summary>Los servicios que siguen igual (misma definición y mismos ficheros): no se tocan.</summary>
    public HashSet<string> Unchanged { get; } = new(StringComparer.Ordinal);

    /// <param name="running">Los servicios en marcha, en su orden.</param>
    /// <param name="runningWeb">El nombre del servidor web en marcha; null si no lo hay.</param>
    /// <param name="runningInputs">La huella de lo que leyó cada servicio en marcha.</param>
    /// <param name="next">Los servicios del plan nuevo, en su orden.</param>
    /// <param name="nextWeb">El nombre del servidor web del plan nuevo.</param>
    /// <param name="nextInputs">La huella de lo que leería cada servicio del plan nuevo.</param>
    public static ReloadPlan Compute(
        IReadOnlyList<ServiceDefinition> running, string? runningWeb, IReadOnlyDictionary<string, string> runningInputs,
        IReadOnlyList<ServiceDefinition> next, string nextWeb, IReadOnlyDictionary<string, string> nextInputs)
    {
        var old = running.ToDictionary(s => s.Name, StringComparer.Ordinal);
        var nextNames = next.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        var plan = new ReloadPlan { _running = old.Keys.ToHashSet(StringComparer.Ordinal), _next = nextNames };
        bool SameInputs(string name) => runningInputs.GetValueOrDefault(name) == nextInputs.GetValueOrDefault(name);

        // Lo que se va: los php-cgi, al final; lo demás, antes de arrancar lo nuevo. El servidor
        // web va aparte.
        foreach (var service in running)
        {
            if (nextNames.Contains(service.Name) || service.Name == runningWeb) continue;
            (IsPhpMember(service.Name) ? plan.RemovePhp : plan.Stop).Add(service);
        }

        foreach (var service in next)
        {
            if (service.Name == nextWeb) continue;
            var exists = old.TryGetValue(service.Name, out var current);
            var php = IsPhpMember(service.Name);
            if (!exists)
            {
                (php ? plan.AddPhp : plan.Start).Add(service);
            }
            else if (!service.Equals(current))
            {
                if (php) plan.RestartPhp.Add((service, true));
                else
                {
                    plan.Stop.Add(current!);
                    plan.Start.Add(service);
                }
            }
            else if (!SameInputs(service.Name))
            {
                if (php) plan.RestartPhp.Add((service, false));
                else
                {
                    plan.Stop.Add(current!);
                    plan.Start.Add(service);
                }
            }
            else
            {
                plan.Unchanged.Add(service.Name);
            }
        }
        // Lo que se para va en el orden de antes (se para al revés).
        plan.Stop.Sort((a, b) => IndexOf(running, a.Name).CompareTo(IndexOf(running, b.Name)));

        plan.NewWeb = next.FirstOrDefault(s => s.Name == nextWeb);
        plan.OldWeb = runningWeb is null ? null : old.GetValueOrDefault(runningWeb);
        plan.Web = plan.NewWeb is null ? WebChange.None
            : plan.OldWeb is null ? WebChange.Start
            : plan.OldWeb.Name != plan.NewWeb.Name ? WebChange.Switch
            : !plan.NewWeb.Equals(plan.OldWeb) ? WebChange.Restart
            : !SameInputs(nextWeb) ? WebChange.Reload
            : WebChange.None;
        if (plan.Web == WebChange.None && plan.NewWeb is not null) plan.Unchanged.Add(plan.NewWeb.Name);
        return plan;
    }

    private static int IndexOf(IReadOnlyList<ServiceDefinition> services, string name)
    {
        for (var i = 0; i < services.Count; i++)
            if (services[i].Name == name) return i;
        return int.MaxValue;
    }

    /// <summary>Lo que se hace, en palabras («recargado nginx», «arrancado php-7.4»), en el orden en que se cuenta.</summary>
    public IReadOnlyList<string> Describe()
    {
        var lines = new List<string>();
        if (DescribeWeb() is { } web) lines.Add(web);
        lines.AddRange(DescribeOthers());
        lines.AddRange(RestartPhp.Select(r => Pool(r.Service.Name)).Distinct().Select(pool => $"reiniciado {pool} (uno a uno)"));
        lines.AddRange(DescribePhp(AddPhp, "arrancado"));
        lines.AddRange(DescribePhp(RemovePhp, "quitado"));
        return lines;
    }

    /// <summary>El servidor web, en palabras; null si no cambia.</summary>
    public string? DescribeWeb() => Web switch
    {
        WebChange.Reload => $"recargado {NewWeb!.Name}",
        WebChange.Restart => $"reiniciado {NewWeb!.Name}",
        WebChange.Switch => $"cambiado {OldWeb!.Name} por {NewWeb!.Name}",
        WebChange.Start => $"arrancado {NewWeb!.Name}",
        _ => null,
    };

    /// <summary>La base de datos, Redis, Mailpit y los de las extensiones: «reiniciado», «quitado» o «arrancado».</summary>
    public IEnumerable<string> DescribeOthers()
    {
        var started = Start.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        var stopped = Stop.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var service in Stop)
            yield return started.Contains(service.Name) ? $"reiniciado {service.Name}" : $"quitado {service.Name}";
        foreach (var service in Start.Where(s => !stopped.Contains(s.Name)))
            yield return $"arrancado {service.Name}";
    }

    /// <summary>
    /// php-cgi que se añaden o se quitan, por pool: «arrancado php-7.4» si es el pool entero y, si
    /// solo cambia su tamaño, cada uno («arrancado php-8.4-2»).
    /// </summary>
    public IEnumerable<string> DescribePhp(IEnumerable<ServiceDefinition> members, string verb)
    {
        foreach (var pool in members.GroupBy(m => Pool(m.Name)))
        {
            // Entero si del otro lado (antes, al añadir; después, al quitar) no hay ninguno de ese pool.
            var other = verb == "quitado" ? _next : _running;
            if (!other.Any(name => IsPhpMember(name) && Pool(name) == pool.Key)) yield return $"{verb} {pool.Key}";
            else foreach (var member in pool) yield return $"{verb} {member.Name}";
        }
    }

    private HashSet<string> _running = [];
    private HashSet<string> _next = [];

    private static readonly Regex PhpMember = new(@"^php-\d+\.\d+-\d+$", RegexOptions.CultureInvariant);

    /// <summary>true si es un php-cgi de un pool (<c>php-8.4-0</c>).</summary>
    public static bool IsPhpMember(string name) => PhpMember.IsMatch(name);

    /// <summary>El pool de un php-cgi: <c>php-8.4-0</c> → <c>php-8.4</c>.</summary>
    public static string Pool(string member) => IsPhpMember(member) ? member[..member.LastIndexOf('-')] : member;
}
