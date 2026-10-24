using Tomlyn.Parsing;
using Tomlyn.Syntax;

namespace Roost.Core.Node;

/// <summary>
/// El Node global, en <c>[node] version</c> de <c>roost.toml</c> (lo fija <c>roost node use</c>):
/// una rama (<c>"24"</c>, así entran solas las versiones nuevas de esa rama) o una versión exacta. Se
/// lee y se edita como <c>RoostConfig</c>, sobre el árbol de sintaxis de Tomlyn: solo cambia esa
/// clave, y el resto del fichero (comentarios incluidos) se queda como estaba. <c>RoostConfig</c> no
/// mira la tabla <c>[node]</c>. Sin clave, el global es el Node de Roost más nuevo instalado.
/// </summary>
public static class NodeConfig
{
    public const string Table = "node";

    public const string Key = "version";

    /// <summary><c>[node] version</c>, o null si no está (o no hay fichero). Lanza <see cref="RoostException"/> si el TOML no vale.</summary>
    public static string? ReadVersion(string path)
    {
        if (!File.Exists(path)) return null;
        var doc = Parse(File.ReadAllText(path), path);
        return Find(doc)?.Value switch
        {
            null => null,
            StringValueSyntax s => string.IsNullOrWhiteSpace(s.Value) ? null : s.Value.Trim(),
            _ => throw new RoostException($"{path}: [{Table}] {Key} tiene que ser un texto entre comillas (\"24\")."),
        };
    }

    /// <summary>Fija <c>[node] version</c> (null la quita), creando el fichero o la tabla si no existen.</summary>
    public static void SetVersion(string path, string? version)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var text = File.Exists(path) ? File.ReadAllText(path) : "";
        var doc = Parse(text, path);
        var table = doc.Tables.OfType<TableSyntax>().FirstOrDefault(t => t.Name?.ToString().Trim() == Table);
        var existing = Find(doc);
        if (string.IsNullOrWhiteSpace(version))
        {
            if (existing is not null) table!.Items.RemoveChild(existing);
        }
        else if (existing is not null)
        {
            existing.Value = new StringValueSyntax(version.Trim());
        }
        else
        {
            if (table is null)
            {
                table = new TableSyntax(Table);
                doc.Tables.Add(table);
            }
            table.Items.Add(new KeyValueSyntax(Key, new StringValueSyntax(version.Trim())));
        }
        File.WriteAllText(path, doc.ToString());
    }

    private static KeyValueSyntax? Find(DocumentSyntax doc) =>
        doc.Tables.OfType<TableSyntax>().FirstOrDefault(t => t.Name?.ToString().Trim() == Table)?
            .Items.OfType<KeyValueSyntax>().FirstOrDefault(k => k.Key?.ToString().Trim() == Key);

    private static DocumentSyntax Parse(string text, string path)
    {
        var doc = SyntaxParser.Parse(text, path, false);
        if (doc.HasErrors)
            throw new RoostException($"{path} no es TOML valido: {string.Join("; ", doc.Diagnostics)}");
        return doc;
    }
}
