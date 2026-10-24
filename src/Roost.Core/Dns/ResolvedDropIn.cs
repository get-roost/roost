namespace Roost.Core.Dns;

/// <summary>
/// El drop-in de systemd-resolved con el que Roost manda los <c>.test</c> a su DNS local en Linux
/// (ROOST.md §5.7): <c>/etc/systemd/resolved.conf.d/roost.conf</c> con <c>DNS=127.53.0.1:5353</c> y
/// <c>Domains=~test</c>. Todo es fijo: ni la ruta ni el contenido vienen de fuera. Como
/// <c>NrptScripts</c>, se compila también dentro del helper (enlazado como fuente), así que no puede
/// depender de nada más de Roost. El 5353 es porque resolved admite <c>ip:puerto</c> desde systemd 246
/// y así el servidor de Roost escucha sin privilegios (el 53 pediría <c>CAP_NET_BIND_SERVICE</c>).
/// </summary>
internal static class ResolvedDropIn
{
    public const string Directory = "/etc/systemd/resolved.conf.d";
    public const string FilePath = Directory + "/roost.conf";
    public const string MainFile = "/etc/systemd/resolved.conf";

    public const string Address = "127.53.0.1";
    public const int Port = 5353;
    public const string Server = Address + ":5353";

    /// <summary>La primera línea del drop-in, por la que se reconoce el de Roost.</summary>
    public const string Marker = "# Roost: los .test los responde el DNS local de Roost (roost dns status). Lo quita roost dns remove.";

    public const string Content = Marker + "\n[Resolve]\nDNS=" + Server + "\nDomains=~test\n";

    /// <summary>El «comentario» con el que se marca la regla de Roost, el mismo que en Windows (<c>NrptScripts.Comment</c>).</summary>
    public const string Comment = "Roost";

    /// <summary>Lo que dice un fichero de resolved de los <c>.test</c>: sus dominios de <c>Domains=</c>, sus servidores de <c>DNS=</c> y si es el de Roost.</summary>
    public sealed record Entry(string File, IReadOnlyList<string> Domains, IReadOnlyList<string> Servers, bool IsRoost)
    {
        /// <summary>true si alguno de sus dominios es <c>test</c> (con o sin <c>~</c>).</summary>
        public bool RoutesTest => Domains.Any(IsTestDomain);
    }

    /// <summary>PURO: true si el texto es el drop-in de Roost (empieza por su marca).</summary>
    public static bool IsRoost(string text) => text.TrimStart().StartsWith(Marker, StringComparison.Ordinal);

    /// <summary>
    /// PURO: un dominio de <c>Domains=</c> que es el <c>test</c> de Roost: <c>~test</c> (dominio de
    /// enrutado), <c>test</c> (de búsqueda), y las variantes con punto delante que resolved tolera.
    /// </summary>
    public static bool IsTestDomain(string domain) =>
        domain.TrimStart('~').TrimStart('.').TrimEnd('.').Equals("test", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// PURO: los dominios y los servidores de la sección <c>[Resolve]</c> de un fichero de resolved,
    /// con sus reglas: los valores van separados por espacios, varias asignaciones se suman y una
    /// vacía (<c>Domains=</c>) borra lo acumulado; <c>#</c> y <c>;</c> al principio son comentarios.
    /// Las claves de otras secciones no cuentan.
    /// </summary>
    public static (IReadOnlyList<string> Domains, IReadOnlyList<string> Servers) Parse(string text)
    {
        var domains = new List<string>();
        var servers = new List<string>();
        var inResolve = false;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';') continue;
            if (line[0] == '[')
            {
                inResolve = line.Equals("[Resolve]", StringComparison.Ordinal);
                continue;
            }
            if (!inResolve) continue;
            var eq = line.IndexOf('=');
            if (eq < 0) continue;
            var key = line[..eq].Trim();
            var values = line[(eq + 1)..].Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            var target = key switch
            {
                "Domains" => domains,
                "DNS" => servers,
                _ => null,
            };
            if (target is null) continue;
            if (values.Length == 0) target.Clear();   // «Domains=» vacío: se olvida lo anterior
            else target.AddRange(values);
        }
        return (domains, servers);
    }

    /// <summary>
    /// Lo que hay en el sistema: el <c>resolved.conf</c> principal y cada drop-in de la carpeta (los
    /// <c>.conf</c>, por orden de nombre, como los lee resolved). Lo que no se puede leer se salta.
    /// Lo usan el helper (para no pisar otro drop-in que ya enrute <c>test</c>) y <c>roost dns status</c>.
    /// </summary>
    public static IReadOnlyList<Entry> ReadFiles(string directory = Directory, string mainFile = MainFile)
    {
        var files = new List<string>();
        if (File.Exists(mainFile)) files.Add(mainFile);
        if (System.IO.Directory.Exists(directory))
            files.AddRange(System.IO.Directory.GetFiles(directory, "*.conf").Order(StringComparer.Ordinal));

        var entries = new List<Entry>();
        foreach (var file in files)
        {
            string text;
            try { text = File.ReadAllText(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            var (domains, servers) = Parse(text);
            entries.Add(new Entry(file, domains, servers, IsRoost(text)));
        }
        return entries;
    }
}
