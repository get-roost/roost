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
/// <c>roost uninstall-cleanup</c> (oculto): lo lanza el desinstalador, ya elevado, antes de borrar
/// los ficheros. Quita lo que Roost puso FUERA de su carpeta: la regla NRPT (sin ella, los .test de
/// otras herramientas dejarian de resolver), el bloque del hosts, los shims del PATH del usuario,
/// las reglas «Roost LAN» del cortafuegos (roost share --lan),
/// el arranque con Windows (la app y la tarea que arranca el stack) y, salvo <c>--keep-ca</c>, la CA de los certificados de confianza y lo
/// de Firefox. Nunca toca los datos, los proyectos ni la configuracion (salvo volver a
/// <c>[sites] resolver = "hosts"</c> si quita la regla). Cada paso va por su cuenta: si uno falla,
/// se apunta y se sigue; siempre sale con 0, para no dejar el desinstalador a medias.
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
        if (!OperatingSystem.IsWindows()) return 0;
        var paths = RoostPaths.Default();
        var log = new List<string>();

        Step(log, "DNS local", () =>
        {
            if (!LocalDns.IsRuleInstalled()) return "no habia regla NRPT de Roost";
            LocalDns.RemoveRule(SiteSupport.HelperExe);
            if (File.Exists(paths.ConfigFile) && RoostConfig.Load(paths.ConfigFile).UsesLocalDns)
                RoostConfig.SetResolver(paths.ConfigFile, RoostConfig.ResolverHosts);
            return "regla NRPT quitada; [sites] resolver vuelve a hosts";
        });

        Step(log, "hosts", () =>
        {
            var hosts = new HostsUpdater();
            if (hosts.Current().Count == 0) return "sin bloque de Roost";
            hosts.Sync([], SiteSupport.HelperExe, Path.Combine(paths.Run, "hosts-request.txt"));
            return "bloque # roost:begin/end quitado";
        });

        Step(log, "shims", () =>
        {
            var shims = new ShimInstaller(paths);
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

        // Compartir (oleada 6): las reglas "Roost LAN <puerto>" de roost share --lan que hayan quedado.
        Step(log, "cortafuegos", () =>
        {
            var ports = LanFirewall.RulePortsAsync(Roost.Core.Scanning.CommandRunner.Default).GetAwaiter().GetResult();
            if (ports.Count == 0) return "no habia reglas de Roost";
            foreach (var port in ports) LanFirewall.Remove(SiteSupport.HelperExe, port);
            return $"quitada(s) {string.Join(", ", ports.Select(LanFirewall.RuleName))}";
        });

        // E4: la tarea que arranca el stack al iniciar sesion lanzaria un roost.exe que ya no esta.
        Step(log, "tarea al iniciar sesion", () =>
        {
            var task = new LogonTask(Roost.Core.Scanning.CommandRunner.Default);
            if (!task.StatusAsync().GetAwaiter().GetResult().Exists) return "no estaba";
            task.DisableAsync().GetAwaiter().GetResult();
            return $"quitada ({LogonTask.TaskName})";
        });

        if (settings.KeepCa)
        {
            log.Add("CA: se deja (--keep-ca). Para quitarla: certmgr.msc, Entidades de certificacion raiz de confianza, «Roost Local CA (...)», o roost cert untrust antes de desinstalar.");
        }
        else
        {
            Step(log, "CA", () =>
            {
                var ca = new LocalCa(paths);
                if (!ca.IsTrusted()) return "no estaba en los certificados de confianza";
                ca.Untrust();
                return ca.IsTrusted() ? "sigue: se rechazo el aviso de Windows" : "quitada de los certificados de confianza del usuario";
            });
            Step(log, "Firefox", () =>
            {
                var profiles = FirefoxTrust.Profiles(FirefoxTrust.DefaultRoot()).Where(FirefoxTrust.IsEnabled).ToList();
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
