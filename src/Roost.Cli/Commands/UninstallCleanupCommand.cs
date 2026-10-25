using System.ComponentModel;
using Microsoft.Win32;
using Roost.Cli.Commands.Sites;
using Roost.Core;
using Roost.Core.Config;
using Roost.Core.Dns;
using Roost.Core.Platform;
using Roost.Core.Share;
using Roost.Core.Shims;
using Roost.Core.Sites;
using Roost.Core.Updates;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Roost.Cli.Commands;

/// <summary>
/// <c>roost uninstall-cleanup</c> (oculto): lo lanza el desinstalador de Windows, ya elevado, antes
/// de borrar los ficheros; en Linux lo lanza el usuario antes de quitar el paquete (el postrm del
/// .deb no puede tocar las homes). Quita lo que Roost puso FUERA de su carpeta: la regla de DNS (la
/// NRPT, o el drop-in de resolved; sin ella, los .test de otras herramientas dejarian de resolver),
/// el bloque del hosts, los shims del PATH del usuario (en Linux, los enlaces de ~/.local/bin y el
/// bloque del perfil de la shell), las reglas «Roost LAN» del cortafuegos (roost share --lan), el
/// arranque con Windows (la app y la tarea que arranca el stack; en Linux, la unidad de systemd
/// --user o el .desktop del stack) y, salvo
/// <c>--keep-ca</c>, la CA de los certificados de confianza (en Linux, de los NSS de Chrome y Firefox
/// y de la del sistema si se puso) y lo de Firefox. Nunca toca los datos, los proyectos ni la
/// configuracion (salvo volver a <c>[sites] resolver = "hosts"</c> si quita la regla). Cada paso va
/// por su cuenta: si uno falla, se apunta y se sigue; siempre sale con 0, para no dejar el
/// desinstalador a medias.
/// </summary>
public sealed class UninstallCleanupCommand : Command<UninstallCleanupCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--keep-ca")]
        [Description("No quita la CA de los certificados de confianza (quitarla ensena un aviso de Windows: en una desinstalacion silenciosa se deja).")]
        public bool KeepCa { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var windows = OperatingSystem.IsWindows();
        var paths = RoostPaths.Default();
        var log = new List<string>();

        Step(log, "DNS local", () =>
        {
            if (!LocalDns.IsRuleInstalled()) return windows ? "no habia regla NRPT de Roost" : "no habia drop-in de resolved de Roost";
            LocalDns.RemoveRule(SiteSupport.HelperExe);
            if (File.Exists(paths.ConfigFile) && RoostConfig.Load(paths.ConfigFile).UsesLocalDns)
                RoostConfig.SetResolver(paths.ConfigFile, RoostConfig.ResolverHosts);
            return (windows ? "regla NRPT quitada" : "drop-in de resolved quitado") + "; [sites] resolver vuelve a hosts";
        });

        Step(log, "hosts", () =>
        {
            var hosts = new HostsUpdater();
            if (hosts.Current().Count == 0) return "sin bloque de Roost";
            hosts.Sync([], SiteSupport.HelperExe, Path.Combine(paths.Run, "hosts-request.txt"));
            return $"bloque # roost:begin/end quitado de {hosts.HostsFile}";
        });

        Step(log, "shims", () =>
        {
            var shims = new ShimInstaller(paths);
            if (!windows)
            {
                // Los enlaces de ~/.local/bin que apuntan a los shims de Roost y el bloque del perfil de la shell.
                var unlinked = shims.Links.Unlink(shims.Directory, ShimInstaller.Tools);
                var profiles = shims.Links.RemoveProfile();
                shims.Remove();
                return (unlinked.Count == 0 ? "sin enlaces en " + shims.Links.LinkDirectory : $"enlaces quitados de {shims.Links.LinkDirectory}: {string.Join(", ", unlinked)}")
                       + (profiles.Count == 0 ? "; sin bloque en el perfil" : $"; bloque quitado de {string.Join(", ", profiles)}");
            }
            var inPath = UserPath.Remove(shims.Directory);
            shims.Remove();
            return inPath ? $"{shims.Directory} quitado del PATH del usuario" : "no estaban en el PATH";
        });

        Step(log, "inicio con Windows", () =>
        {
            if (!OperatingSystem.IsWindows()) return "no aplica";   // para el analizador: la lambda no ve la guarda de arriba
            using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (run?.GetValue("Roost") is not string value || !value.Contains("roost-desktop.exe", StringComparison.OrdinalIgnoreCase))
                return "no estaba";
            run.DeleteValue("Roost", throwOnMissingValue: false);
            return "quitado de HKCU\\...\\Run";
        });

        // Compartir (oleada 6): las reglas "Roost LAN <puerto>" de roost share --lan que hayan quedado
        // (en Linux, las que Roost apunto al abrirlas: ufw no se puede leer sin root).
        Step(log, "cortafuegos", () =>
        {
            var ports = LanFirewall.RulePortsAsync(Roost.Core.Scanning.CommandRunner.Default, cancellation, paths).GetAwaiter().GetResult();
            if (ports.Count == 0) return "no habia reglas de Roost";
            foreach (var port in ports) LanFirewall.Remove(SiteSupport.HelperExe, port, paths);
            return $"quitada(s) {string.Join(", ", ports.Select(LanFirewall.RuleName))}";
        });

        // E4: la tarea (o la unidad de systemd --user, o el .desktop) que arranca el stack al iniciar
        // sesion lanzaria un roost que ya no esta.
        Step(log, "tarea al iniciar sesion", () =>
        {
            if (!windows)
            {
                if (!OperatingSystem.IsLinux()) return "no aplica";
                var unit = new UserUnit(Roost.Core.Scanning.CommandRunner.Default);
                var status = unit.Status();
                if (!status.Exists) return "no estaba";
                unit.DisableAsync().GetAwaiter().GetResult();
                return status.Kind == AutostartKind.SystemdUser ? $"quitada la unidad {UserUnit.UnitName} ({status.File})" : $"quitado el .desktop ({status.File})";
            }
            var task = new LogonTask(Roost.Core.Scanning.CommandRunner.Default);
            if (!task.StatusAsync().GetAwaiter().GetResult().Exists) return "no estaba";
            task.DisableAsync().GetAwaiter().GetResult();
            return $"quitada ({LogonTask.TaskName})";
        });

        if (settings.KeepCa)
        {
            log.Add(windows
                ? "CA: se deja (--keep-ca). Para quitarla: certmgr.msc, Entidades de certificacion raiz de confianza, «Roost Local CA (...)», o roost cert untrust antes de desinstalar."
                : "CA: se deja (--keep-ca). Para quitarla: roost cert untrust antes de desinstalar (o certutil -D -n 'Roost Local CA' en cada almacen NSS).");
        }
        else
        {
            Step(log, "CA", () =>
            {
                var ca = new LocalCa(paths);
                if (!ca.IsTrusted()) return $"no estaba en {ca.Store.Description}";
                var report = ca.Untrust();
                return ca.IsTrusted() ? "sigue: se rechazo el aviso de Windows" : $"quitada de {report.Summary}";
            });
            if (!windows && !Wsl.IsWsl)
            {
                Step(log, "CA del sistema", () =>
                {
                    var ca = new LocalCa(paths);
                    if (!ca.IsTrustedBySystem()) return "no estaba";
                    ca.UntrustSystem(SiteSupport.HelperExe);
                    return $"quitada ({LocalCa.SystemCaFile()})";
                });
            }
            Step(log, "Firefox", () =>
            {
                if (!windows) return "en Linux va con la CA (certutil en cada perfil)";
                var profiles = FirefoxTrust.AllProfiles().Where(FirefoxTrust.IsEnabled).ToList();
                foreach (var profile in profiles) FirefoxTrust.Disable(profile);
                return profiles.Count == 0 ? "nada que quitar" : $"quitado de {profiles.Count} perfil(es)";
            });
        }

        foreach (var line in log) AnsiConsole.WriteLine(line);
        try
        {
            Directory.CreateDirectory(paths.Logs);
            File.AppendAllLines(Path.Combine(paths.Logs, "uninstall.log"),
                [$"=== {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} roost uninstall-cleanup (Roost {RoostVersion.Current})", .. log]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Sin log: lo de arriba ya ha salido por la consola.
        }
        return 0;
    }

    private static void Step(List<string> log, string what, Func<string> action)
    {
        try
        {
            log.Add($"{what}: {action()}");
        }
        catch (Exception ex) when (ex is RoostException or IOException or UnauthorizedAccessException or System.Security.SecurityException
                                       or System.Security.Cryptography.CryptographicException or System.ComponentModel.Win32Exception)
        {
            log.Add($"{what}: FALLO - {ex.Message}");
        }
    }
}
