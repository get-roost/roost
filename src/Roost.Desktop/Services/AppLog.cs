using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Logging;
using Roost.Core.Platform;

namespace Roost.Desktop.Services;

/// <summary>
/// El log de la propia app (J6): <c>logs/roost-desktop.log</c>, junto a los del daemon y los
/// servicios, así que sale también en Registros. Cuenta el arranque, los errores que se enseñan
/// en un diálogo, lo que ha fallado sin que nadie lo recogiera, los avisos de Avalonia y lo que
/// hace Ajustes con el sistema (la raíz, la tarea al iniciar sesión, roostd).
/// <list type="bullet">
/// <item>Se rota al pasar de <see cref="MaxBytes"/>: el anterior queda como <c>roost-desktop.log.1</c>.</item>
/// <item>Cada línea se abre, se escribe y se cierra: nunca queda abierto (mover la raíz lo necesita).</item>
/// <item>Sin datos sensibles: <see cref="Scrub"/> tapa lo que parezca una contraseña o un token.</item>
/// <item>Nunca lanza: un log que no se puede escribir no impide usar la app.</item>
/// </list>
/// Hasta que se llama a <see cref="Start"/> (la app de verdad, no los tests) no escribe nada.
/// </summary>
public static partial class AppLog
{
    public const string FileName = "roost-desktop.log";

    /// <summary>A partir de aquí se rota (1 MB: semanas de uso normal).</summary>
    public const long MaxBytes = 1024 * 1024;

    private static readonly Lock Gate = new();
    private static string? _file;
    private static int _paused;

    /// <summary>El fichero del log; null si la app no lo ha empezado (los tests).</summary>
    public static string? FilePath => _file;

    /// <summary>Empieza a escribir en <c>&lt;raíz&gt;/logs/roost-desktop.log</c>.</summary>
    public static void Start(RoostPaths paths, string version, IEnumerable<string>? args)
    {
        _file = Path.Combine(paths.Logs, FileName);
        var line = $"Roost {version} se abre · raíz {paths.Root}";
        var given = string.Join(" ", args ?? []);
        Info(given.Length == 0 ? line : $"{line} · {given}");
    }

    /// <summary>Deja de escribir (la app se cierra) o, en los tests, vuelve a como estaba.</summary>
    public static void Stop(string? why = null)
    {
        if (why is not null) Info(why);
        _file = null;
    }

    public static void Info(string message) => Write("info", message);

    public static void Warn(string message) => Write("warn", message);

    public static void Error(string message, Exception? exception = null) =>
        Write("error", exception is null ? message : $"{message}: {exception.GetType().Name}: {exception.Message}{Environment.NewLine}{exception.StackTrace}");

    /// <summary>
    /// Mientras dura, no se escribe nada (las líneas se pierden): mover la raíz renombra la carpeta
    /// de los logs y no puede haber nadie escribiendo dentro.
    /// </summary>
    public static IDisposable Pause()
    {
        Interlocked.Increment(ref _paused);
        return new Resume();
    }

    private sealed class Resume : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) Interlocked.Decrement(ref _paused);
        }
    }

    /// <summary>La línea tal como va al fichero (PURO): fecha, nivel entre corchetes y el texto ya limpio.</summary>
    public static string Format(DateTime when, string level, string message) =>
        $"{when.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} [{level}] {Scrub(message)}";

    /// <summary>
    /// PURO: quita lo que no debe ir a un log. Las credenciales dentro de una URL
    /// (<c>https://usuario:token@host</c> → <c>https://***@host</c>) y el valor de cualquier
    /// <c>password=</c>, <c>token:</c>, <c>secret=</c>, <c>pwd=</c> o <c>-p…</c> pegado.
    /// </summary>
    public static string Scrub(string text)
    {
        text = UrlCredentials().Replace(text, "://***@");
        text = SecretValue().Replace(text, m => $"{m.Groups["key"].Value}{m.Groups["sep"].Value}***");
        return text;
    }

    [GeneratedRegex(@"://[^/\s:@]+(:[^/\s@]*)?@")]
    private static partial Regex UrlCredentials();

    [GeneratedRegex(@"(?<key>\b(?:password|passwd|pwd|token|secret|apikey|api_key)\b)(?<sep>\s*[=:]\s*)(?<value>""[^""]*""|'[^']*'|\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex SecretValue();

    private static void Write(string level, string message)
    {
        if (_file is not { } file || Volatile.Read(ref _paused) > 0) return;
        var line = Format(DateTime.Now, level, message) + Environment.NewLine;
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                Rotate(file, MaxBytes);
                // Abrir, escribir y cerrar, compartido: Registros lo lee a la vez y nadie lo tiene cogido.
                using var stream = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                var bytes = Encoding.UTF8.GetBytes(line);
                stream.Write(bytes, 0, bytes.Length);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Un log que no se puede escribir no impide usar la app.
            }
        }
    }

    /// <summary>Si <paramref name="file"/> pasa de <paramref name="maxBytes"/>, lo deja como <c>.1</c> (pisando el anterior).</summary>
    internal static void Rotate(string file, long maxBytes)
    {
        var info = new FileInfo(file);
        if (!info.Exists || info.Length < maxBytes) return;
        File.Move(file, file + ".1", overwrite: true);
    }

    /// <summary>
    /// Los avisos y errores de Avalonia (bindings rotos, una plantilla que no carga) también van
    /// al log, además de a donde ya iban (Trace).
    /// </summary>
    public sealed class AvaloniaSink(ILogSink? inner) : ILogSink
    {
        // Cada aviso, una vez por sesión: uno de un binding se repite en cada redibujado.
        private readonly HashSet<string> _seen = [];

        public bool IsEnabled(LogEventLevel level, string area) => level >= LogEventLevel.Warning || inner?.IsEnabled(level, area) == true;

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate) =>
            Log(level, area, source, messageTemplate, []);

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
        {
            if (inner?.IsEnabled(level, area) == true) inner.Log(level, area, source, messageTemplate, propertyValues);
            if (level < LogEventLevel.Warning) return;
            var text = $"Avalonia ({area}): {Fill(messageTemplate, propertyValues)}";
            lock (_seen)
                if (!_seen.Add(text)) return;
            if (level >= LogEventLevel.Error) Error(text);
            else Warn(text);
        }

        /// <summary>PURO: «{Property} vale {Value}» con sus valores, por orden, como el sink de Trace.</summary>
        public static string Fill(string template, object?[] values)
        {
            var result = new StringBuilder();
            var next = 0;
            for (var i = 0; i < template.Length; i++)
            {
                if (template[i] == '{' && template.IndexOf('}', i) is var end and > 0 && next < values.Length)
                {
                    result.Append(values[next++]);
                    i = end;
                }
                else
                {
                    result.Append(template[i]);
                }
            }
            return result.ToString();
        }
    }
}
