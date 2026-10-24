using Roost.Core.Scanning;

namespace Roost.Core.Platform;

/// <summary>Cómo arranca un servicio de Windows (el <c>Start</c> de su clave en el registro).</summary>
public enum ServiceStartMode
{
    Automatic,
    Manual,
    Disabled,
    Other,
}

/// <summary>Un servicio de Windows de bases de datos o web que puede quitarle un puerto a Roost.</summary>
/// <param name="Kind">«MySQL», «MariaDB», «Apache», «nginx», «IIS» o «Redis».</param>
public sealed record WindowsService(string Name, string DisplayName, string Kind, string? Executable, ServiceStartMode Start, bool Running)
{
    /// <summary>Los puertos que suele usar: los de la base de datos o los del servidor web.</summary>
    public IReadOnlyList<int> UsualPorts => Kind switch
    {
        "MySQL" or "MariaDB" => [3306],
        "Redis" => [6379],
        _ => [80, 443],
    };

    /// <summary>Arranca solo al encender el equipo: aunque hoy esté parado, mañana tendrá el puerto.</summary>
    public bool StartsWithWindows => Start == ServiceStartMode.Automatic;
}

/// <summary>Un servicio de Windows que molesta, con los puertos del stack de Roost que suele usar.</summary>
public sealed record ServiceConflict(WindowsService Service, IReadOnlyList<int> Ports);

/// <summary>
/// Los servicios de Windows que se pelean con Roost por los puertos: un MySQL o una MariaDB
/// instalados con su MSI, un Apache como servicio, IIS (http.sys en el 80)… Con inicio automático
/// arrancan al encender el equipo y se quedan el puerto antes que Roost. Solo lee: el registro
/// (<c>HKLM\SYSTEM\CurrentControlSet\Services</c>) y <c>sc query</c> para saber si está en marcha.
/// </summary>
public static class WindowsServices
{
    // Por el ejecutable, no por el nombre del servicio: «GameInputRedistService» no es Redis.
    private static readonly IReadOnlyDictionary<string, string> KnownExecutables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["mysqld.exe"] = "MySQL",
        ["mysqld-nt.exe"] = "MySQL",
        ["mariadbd.exe"] = "MariaDB",
        ["httpd.exe"] = "Apache",
        ["nginx.exe"] = "nginx",
        ["redis-server.exe"] = "Redis",
        ["memurai.exe"] = "Redis",
    };

    /// <summary>Los que hay en esta máquina (vacío fuera de Windows o si no se puede leer el registro).</summary>
    public static IReadOnlyList<WindowsService> Find(IProcessRunner runner)
    {
        if (!OperatingSystem.IsWindows()) return [];
        var found = new List<WindowsService>();
        try
        {
            using var services = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            if (services is null) return [];
            foreach (var name in services.GetSubKeyNames())
            {
                using var key = services.OpenSubKey(name);
                if (key is null) continue;
                var exe = ExecutableOf(key.GetValue("ImagePath") as string);
                var kind = Classify(name, exe);
                if (kind is null) continue;
                var display = key.GetValue("DisplayName") as string ?? name;
                var start = StartModeOf(key.GetValue("Start"));
                var running = IsRunning(runner.Run("sc.exe", $"query \"{name}\"", TimeSpan.FromSeconds(5)));
                found.Add(new WindowsService(name, display.StartsWith('@') ? name : display, kind, exe, start, running));
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            // Sin permiso para leer alguna clave: se queda con lo que haya podido leer.
        }
        return found;
    }

    /// <summary>
    /// Los ejecutables de los servicios de bases de datos y servidores web (los de <see cref="Classify"/>),
    /// para el escaneo: un Apache instalado como servicio fuera de las rutas típicas. Solo lee el
    /// registro (sin <c>sc query</c>); vacío fuera de Windows o sin permiso.
    /// </summary>
    public static IReadOnlyList<string> Executables()
    {
        if (!OperatingSystem.IsWindows()) return [];
        var found = new List<string>();
        try
        {
            using var services = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            if (services is null) return [];
            foreach (var name in services.GetSubKeyNames())
            {
                try
                {
                    using var key = services.OpenSubKey(name);
                    if (ExecutableOf(key?.GetValue("ImagePath") as string) is { } exe && Classify(name, exe) is not null and not "IIS")
                        found.Add(Environment.ExpandEnvironmentVariables(exe));
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
                {
                    // Esa clave no se puede leer: las demás sí.
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            // Sin permiso para el registro: sin servicios.
        }
        return found;
    }

    /// <summary>
    /// PURO: los que molestan con estos puertos de Roost: arrancan con Windows o están en marcha,
    /// y suelen usar uno de ellos. Los deshabilitados no.
    /// </summary>
    public static IReadOnlyList<WindowsService> Conflicting(IEnumerable<WindowsService> services, IReadOnlyCollection<int> roostPorts) =>
        services
            .Where(s => s.Start != ServiceStartMode.Disabled && (s.StartsWithWindows || s.Running))
            .Where(s => s.UsualPorts.Any(roostPorts.Contains))
            .ToList();

    /// <summary>PURO: <see cref="Conflicting"/> con los puertos de Roost que cada uno suele usar.</summary>
    public static IReadOnlyList<ServiceConflict> Conflicts(IEnumerable<WindowsService> services, IReadOnlyCollection<int> roostPorts) =>
        Conflicting(services, roostPorts)
            .Select(s => new ServiceConflict(s, s.UsualPorts.Where(roostPorts.Contains).ToList()))
            .ToList();

    /// <summary>
    /// PURO: el servicio que tiene un puerto, por el proceso que escucha en él (<c>mysqld</c> es el
    /// servicio cuyo ejecutable es <c>mysqld.exe</c>; el 80 o el 443 de <c>System</c> es http.sys, o
    /// sea IIS si está en marcha). Solo los que están en marcha; null si no es ninguno.
    /// </summary>
    public static WindowsService? OwnerOf(PortOwner? owner, IEnumerable<WindowsService> services)
    {
        if (owner?.ProcessName is not { } process) return null;
        var running = services.Where(s => s.Running).ToList();
        if (process.Equals("System", StringComparison.OrdinalIgnoreCase))
            return owner.Port is 80 or 443 ? running.FirstOrDefault(s => s.Kind == "IIS") : null;
        return running.FirstOrDefault(s => s.Executable is { } exe
            && Path.GetFileNameWithoutExtension(exe).Equals(process, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>PURO: la orden que lo deja en Manual (hace falta una consola de administrador).</summary>
    public static string ManualCommand(WindowsService service) => $"sc config \"{service.Name}\" start= demand";

    /// <summary>PURO: la orden que lo para ahora (también como administrador).</summary>
    public static string StopCommand(WindowsService service) => $"sc stop \"{service.Name}\"";

    /// <summary>PURO: «MariaDB» (MariaDB 10.6): el nombre del servicio y, si es otro, el que enseña Windows.</summary>
    public static string Label(WindowsService service) =>
        service.DisplayName.Equals(service.Name, StringComparison.OrdinalIgnoreCase) ? $"\"{service.Name}\"" : $"\"{service.Name}\" ({service.DisplayName})";

    /// <summary>PURO: «arranca con Windows y esta en marcha», para los textos de la CLI y del daemon (sin tildes).</summary>
    public static string StateText(WindowsService service) => (service.StartsWithWindows, service.Running) switch
    {
        (true, true) => "arranca con Windows y esta en marcha",
        (true, false) => "arranca con Windows (ahora esta parado)",
        (false, true) => "esta en marcha",
        _ => "esta parado",
    };

    /// <summary>PURO: el tipo por el ejecutable (o IIS por su nombre); null si no es de los que molestan.</summary>
    public static string? Classify(string serviceName, string? executable)
    {
        if (serviceName.Equals("W3SVC", StringComparison.OrdinalIgnoreCase)) return "IIS";
        if (executable is null || !KnownExecutables.TryGetValue(Path.GetFileName(executable), out var kind)) return null;
        // Una MariaDB vieja instalada con su MSI arranca mysqld.exe: el nombre del servicio lo dice.
        return kind == "MySQL" && serviceName.Contains("maria", StringComparison.OrdinalIgnoreCase) ? "MariaDB" : kind;
    }

    /// <summary>PURO: el ejecutable de un <c>ImagePath</c> («"C:\x y\mysqld.exe" --defaults-file=…» → «C:\x y\mysqld.exe»).</summary>
    public static string? ExecutableOf(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath)) return null;
        var text = imagePath.Trim();
        if (text.StartsWith('"'))
        {
            var close = text.IndexOf('"', 1);
            return close > 1 ? text[1..close] : text.Trim('"');
        }
        var exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe < 0 ? text.Split(' ')[0] : text[..(exe + 4)];
    }

    /// <summary>PURO: el <c>Start</c> del registro (2 automático, 3 manual, 4 deshabilitado).</summary>
    public static ServiceStartMode StartModeOf(object? start) => start switch
    {
        2 => ServiceStartMode.Automatic,
        3 => ServiceStartMode.Manual,
        4 => ServiceStartMode.Disabled,
        _ => ServiceStartMode.Other,
    };

    /// <summary>PURO: si la salida de <c>sc query</c> dice que está en marcha («STATE : 4  RUNNING»).</summary>
    public static bool IsRunning(string? scQuery) =>
        scQuery is not null && scQuery.Split('\n').Any(l => l.Contains("STATE", StringComparison.Ordinal) && l.Contains("RUNNING", StringComparison.Ordinal));
}
