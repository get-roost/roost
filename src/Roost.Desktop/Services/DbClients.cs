using System.Diagnostics;
using Microsoft.Win32;
using Roost.Core;
using Roost.Core.Install;

namespace Roost.Desktop.Services;

/// <summary>Los clientes gráficos que Roost sabe abrir.</summary>
public enum DbClientKind
{
    HeidiSql,
    TablePlus,
    DBeaver,
}

/// <summary>Un cliente gráfico instalado en el equipo.</summary>
public sealed record DbClient(DbClientKind Kind, string Name, string Exe)
{
    /// <summary>true si se le puede pasar la conexión por argumentos; si no, se abre y Roost copia la URL.</summary>
    public bool AcceptsConnection => Kind is DbClientKind.HeidiSql or DbClientKind.DBeaver;

    public string ButtonLabel => $"Abrir en {Name}";

    public string Tip => AcceptsConnection
        ? $"Abre {Name} ya conectado a la base de datos de Roost"
        : $"Abre {Name} y copia la URL de conexion al portapapeles: pégala en Nueva conexión › desde URL";
}

/// <summary>Los datos de una conexión a la base de datos de Roost.</summary>
public sealed record DbConnection(DbEngine Engine, string Host, int Port, string User, string Password, string? Database, string Label)
{
    /// <summary>La URL <c>mysql://usuario:contraseña@host:puerto/bd</c> que entienden TablePlus y compañía.</summary>
    public string Url =>
        $"mysql://{Uri.EscapeDataString(User)}{(Password.Length == 0 ? "" : ":" + Uri.EscapeDataString(Password))}@{Host}:{Port}/{Database ?? ""}";
}

/// <summary>
/// Detecta HeidiSQL, TablePlus y DBeaver (rutas típicas y el registro de Windows, solo lectura)
/// y los abre con la conexión de Roost. Si no hay ninguno, la pantalla no enseña nada.
/// </summary>
public static class DbClients
{
    /// <summary>Los clientes instalados, en orden de preferencia (HeidiSQL es el que trae Laragon).</summary>
    public static IReadOnlyList<DbClient> Detect()
    {
        if (!OperatingSystem.IsWindows()) return [];
        var found = new List<DbClient>();
        if (Find(DbClientKind.HeidiSql, "heidisql.exe",
                ["HeidiSQL_is1"],
                [@"%ProgramFiles%\HeidiSQL", @"%ProgramFiles(x86)%\HeidiSQL", @"%LocalAppData%\Programs\HeidiSQL", @"C:\laragon\bin\heidisql"]) is { } heidi)
            found.Add(new DbClient(DbClientKind.HeidiSql, "HeidiSQL", heidi));
        if (Find(DbClientKind.TablePlus, "TablePlus.exe",
                ["TablePlus", "TablePlus_is1"],
                [@"%LocalAppData%\TablePlus", @"%LocalAppData%\Programs\TablePlus", @"%ProgramFiles%\TablePlus"]) is { } tablePlus)
            found.Add(new DbClient(DbClientKind.TablePlus, "TablePlus", tablePlus));
        if (Find(DbClientKind.DBeaver, "dbeaver.exe",
                ["DBeaver", "DBeaver Community", "DBeaverCE"],
                [@"%ProgramFiles%\DBeaver", @"%LocalAppData%\DBeaver", @"%LocalAppData%\Programs\DBeaver"]) is { } dbeaver)
            found.Add(new DbClient(DbClientKind.DBeaver, "DBeaver", dbeaver));
        return found;
    }

    /// <summary>
    /// Abre el cliente con la conexión. HeidiSQL acepta <c>-h -u -p -P</c> (con espacio: vale en la
    /// 12 y en la 13); DBeaver, <c>-con "driver=…|host=…"</c>. TablePlus en Windows no documenta
    /// argumentos: se abre y la URL queda en el portapapeles (lo hace quien llama, con
    /// <see cref="DbConnection.Url"/>).
    /// </summary>
    public static void Open(DbClient client, DbConnection connection)
    {
        var start = new ProcessStartInfo(client.Exe)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(client.Exe) ?? "",
        };
        switch (client.Kind)
        {
            case DbClientKind.HeidiSql:
                start.ArgumentList.Add("-n"); start.ArgumentList.Add("0");
                start.ArgumentList.Add("-h"); start.ArgumentList.Add(connection.Host);
                start.ArgumentList.Add("-P"); start.ArgumentList.Add(connection.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
                start.ArgumentList.Add("-u"); start.ArgumentList.Add(connection.User);
                if (connection.Password.Length > 0) { start.ArgumentList.Add("-p"); start.ArgumentList.Add(connection.Password); }
                if (connection.Database is not null) { start.ArgumentList.Add("-db"); start.ArgumentList.Add(connection.Database); }
                break;
            case DbClientKind.DBeaver:
                // Una conexión temporal (save=false): cada clic abre la de Roost sin ir acumulando copias.
                var parts = new List<string>
                {
                    $"driver={(connection.Engine == DbEngine.MariaDb ? "mariadb" : "mysql")}",
                    $"host={connection.Host}", $"port={connection.Port}", $"user={connection.User}",
                    $"name={connection.Label}", "savePassword=true", "connect=true", "save=false",
                };
                if (connection.Password.Length > 0) parts.Add($"password={connection.Password}");
                if (connection.Database is not null) parts.Add($"database={connection.Database}");
                start.ArgumentList.Add("-con");
                start.ArgumentList.Add(string.Join("|", parts));
                break;
        }

        try
        {
            Process.Start(start)?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            throw new RoostException($"No se pudo abrir {client.Name} ({client.Exe}): {ex.Message}");
        }
    }

    /// <summary>El ejecutable: primero las rutas típicas, luego el registro (desinstalación y App Paths).</summary>
    private static string? Find(DbClientKind kind, string exe, string[] uninstallKeys, string[] folders)
    {
        foreach (var folder in folders)
        {
            var candidate = Path.Combine(Environment.ExpandEnvironmentVariables(folder), exe);
            if (File.Exists(candidate)) return candidate;
        }
        if (!OperatingSystem.IsWindows()) return null;

        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            foreach (var root in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
            {
                foreach (var key in uninstallKeys)
                {
                    if (FromUninstallKey(hive, $@"{root}\{key}", exe) is { } found) return found;
                }
            }
            if (ReadString(hive, $@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exe}", null) is { } appPath && File.Exists(appPath))
                return appPath;
        }

        // Instaladores que registran la clave con otro nombre: se busca por DisplayName, solo lectura.
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            foreach (var root in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
            {
                try
                {
                    using var uninstall = hive.OpenSubKey(root);
                    if (uninstall is null) continue;
                    foreach (var name in uninstall.GetSubKeyNames())
                    {
                        var display = ReadString(hive, $@"{root}\{name}", "DisplayName") ?? "";
                        if (!display.Contains(kind.ToString(), StringComparison.OrdinalIgnoreCase)) continue;
                        if (FromUninstallKey(hive, $@"{root}\{name}", exe) is { } found) return found;
                    }
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
                {
                }
            }
        }
        return null;
    }

    private static string? FromUninstallKey(RegistryKey hive, string key, string exe)
    {
        if (ReadString(hive, key, "InstallLocation") is { Length: > 0 } location)
        {
            var candidate = Path.Combine(location.Trim('"'), exe);
            if (File.Exists(candidate)) return candidate;
        }
        if (ReadString(hive, key, "DisplayIcon") is { Length: > 0 } icon)
        {
            var path = icon.Split(',')[0].Trim('"');
            if (Path.GetFileName(path).Equals(exe, StringComparison.OrdinalIgnoreCase) && File.Exists(path)) return path;
            var sibling = Path.Combine(Path.GetDirectoryName(path) ?? "", exe);
            if (File.Exists(sibling)) return sibling;
        }
        return null;
    }

    private static string? ReadString(RegistryKey hive, string key, string? value)
    {
        try
        {
            using var sub = hive.OpenSubKey(key);
            return sub?.GetValue(value) as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
