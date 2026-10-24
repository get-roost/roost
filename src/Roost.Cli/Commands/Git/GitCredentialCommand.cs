using System.ComponentModel;
using System.Text;
using Roost.Core.Git;
using Spectre.Console.Cli;

namespace Roost.Cli.Commands.Git;

/// <summary>
/// <c>roost git-credential &lt;get|store|erase&gt;</c> (oculto): el helper de credenciales que git
/// lanza por su cuenta cuando un servidor https pide acceso (Roost se lo pasa solo en cada
/// invocacion, por GIT_CONFIG_COUNT). Habla el protocolo de git por stdin y stdout y no escribe nada
/// mas: ni un aviso, ni el token en ningun sitio que no sea esa tuberia.
/// </summary>
public sealed class GitCredentialCommand : Command<GitCredentialCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ACCION>")]
        [Description("get, store o erase (lo elige git).")]
        public string Action { get; init; } = "";
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellation) => Run(settings.Action);

    /// <summary>Lo que hace el comando; Program lo llama directamente, antes de cargar extensiones (que no corra nada mas con el token).</summary>
    public static int Run(string action)
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        using var input = new StreamReader(Console.OpenStandardInput(), utf8);
        using var output = new StreamWriter(Console.OpenStandardOutput(), utf8) { NewLine = "\n", AutoFlush = false };
        try
        {
            ICredentialStore store = OperatingSystem.IsWindows() ? new WindowsCredentialStore() : NoCredentialStore.Instance;
            var code = GitCredentialProtocol.Run(action, input, output, new GitCredentials(store));
            output.Flush();
            return code;
        }
        catch (Exception ex)
        {
            // git enseña el stderr del helper: el motivo, nunca nada de la peticion.
            Console.Error.WriteLine($"roost git-credential: {ex.Message}");
            return 1;
        }
    }
}
