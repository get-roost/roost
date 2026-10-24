using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Roost.Core.Platform;

namespace Roost.Core.Git;

/// <summary>Una operación de Roost sobre el repositorio de un sitio: clonar, actualizar o cambiar de rama.</summary>
/// <param name="Kind"><see cref="GitHistory.Clone"/>, <see cref="GitHistory.Pull"/>, <see cref="GitHistory.Checkout"/> o <see cref="GitHistory.Adopt"/>.</param>
/// <param name="From">El sha corto de antes (null al clonar).</param>
/// <param name="To">El sha corto de después (null si falló antes de llegar).</param>
public sealed record GitHistoryEntry(DateTimeOffset When, string Kind, string? Branch, string? From, string? To, bool Ok, double Seconds, string? Error);

/// <summary>El historial de cada sitio, en <c>state/git/&lt;sitio&gt;/history.json</c> (lo que enseña «Historial»).</summary>
public sealed class GitHistory(RoostPaths paths)
{
    public const string Clone = "clone";
    public const string Pull = "pull";
    public const string Checkout = "checkout";
    public const string Adopt = "adopt";

    /// <summary>Cuántas entradas se guardan como mucho (las más nuevas).</summary>
    public const int Keep = 100;

    public string FilePath(string site) => Path.Combine(paths.GitState, site, "history.json");

    /// <summary>De la más nueva a la más vieja. Un fichero roto cuenta como vacío: es un historial, no un registro.</summary>
    public IReadOnlyList<GitHistoryEntry> Load(string site)
    {
        var file = FilePath(site);
        if (!File.Exists(file)) return [];
        try
        {
            var entries = JsonSerializer.Deserialize(File.ReadAllText(file), GitJson.Default.ListGitHistoryEntry) ?? [];
            return entries.OrderByDescending(e => e.When).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public void Append(string site, GitHistoryEntry entry)
    {
        var entries = Load(site).Prepend(entry).Take(Keep).ToList();
        Save(site, entries);
    }

    /// <summary>Empieza de cero (al clonar un sitio nuevo con un nombre que ya tuvo historial).</summary>
    public void Clear(string site) => Install.InstallFiles.TryDeleteFile(FilePath(site));

    private void Save(string site, List<GitHistoryEntry> entries)
    {
        var file = FilePath(site);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp = file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(entries, GitJson.Default.ListGitHistoryEntry));
        File.Move(temp, file, overwrite: true);
    }
}

/// <summary>
/// El registro completo de lo que dijo git, en <c>logs/git-&lt;sitio&gt;.log</c>. Cada línea pasa por
/// <see cref="Redact"/>: si git imprimiera una dirección con <c>usuario@</c> o <c>usuario:clave@</c>,
/// no queda en disco.
/// </summary>
public sealed partial class GitLog(RoostPaths paths)
{
    public string FilePath(string site) => Path.Combine(paths.Logs, $"git-{site}.log");

    /// <summary>Abre una operación: escribe su cabecera y devuelve a quién darle las líneas. Al cerrarla, escribe el resultado.</summary>
    public Session Begin(string site, string operation, string detail)
    {
        Directory.CreateDirectory(paths.Logs);
        var writer = new StreamWriter(new FileStream(FilePath(site), FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        writer.WriteLine($"=== {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {operation} {Redact(detail)}");
        return new Session(writer);
    }

    /// <summary>Quita lo que haya entre <c>://</c> y <c>@</c>: un usuario, o un usuario y su contraseña.</summary>
    public static string Redact(string line) => UserInfo().Replace(line, "://***@");

    [GeneratedRegex(@"://[^/@\s]+@")]
    private static partial Regex UserInfo();

    /// <summary>Las líneas de una operación. Es un <see cref="IProgress{T}"/> síncrono: escribe en el acto.</summary>
    public sealed class Session(StreamWriter writer) : IProgress<string>, IDisposable
    {
        private readonly object _lock = new();

        public void Report(string value)
        {
            lock (_lock)
            {
                try { writer.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] {Redact(value)}"); }
                catch (IOException) { /* el log nunca tumba la operación */ }
            }
        }

        /// <summary>La última línea de la operación: cómo acabó.</summary>
        public void End(bool ok, string summary)
        {
            lock (_lock)
            {
                try { writer.WriteLine($"=== {(ok ? "ok" : "FALLO")}: {Redact(summary)}"); writer.WriteLine(); }
                catch (IOException) { }
            }
        }

        public void Dispose()
        {
            lock (_lock) writer.Dispose();
        }
    }
}

/// <summary>El JSON del historial, generado al compilar (como <c>SiteJson</c>).</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true)]
[JsonSerializable(typeof(List<GitHistoryEntry>))]
internal sealed partial class GitJson : JsonSerializerContext;
