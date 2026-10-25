using System.Text.Json;
using System.Text.Json.Serialization;
using Roost.Core.Platform;
using Roost.Core.Scanning;

namespace Roost.Core.Git;

/// <summary>
/// Qué almacén de tokens toca en este sistema: en Windows el Administrador de credenciales
/// (<see cref="WindowsCredentialStore"/>); en Linux, el Secret Service de la sesión por <c>secret-tool</c>
/// (<see cref="SecretServiceCredentialStore"/>: el llavero de GNOME o KWallet, cifrado) si hay uno vivo, y
/// si no, un fichero solo legible por el usuario (<see cref="FileCredentialStore"/>, <c>state/git/credentials.json</c>,
/// 0600) que no va cifrado, y se avisa. En macOS, de momento ninguno.
/// </summary>
public static class CredentialStores
{
    /// <summary>El de este sistema (ver la clase).</summary>
    public static ICredentialStore Default(RoostPaths paths, ICommandRunner? runner = null)
    {
        if (OperatingSystem.IsWindows()) return new WindowsCredentialStore();
        if (!OperatingSystem.IsLinux()) return NoCredentialStore.Instance;
        var file = new FileCredentialStore(paths);
        var secretTool = SecretServiceCredentialStore.Locate();
        if (secretTool is not null && SecretServiceCredentialStore.IsAlive(runner ?? CommandRunner.Default, secretTool))
            return new SecretServiceCredentialStore(runner ?? CommandRunner.Default, secretTool, fallback: file);
        return file;
    }

    /// <summary>Dónde se guardan, para decirlo al guardar («en el Administrador de credenciales de Windows, cifrado para tu usuario»).</summary>
    public static string Describe(ICredentialStore store) => store switch
    {
        WindowsCredentialStore => "en el Administrador de credenciales de Windows, cifrado para tu usuario",
        SecretServiceCredentialStore => "en el llavero de tu sesion (Secret Service, por secret-tool), cifrado",
        FileCredentialStore f => $"en {f.File} (solo lo puede leer tu usuario, 0600; OJO: sin cifrar, porque no hay un llavero en esta sesion)",
        _ => "en ningun sitio",
    };

    /// <summary>Lo corto, para una tabla o un estado: «el Administrador de credenciales de Windows», «el llavero de la sesion», «state/git/credentials.json (sin cifrar)».</summary>
    public static string Label(ICredentialStore store) => store switch
    {
        WindowsCredentialStore => "el Administrador de credenciales de Windows",
        SecretServiceCredentialStore => "el llavero de la sesion (Secret Service)",
        FileCredentialStore f => $"{f.File} (0600, sin cifrar)",
        _ => "ningun almacen",
    };

    /// <summary>true si lo que se guarde no va cifrado (el fichero de Linux): para avisar.</summary>
    public static bool IsUnencrypted(ICredentialStore store) => store is FileCredentialStore;
}

/// <summary>
/// Los tokens en un fichero JSON solo legible por el usuario (<c>state/git/credentials.json</c>, 0600,
/// escrito con <see cref="PrivateFile"/>: nace ya con ese modo). Es lo mismo que hace
/// <c>git credential-store</c>, y como él, no cifra: la protección es el permiso del fichero. Se usa en
/// Linux cuando no hay un Secret Service en la sesión (una terminal por ssh, un servidor sin
/// escritorio, WSL). Lo que se escribe se lee entero y se vuelve a escribir entero, de forma atómica.
/// </summary>
public sealed class FileCredentialStore(RoostPaths paths) : ICredentialStore
{
    public string File { get; } = Path.Combine(paths.GitState, "credentials.json");

    public StoredCredential? Read(string target) =>
        Load().FirstOrDefault(c => c.Target == target) is { } found ? new StoredCredential(found.Target, found.User, found.Secret) : null;

    public void Write(StoredCredential credential)
    {
        var all = Load().Where(c => c.Target != credential.Target).ToList();
        all.Add(new Entry(credential.Target, credential.UserName, credential.Secret, DateTimeOffset.UtcNow));
        Save(all);
    }

    public bool Delete(string target)
    {
        var all = Load();
        var kept = all.Where(c => c.Target != target).ToList();
        if (kept.Count == all.Count) return false;
        Save(kept);
        return true;
    }

    public IReadOnlyList<CredentialInfo> List(string prefix) =>
        Load().Where(c => c.Target.StartsWith(prefix, StringComparison.Ordinal))
            .Select(c => new CredentialInfo(c.Target, c.User, c.Written))
            .OrderBy(c => c.Target, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private List<Entry> Load()
    {
        try
        {
            if (!System.IO.File.Exists(File)) return [];
            var text = System.IO.File.ReadAllText(File);
            return JsonSerializer.Deserialize(text, CredentialFileJson.Default.ListEntry) ?? [];
        }
        catch (JsonException ex)
        {
            throw new RoostException($"No se entiende {File} (los tokens de git): {ex.Message}. Arreglalo o borralo (se perderian los tokens).");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new RoostException($"No se pudo leer {File}: {ex.Message}");
        }
    }

    private void Save(List<Entry> entries)
    {
        try
        {
            PrivateFile.EnsureDirectory(Path.GetDirectoryName(File)!);
            if (entries.Count == 0)
            {
                if (System.IO.File.Exists(File)) System.IO.File.Delete(File);
                return;
            }
            var temp = $"{File}.{Guid.NewGuid():N}.tmp";
            PrivateFile.Write(temp, JsonSerializer.Serialize(entries, CredentialFileJson.Default.ListEntry) + "\n");
            System.IO.File.Move(temp, File, overwrite: true);
            PrivateFile.Restrict(File);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new RoostException($"No se pudo guardar el token en {File}: {ex.Message}");
        }
    }

    /// <summary>Una entrada del fichero.</summary>
    public sealed record Entry(string Target, string? User, string Secret, DateTimeOffset Written);
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<FileCredentialStore.Entry>))]
internal sealed partial class CredentialFileJson : JsonSerializerContext;

/// <summary>
/// Los tokens en el Secret Service de la sesión (el llavero de GNOME, KWallet con su puente, KeePassXC)
/// por <c>secret-tool</c> (paquete <c>libsecret-tools</c>): cifrado y con la sesión del usuario. Cada token
/// es un secreto con los atributos <c>roost-app=roost</c>, <c>roost-target=&lt;target&gt;</c> y
/// <c>roost-user=&lt;usuario&gt;</c>; el secreto va por stdin y vuelve por stdout, nunca en argumentos. Solo
/// se usa si hay un servicio vivo (<see cref="IsAlive"/>): sin bus de sesión, <c>secret-tool</c> intenta
/// arrancar uno y falla. Lo que hubiera en el fichero de <see cref="FileCredentialStore"/> (de antes de
/// tener llavero) se sigue leyendo (<c>fallback</c>), y lo nuevo va al llavero.
/// </summary>
public sealed class SecretServiceCredentialStore(ICommandRunner runner, string secretTool, ICredentialStore? fallback = null) : ICredentialStore
{
    public const string AppAttribute = "roost-app";
    public const string AppValue = "roost";
    public const string TargetAttribute = "roost-target";
    public const string UserAttribute = "roost-user";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>El <c>secret-tool</c> del PATH, o null (paquete <c>libsecret-tools</c>).</summary>
    public static string? Locate() => PathProbe.ResolveActive(["secret-tool"]);

    /// <summary>
    /// true si hay un Secret Service que conteste en esta sesión: una búsqueda que no encuentra nada
    /// sale con 0; sin bus de sesión o sin servicio, <c>secret-tool</c> falla («Cannot autolaunch D-Bus
    /// without X11 $DISPLAY», «The name org.freedesktop.secrets was not provided»). Nunca lanza.
    /// </summary>
    public static bool IsAlive(ICommandRunner runner, string secretTool, Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        if (string.IsNullOrEmpty(env("DBUS_SESSION_BUS_ADDRESS")) && !File.Exists(Path.Combine(env("XDG_RUNTIME_DIR") ?? "", "bus"))) return false;
        try
        {
            var result = runner.RunAsync(new CommandSpec(secretTool, ["search", "--all", AppAttribute, "roost-probe"], Timeout: TimeSpan.FromSeconds(5), Stdin: ""), null, CancellationToken.None)
                .GetAwaiter().GetResult();
            return result.Succeeded;
        }
        catch (Exception ex) when (ex is RoostException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    public StoredCredential? Read(string target)
    {
        var result = Run(["lookup", AppAttribute, AppValue, TargetAttribute, target], null);
        if (result.Succeeded && result.Stdout.Length > 0)
        {
            var user = Search().FirstOrDefault(e => e.Target == target)?.User;
            return new StoredCredential(target, user, result.Stdout.TrimEnd('\n', '\r'));
        }
        return fallback?.Read(target);
    }

    public void Write(StoredCredential credential)
    {
        var user = credential.UserName ?? "";
        var result = Run(["store", $"--label=Roost: token de git de {credential.Target}", AppAttribute, AppValue, TargetAttribute, credential.Target, UserAttribute, user], credential.Secret);
        if (!result.Succeeded)
            throw new RoostException($"No se pudo guardar el token en el llavero de la sesion (secret-tool salio con {result.ExitCode}): {GitOutput.Headline(result.Stderr)}");
        // El que hubiera en el fichero, de antes de tener llavero, ya no vale.
        fallback?.Delete(credential.Target);
    }

    public bool Delete(string target)
    {
        var existed = Search().Any(e => e.Target == target);
        if (existed)
        {
            var result = Run(["clear", AppAttribute, AppValue, TargetAttribute, target], null);
            if (!result.Succeeded)
                throw new RoostException($"No se pudo borrar el token del llavero (secret-tool salio con {result.ExitCode}): {GitOutput.Headline(result.Stderr)}");
        }
        var inFile = fallback?.Delete(target) ?? false;
        return existed || inFile;
    }

    public IReadOnlyList<CredentialInfo> List(string prefix)
    {
        var own = Search().Where(e => e.Target.StartsWith(prefix, StringComparison.Ordinal)).Select(e => new CredentialInfo(e.Target, e.User, e.Modified));
        var old = fallback?.List(prefix) ?? [];
        return own.Concat(old.Where(o => !own.Any(e => e.Target == o.Target))).OrderBy(c => c.Target, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Una entrada de <c>secret-tool search --all</c> (sin el secreto, que ahí también sale y no se guarda).</summary>
    public sealed record Found(string Target, string? User, DateTimeOffset? Modified);

    /// <summary>
    /// PURO: lo que saca <c>secret-tool search --all roost-app roost</c>: bloques <c>[ruta]</c> con líneas
    /// <c>clave = valor</c> (<c>label</c>, <c>secret</c>, <c>created</c>, <c>modified</c>, <c>attribute.roost-target</c>…).
    /// El secreto se ignora; las fechas son segundos desde la época.
    /// </summary>
    public static IReadOnlyList<Found> ParseSearch(string output)
    {
        var found = new List<Found>();
        string? target = null, user = null;
        DateTimeOffset? modified = null;
        void Flush()
        {
            if (target is not null) found.Add(new Found(target, string.IsNullOrEmpty(user) ? null : user, modified));
            target = null; user = null; modified = null;
        }
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith('[')) { Flush(); continue; }
            var eq = line.IndexOf(" = ", StringComparison.Ordinal);
            if (eq < 0) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 3)..];
            switch (key)
            {
                case "attribute." + TargetAttribute: target = value; break;
                case "attribute." + UserAttribute: user = value; break;
                case "modified":
                    if (long.TryParse(value.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var seconds))
                        modified = DateTimeOffset.FromUnixTimeSeconds(seconds);
                    break;
            }
        }
        Flush();
        return found;
    }

    private IReadOnlyList<Found> Search()
    {
        var result = Run(["search", "--all", "--unlock", AppAttribute, AppValue], null);
        return result.Succeeded ? ParseSearch(result.Stdout) : [];
    }

    private CommandResult Run(string[] arguments, string? stdin)
    {
        try
        {
            return runner.RunAsync(new CommandSpec(secretTool, arguments, Timeout: Timeout, Stdin: stdin ?? ""), null, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            throw new RoostException($"No se pudo ejecutar {secretTool}: {ex.Message}");
        }
    }
}
