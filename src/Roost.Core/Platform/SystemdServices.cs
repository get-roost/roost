using System.Text.RegularExpressions;
using Roost.Core.Scanning;

namespace Roost.Core.Platform;

/// <summary>
/// Linux: las unidades de systemd de bases de datos y servidores web que le pueden quitar un puerto a
/// Roost: el <c>apache2</c> que se habilita solo al instalarlo con apt, <c>nginx</c>, <c>mysql</c>,
/// <c>mariadb</c>, <c>redis-server</c> y los <c>php*-fpm</c> de la distro. Solo lee, con
/// <c>systemctl list-units</c> (qué hay y qué está en marcha) y <c>systemctl is-enabled</c> (qué arranca
/// con el sistema). Sin systemd (WSL sin él, un contenedor) <c>systemctl</c> no contesta nada útil y no
/// hay servicios. Reutiliza <see cref="WindowsService"/> como forma común: <c>Name</c> es la unidad
/// (<c>apache2.service</c>), <c>DisplayName</c> su descripción y <c>Start</c> si está <c>enabled</c>.
/// </summary>
public sealed class SystemdServices : ISystemServices
{
    /// <summary>Una línea de <c>systemctl list-units --type=service --all --plain --no-legend</c>.</summary>
    public sealed record Unit(string Name, string Load, string Active, string Sub, string Description)
    {
        public bool Loaded => Load == "loaded";
        public bool Running => Active == "active" && Sub == "running";
    }

    private static readonly Regex PhpFpm = new(@"^php[0-9.]*-fpm$", RegexOptions.CultureInvariant);

    private readonly bool _enabled;

    public SystemdServices() : this(OperatingSystem.IsLinux()) { }

    /// <summary>Con <paramref name="enabled"/> a true pregunta a <c>systemctl</c> aunque esto no sea Linux (los tests, con un runner de pega).</summary>
    internal SystemdServices(bool enabled)
    {
        _enabled = enabled;
    }

    public IReadOnlyList<WindowsService> Find(IProcessRunner runner)
    {
        if (!_enabled) return [];
        var listed = runner.Run("systemctl", "list-units --type=service --all --plain --no-legend --no-pager", TimeSpan.FromSeconds(10));
        if (listed is null) return [];

        var found = new List<WindowsService>();
        foreach (var unit in ParseListUnits(listed))
        {
            if (!unit.Loaded || Classify(unit.Name) is not { } kind) continue;
            var enabled = runner.Run("systemctl", $"is-enabled {unit.Name}", TimeSpan.FromSeconds(5));
            found.Add(new WindowsService(unit.Name, unit.Description, kind, null, StartModeOf(enabled), unit.Running));
        }
        return found;
    }

    /// <summary>
    /// PURO: las unidades de la salida de <c>systemctl list-units --type=service --all --plain --no-legend</c>
    /// (<c>nombre.service  load  active  sub  descripcion…</c>). Lo que no tiene esa forma (el aviso de
    /// «System has not been booted with systemd», una línea vacía) se salta.
    /// </summary>
    public static IReadOnlyList<Unit> ParseListUnits(string text)
    {
        var units = new List<Unit>();
        foreach (var raw in text.Split('\n'))
        {
            var fields = raw.Trim().TrimStart('●', '*').Trim().Split(' ', 5, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 4 || !fields[0].EndsWith(".service", StringComparison.Ordinal)) continue;
            units.Add(new Unit(fields[0], fields[1], fields[2], fields[3], fields.Length > 4 ? fields[4].Trim() : fields[0]));
        }
        return units;
    }

    /// <summary>PURO: el tipo por el nombre de la unidad («apache2.service» → «Apache»); null si no es de los que molestan.</summary>
    public static string? Classify(string unitName)
    {
        var name = unitName.EndsWith(".service", StringComparison.Ordinal) ? unitName[..^".service".Length] : unitName;
        if (PhpFpm.IsMatch(name)) return "PHP-FPM";
        return name switch
        {
            "apache2" or "httpd" => "Apache",
            "nginx" => "nginx",
            "mysql" or "mysqld" => "MySQL",
            "mariadb" => "MariaDB",
            "redis-server" or "redis" or "valkey" or "valkey-server" => "Redis",
            _ => null,
        };
    }

    /// <summary>
    /// PURO: lo que dice <c>systemctl is-enabled</c>: <c>enabled</c> (arranca con el sistema), <c>disabled</c>
    /// (manual), <c>masked</c> (no se puede arrancar) y lo demás (<c>static</c>, <c>indirect</c>…), que no cuenta.
    /// </summary>
    public static ServiceStartMode StartModeOf(string? isEnabled) => isEnabled?.Trim().Split('\n')[0].Trim() switch
    {
        "enabled" or "enabled-runtime" or "alias" => ServiceStartMode.Automatic,
        "disabled" => ServiceStartMode.Manual,
        "masked" or "masked-runtime" => ServiceStartMode.Disabled,
        _ => ServiceStartMode.Other,
    };

    /// <summary>La unidad sin <c>.service</c>, para las órdenes de systemctl.</summary>
    public static string UnitName(WindowsService service) =>
        service.Name.EndsWith(".service", StringComparison.Ordinal) ? service.Name[..^".service".Length] : service.Name;

    /// <summary>PURO: «"apache2.service" (The Apache HTTP Server)».</summary>
    public static string Label(WindowsService service) =>
        service.DisplayName.Equals(service.Name, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(service.DisplayName)
            ? $"\"{service.Name}\""
            : $"\"{service.Name}\" ({service.DisplayName})";

    /// <summary>PURO: «arranca con el sistema y esta en marcha», para la CLI y el daemon (sin tildes).</summary>
    public static string StateText(WindowsService service) => (service.StartsWithWindows, service.Running) switch
    {
        (true, true) => "arranca con el sistema y esta en marcha",
        (true, false) => "arranca con el sistema (ahora esta parado)",
        (false, true) => "esta en marcha",
        _ => "esta parado",
    };

    /// <summary>PURO: la orden que lo para ahora y lo quita del arranque (hace falta sudo).</summary>
    public static string DisableCommand(WindowsService service) => $"sudo systemctl disable --now {UnitName(service)}";

    /// <summary>
    /// PURO: el servicio que tiene un puerto. Con el proceso a la vista (es nuestro), por su nombre
    /// (<c>mysqld</c>, <c>apache2</c>…); si el dueño es de otro usuario (lo normal con un servicio del
    /// sistema: sin root no se ve), el servicio en marcha que suele usar ese puerto. Solo los que están en marcha.
    /// </summary>
    public WindowsService? OwnerOf(PortOwner? owner, IEnumerable<WindowsService> services)
    {
        if (owner is null) return null;
        var running = services.Where(s => s.Running).ToList();
        if (owner.IsForeign || owner.ProcessName is null)
            return running.FirstOrDefault(s => s.UsualPorts.Contains(owner.Port));
        return running.FirstOrDefault(s => ProcessNames(s.Kind).Any(n => owner.ProcessName.StartsWith(n, StringComparison.Ordinal)));
    }

    private static string[] ProcessNames(string kind) => kind switch
    {
        "Apache" => ["apache2", "httpd"],
        "nginx" => ["nginx"],
        "MySQL" => ["mysqld"],
        "MariaDB" => ["mariadbd", "mysqld"],
        "Redis" => ["redis-server", "valkey-server"],
        "PHP-FPM" => ["php-fpm"],
        _ => [],
    };

    public string Describe(WindowsService service) => $"el servicio del sistema {Label(service)} {StateText(service)}";

    public string HowToFree(WindowsService service) => $"Quitalo del arranque y paralo: {DisableCommand(service)}";

    public string Disclaimer => "Roost no toca los servicios del sistema.";

    public string PortHint(PortOwner owner, WindowsService service) => owner.IsForeign
        ? $"El puerto {owner.Port} lo tiene un proceso de otro usuario (en Linux solo se ven los propios), seguramente el servicio del sistema {Label(service)}, que {StateText(service)}. "
          + $"Para que no se lo quede, quitalo del arranque y paralo: {DisableCommand(service)} (o miralo con: sudo ss -ltnp)."
        : $"El puerto {owner.Port} lo tiene el servicio del sistema {Label(service)}, que {StateText(service)}. "
          + $"Para que no se lo quede, quitalo del arranque y paralo: {DisableCommand(service)}";
}
