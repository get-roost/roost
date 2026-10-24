using Roost.Core.Config;
using Roost.Core.Install;
using Roost.Core.Platform;

namespace Roost.Core.Stack;

/// <summary>
/// El correo de pruebas con Mailpit: cómo se arranca y el fragmento de PHP que le manda el correo.
/// En Windows, <c>mail()</c> no usa <c>sendmail_path</c>: habla SMTP con <c>SMTP</c> y
/// <c>smtp_port</c> del php.ini. En Linux es al revés: <c>mail()</c> solo usa <c>sendmail_path</c>, y
/// Mailpit hace de sendmail (<c>mailpit sendmail --smtp-addr 127.0.0.1:&lt;smtp&gt;</c>, que lee el
/// mensaje por stdin y lo entrega por SMTP a sí mismo). Por eso Roost deja en el conf.d común (el que
/// leen el pool y la terminal por <c>PHP_INI_SCAN_DIR</c>) un <c>20-roost-mail.ini</c> con las dos cosas
/// mientras Mailpit va en el stack, y lo quita cuando sale. El <c>99-usuario.ini</c> se carga después y manda.
/// </summary>
public static class MailConf
{
    public const string PhpIniName = "20-roost-mail.ini";

    /// <summary>La dirección del remitente cuando el script no pone <c>From:</c>.</summary>
    public const string DefaultFrom = "roost@localhost";

    /// <summary>La bandeja: un SQLite en <c>data/mailpit</c>, que no se borra nunca.</summary>
    public static string Database(RoostPaths paths) => Path.Combine(paths.MailpitData, "mailpit.db");

    /// <summary>
    /// PURO: los argumentos de <c>mailpit.exe</c>. Todo en 127.0.0.1; la web solo atiende a los
    /// Host <c>localhost</c> y <c>127.0.0.1</c> (y <c>::1</c>), contra el DNS rebinding; acepta
    /// cualquier usuario y contraseña SMTP sin TLS, para que funcione igual un proyecto con
    /// <c>MAIL_USERNAME</c> puesto que uno sin él; y no pregunta a GitHub por versiones nuevas.
    /// </summary>
    public static string Arguments(int smtpPort, int webPort, string database) =>
        $"--smtp 127.0.0.1:{smtpPort} --listen 127.0.0.1:{webPort} --database \"{database}\" "
        + "--allowed-hosts localhost,127.0.0.1 --smtp-auth-accept-any --smtp-auth-allow-insecure --disable-version-check";

    /// <summary>
    /// PURO: el <c>sendmail_path</c> de Linux: el ejecutable de Mailpit haciendo de sendmail contra su
    /// propio SMTP. Es lo único que mira <c>mail()</c> fuera de Windows.
    /// </summary>
    public static string SendmailPath(string mailpitExecutable, int smtpPort) =>
        $"{mailpitExecutable} sendmail --smtp-addr 127.0.0.1:{smtpPort}";

    /// <summary>
    /// PURO: el <c>20-roost-mail.ini</c> que lleva el <c>mail()</c> de PHP a Mailpit. Con
    /// <paramref name="sendmail"/> (la orden de <see cref="SendmailPath"/>, en Linux) añade
    /// <c>sendmail_path</c>; sin él, solo el SMTP de Windows.
    /// </summary>
    public static string PhpIni(int smtpPort, int webPort, string? sendmail = null) => $"""
        ; Generado por Roost: el correo de PHP (mail()) va a Mailpit, que lo guarda en su bandeja
        ; (http://localhost:{webPort}/) y no lo manda a nadie. NO lo edites: Roost lo reescribe en cada
        ; `roost up` y lo quita al sacar Mailpit del stack. Lo tuyo va en 99-usuario.ini, que manda.
        [mail function]
        SMTP = 127.0.0.1
        smtp_port = {smtpPort}
        sendmail_from = "{DefaultFrom}"

        """ + (sendmail is null ? "" : $"sendmail_path = \"{sendmail}\"\n");

    /// <summary>
    /// Deja el fragmento como pide la configuración: escrito con Mailpit en el stack, quitado sin
    /// él. En Linux, <paramref name="mailpitExecutable"/> es el Mailpit instalado (el de
    /// <c>MailpitInstaller.Locate</c>), que pone el <c>sendmail_path</c>; sin él (Windows, o Mailpit
    /// aún sin instalar) no se pone nada de sendmail. true si ha cambiado algo.
    /// </summary>
    public static bool SyncPhpIni(string confD, RoostConfig config, string? mailpitExecutable = null)
    {
        var file = Path.Combine(confD, PhpIniName);
        if (!config.MailEnabled)
        {
            if (!File.Exists(file)) return false;
            File.Delete(file);
            return true;
        }

        var sendmail = !OperatingSystem.IsWindows() && mailpitExecutable is not null
            ? SendmailPath(mailpitExecutable, config.MailSmtpPort)
            : null;
        var text = PhpIni(config.MailSmtpPort, config.MailWebPort, sendmail);
        if (File.Exists(file) && File.ReadAllText(file) == text) return false;
        Directory.CreateDirectory(confD);
        File.WriteAllText(file, text);
        return true;
    }

    /// <summary>Lo mismo, buscando el Mailpit de Roost para el <c>sendmail_path</c> de Linux.</summary>
    public static bool SyncPhpIni(RoostPaths paths, string confD, RoostConfig config) =>
        SyncPhpIni(confD, config, OperatingSystem.IsWindows() ? null : new MailpitInstaller(paths).Locate()?.Executable);

    /// <summary>
    /// PURO: las variables de correo para el <c>.env</c> de Laravel. <c>MAIL_SCHEME</c> es de Laravel
    /// 11 en adelante y <c>MAIL_ENCRYPTION</c> de las anteriores; cada una ignora la de la otra.
    /// </summary>
    public static string EnvLines(int smtpPort) =>
        $"MAIL_MAILER=smtp\nMAIL_HOST=127.0.0.1\nMAIL_PORT={smtpPort}\nMAIL_USERNAME=null\nMAIL_PASSWORD=null\nMAIL_SCHEME=null\nMAIL_ENCRYPTION=null";
}
