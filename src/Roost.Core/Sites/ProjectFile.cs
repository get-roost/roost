using System.Text;
using Tomlyn.Parsing;
using Tomlyn.Syntax;

namespace Roost.Core.Sites;

/// <summary>Lo que dice el <c>.roost.toml</c> de un proyecto; null en cada clave que no está.</summary>
public sealed record ProjectSettings
{
    /// <summary>El nombre del sitio al aparcar la carpeta de encima (<c>roost park</c>); si no, el de la carpeta.</summary>
    public string? Name { get; init; }

    /// <summary>El PHP del proyecto: rama, versión o instalación. También lo usa el <c>php</c> de la terminal.</summary>
    public string? Php { get; init; }

    /// <summary>La raíz web, relativa a la carpeta del proyecto (<c>public</c>); <c>.</c> = la carpeta.</summary>
    public string? Root { get; init; }

    /// <summary>Su base de datos en la global (solo el nombre).</summary>
    public string? Db { get; init; }

    /// <summary>Sus alias y subdominios (<c>api.tienda.test</c>, <c>tienda-alias.test</c>).</summary>
    public IReadOnlyList<string>? Aliases { get; init; }

    public bool IsEmpty => Name is null && Php is null && Root is null && Db is null && Aliases is null;
}

/// <summary>
/// Sitios avanzados (C11): el <c>.roost.toml</c> de un proyecto, versionable con él, con lo que
/// Roost tiene que saber para servirlo: <c>php</c>, <c>root</c>, <c>db</c> y <c>aliases</c> (y
/// <c>name</c>, al aparcar). Lo leen <c>roost site add</c> y el aparcado al dar de alta el sitio,
/// <c>roost up</c> y la app al abrirse (<see cref="SiteManager.SyncProjectFiles"/>), y el <c>php</c>
/// de la terminal (<c>TerminalPhp</c>). Se lee y se edita con el árbol de sintaxis de Tomlyn (como
/// <c>roost.toml</c>), así que conserva comentarios y claves que Roost no conoce; sin reflexión, lo
/// puede leer el shim publicado con NativeAOT.
/// </summary>
public static class ProjectFile
{
    public const string FileName = ".roost.toml";

    public static string PathIn(string folder) => Path.Combine(folder, FileName);

    public static bool ExistsIn(string folder) => File.Exists(PathIn(folder));

    /// <summary>
    /// Lo que dice el <c>.roost.toml</c> de <paramref name="folder"/>, o null si no tiene. Lanza
    /// <see cref="RoostException"/> si no es TOML válido o una clave no es del tipo que toca; las claves
    /// que no conoce, las ignora.
    /// </summary>
    public static ProjectSettings? Read(string folder)
    {
        var file = PathIn(folder);
        if (!File.Exists(file)) return null;
        return Parse(File.ReadAllText(file), file);
    }

    /// <summary>PURO: <see cref="Read"/> sobre un texto.</summary>
    public static ProjectSettings Parse(string text, string file)
    {
        var doc = SyntaxParser.Parse(text, file, false);
        if (doc.HasErrors)
            throw new RoostException($"{file} no es TOML valido: {string.Join("; ", doc.Diagnostics)}");

        string? Text(string key) => Value(doc, key) switch
        {
            null => null,
            StringValueSyntax s => string.IsNullOrWhiteSpace(s.Value) ? null : s.Value.Trim(),
            _ => throw new RoostException($"{file}: {key} tiene que ser un texto entre comillas."),
        };

        IReadOnlyList<string>? List(string key) => Value(doc, key) switch
        {
            null => null,
            ArraySyntax array => array.Items.Select(item => item.Value is StringValueSyntax s
                    ? s.Value?.Trim() ?? ""
                    : throw new RoostException($"{file}: {key} tiene que ser una lista de textos entre comillas, como [\"api.tienda.test\"]."))
                .Where(v => v.Length > 0).ToList(),
            _ => throw new RoostException($"{file}: {key} tiene que ser una lista, como [\"api.tienda.test\"]."),
        };

        return new ProjectSettings { Name = Text("name"), Php = Text("php"), Root = Text("root"), Db = Text("db"), Aliases = List("aliases") };
    }

    /// <summary>
    /// Escribe <paramref name="settings"/> en el <c>.roost.toml</c> de <paramref name="folder"/>: si no
    /// existe, lo crea con una cabecera que explica cada clave; si existe, cambia solo esas claves
    /// (una a null la quita) y respeta lo demás, comentarios incluidos.
    /// </summary>
    public static void Write(string folder, ProjectSettings settings)
    {
        var file = PathIn(folder);
        var text = File.Exists(file) ? File.ReadAllText(file) : null;
        File.WriteAllText(file, Render(text, settings, file));
    }

    /// <summary>PURO: el texto de <see cref="Write"/> a partir del de ahora (null si no existe).</summary>
    public static string Render(string? existing, ProjectSettings settings, string file = FileName)
    {
        if (existing is null)
        {
            var text = new StringBuilder();
            text.Append("# Roost: como se sirve este proyecto (https://<nombre>.test/). Versionalo con el proyecto:\n");
            text.Append("# roost site add, roost park y la app lo leen; roost up y la app, al abrirse, ponen el sitio al dia.\n");
            text.Append("# php: rama (8.3), version (8.3.36) o global. root: la raiz web (public, web, dist...).\n");
            text.Append("# db: su base de datos. aliases: otros nombres .test (tienda-alias.test, api.tienda.test).\n");
            if (settings.Name is { } name) text.Append($"name = {Quote(name)}\n");
            if (settings.Php is { } php) text.Append($"php = {Quote(php)}\n");
            if (settings.Root is { } root) text.Append($"root = {Quote(root)}\n");
            if (settings.Db is { } db) text.Append($"db = {Quote(db)}\n");
            if (settings.Aliases is { Count: > 0 } aliases) text.Append($"aliases = [{string.Join(", ", aliases.Select(Quote))}]\n");
            return text.ToString();
        }

        // Sin el salto final, una clave nueva quedaría pegada a la última línea.
        if (existing.Length > 0 && !existing.EndsWith('\n')) existing += existing.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var doc = SyntaxParser.Parse(existing, file, false);
        if (doc.HasErrors)
            throw new RoostException($"{file} no es TOML valido: {string.Join("; ", doc.Diagnostics)}");
        Set(doc, "name", settings.Name is { } n ? new StringValueSyntax(n) : null);
        Set(doc, "php", settings.Php is { } p ? new StringValueSyntax(p) : null);
        Set(doc, "root", settings.Root is { } r ? new StringValueSyntax(r) : null);
        Set(doc, "db", settings.Db is { } d ? new StringValueSyntax(d) : null);
        Set(doc, "aliases", settings.Aliases is { Count: > 0 } a ? new ArraySyntax([.. a]) : null);
        return doc.ToString();
    }

    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static ValueSyntax? Value(DocumentSyntax doc, string key) =>
        doc.KeyValues.FirstOrDefault(kv => kv.Key?.ToString().Trim() == key)?.Value;

    private static void Set(DocumentSyntax doc, string key, ValueSyntax? value)
    {
        var existing = doc.KeyValues.FirstOrDefault(kv => kv.Key?.ToString().Trim() == key);
        if (value is null)
        {
            if (existing is not null) doc.KeyValues.RemoveChild(existing);
        }
        else if (existing is null)
        {
            doc.KeyValues.Add(new KeyValueSyntax(key, value));
        }
        else
        {
            existing.Value = value;
        }
    }

    /// <summary>
    /// Lo que se escribe en el <c>.roost.toml</c> para un sitio: su PHP, su raíz, su base de datos y sus
    /// alias, tal como están en el registro (el nombre solo si ya lo tenía el fichero). Un
    /// <c>php = "global"</c> o un <c>root = "."</c> que ya estaban se quedan así.
    /// </summary>
    public static ProjectSettings For(Site site, ProjectSettings? current = null) => new()
    {
        Name = current?.Name,
        Php = site.Php ?? (current?.Php is { } php && php.Equals("global", StringComparison.OrdinalIgnoreCase) ? php : null),
        Root = site.Root ?? (current?.Root is "." ? "." : null),
        Db = site.Database,
        Aliases = site.Aliases is { Count: > 0 } aliases ? aliases : null,
    };
}
