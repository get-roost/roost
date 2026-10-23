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
    /// PURO: los que molestan con estos puertos de Roost: arrancan con Windows o están en marcha,
    /// y suelen usar uno de ellos. Los deshabilitados no.
    /// </summary>
    public static IReadOnlyList<WindowsService> Conflicting(IEnumerable<WindowsService> services, IReadOnlyCollection<int> roostPorts) =>
        services
            .Where(s => s.Start != ServiceStartMode.Disabled && (s.StartsWithWindows || s.Running))
            .Where(s => s.UsualPorts.Any(roostPorts.Contains))
            .ToList();

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
