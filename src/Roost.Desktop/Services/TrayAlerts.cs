using Roost.Daemon;

namespace Roost.Desktop.Services;

public enum TrayAlertKind
{
    /// <summary>Un servicio del stack ha pasado a Failed (J4).</summary>
    ServiceDown,

    /// <summary>La auditoría ha pasado a rojo (K5).</summary>
    AuditRed,
}

/// <summary>Un aviso de la bandeja: lo que dice y a dónde lleva al pulsarlo.</summary>
public sealed record TrayAlert(TrayAlertKind Kind, string Title, string Text);

/// <summary>
/// Cuándo avisa la bandeja (J4, K5): una vez por cambio, nunca en bucle.
/// <list type="bullet">
/// <item>Un servicio que pasa a <c>Failed</c> avisa una vez; si vuelve a estar en marcha y se cae
/// otra vez, otra. Los pools de PHP cuentan como uno (<c>php-8.4-0</c>, <c>-1</c>… son «PHP 8.4»).</item>
/// <item>La auditoría avisa si hay algo en rojo que no lo estaba en la anterior (que se guarda entre
/// sesiones: abrir la app con lo mismo en rojo no vuelve a avisar).</item>
/// </list>
/// Sin estado de Avalonia: los tests lo prueban directamente.
/// </summary>
public sealed class TrayAlerts
{
    private readonly HashSet<string> _failed = new(StringComparer.Ordinal);

    /// <summary>Con lo último que cuenta el daemon: el aviso de lo que se acaba de caer, o null.</summary>
    public TrayAlert? ServicesDown(StatusResponse? status)
    {
        if (status is null)
        {
            // Sin daemon no hay nada caído: si vuelve y algo falla, se avisa de nuevo.
            _failed.Clear();
            return null;
        }

        var failed = status.Services.Where(s => s.State == "Failed").ToList();
        _failed.IntersectWith(failed.Select(s => s.Name));
        var fresh = failed.Where(s => !_failed.Contains(s.Name)).ToList();
        if (fresh.Count == 0) return null;
        _failed.UnionWith(fresh.Select(s => s.Name));

        var names = fresh.Select(s => Display(s.Name)).Distinct().ToList();
        var title = names.Count == 1 ? $"Se ha caído {names[0]}" : $"Se han caído {string.Join(", ", names.Take(names.Count - 1))} y {names[^1]}";
        var error = fresh.Select(s => s.LastError).FirstOrDefault(e => !string.IsNullOrWhiteSpace(e));
        var text = error is null
            ? "Roost lo ha intentado varias veces y lo ha dejado parado. Mira su registro."
            : $"{Shorten(error)} Roost lo ha dejado parado: mira su registro.";
        return new TrayAlert(TrayAlertKind.ServiceDown, title, text);
    }

    /// <summary>El aviso de lo que ha pasado a rojo desde la auditoría anterior, o null.</summary>
    public static TrayAlert? AuditRed(AuditChanges? changes)
    {
        if (changes?.NewRed is not { Count: > 0 } red) return null;
        var first = red[0];
        var what = $"{KindName(first.Kind)} {first.Version} ({first.Source})";
        var title = red.Count == 1 ? $"La auditoría pasa a rojo: {what}" : $"La auditoría pasa a rojo: {red.Count} componentes";
        var text = red.Count == 1
            ? $"{first.Verdict}. Ábrelo en Auditoría para ver los avisos y cómo arreglarlo."
            : $"{what} y {red.Count - 1} más. Ábrelo en Auditoría para ver los avisos y cómo arreglarlo.";
        return new TrayAlert(TrayAlertKind.AuditRed, title, text);
    }

    /// <summary>«php-8.4-2» → «PHP 8.4»; «mariadb-11.4» → «MariaDB 11.4»; los demás, tal cual.</summary>
    public static string Display(string service)
    {
        if (service.StartsWith("php-", StringComparison.Ordinal))
        {
            var parts = service.Split('-');
            return parts.Length >= 2 ? $"PHP {parts[1]}" : "PHP";
        }
        if (service.StartsWith("mariadb-", StringComparison.Ordinal)) return "MariaDB " + service["mariadb-".Length..];
        if (service.StartsWith("mysql-", StringComparison.Ordinal)) return "MySQL " + service["mysql-".Length..];
        return service switch
        {
            "apache" => "Apache",
            "dns" => "el DNS local",
            "redis" => "Redis",
            "mailpit" => "Mailpit",
            _ => service,
        };
    }

    private static string KindName(string kind) => kind switch
    {
        "Php" => "PHP",
        "MySql" => "MySQL",
        "MariaDb" => "MariaDB",
        _ => kind,
    };

    // Un globo de Windows corta el texto a 255 caracteres.
    private static string Shorten(string text)
    {
        text = text.ReplaceLineEndings(" ").Trim();
        if (!text.EndsWith('.')) text += ".";
        return text.Length <= 160 ? text : text[..157] + "...";
    }
}
