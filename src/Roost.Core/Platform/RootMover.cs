using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Roost.Core.Platform;

/// <summary>Por dónde va un movimiento de la raíz: el paso, los bytes hechos de los que hay y el fichero.</summary>
public sealed record RootMoveProgress(RootMoveStep Step, long Done, long Total, string? Current);

public enum RootMoveStep
{
    Copying,
    Verifying,
    Placing,
}

/// <summary>Lo que hay en una raíz: cuánto pesa, cuántos ficheros y qué enlaces lleva.</summary>
public sealed record RootSize(long Bytes, int Files, IReadOnlyList<string> Links);

/// <summary>
/// J2: mover la raíz de Roost (binarios, datos de las bases de datos, la CA, los registros, la www)
/// a otra carpeta sin perder nada. La regla es que <b>el origen nunca se toca</b> hasta que la copia
/// está comprobada, y ni siquiera entonces se borra: se renombra (<see cref="Retire"/>).
/// <list type="number">
/// <item>Se copia a una carpeta de trabajo junto al destino (<c>.&lt;nombre&gt;.tmp-…</c>, en el mismo
/// disco, para poder renombrar).</item>
/// <item>Se comprueba la copia entera: los mismos ficheros con el mismo tamaño y el mismo SHA-256,
/// las mismas carpetas y los mismos enlaces. Nada de más.</item>
/// <item>Se renombra al destino. Si algo falla antes, se borra la carpeta de trabajo (sin seguir los
/// enlaces) y se lanza <see cref="RoostException"/>: el destino no llega a existir.</item>
/// </list>
/// Los enlaces (uniones y simbólicos, como un proyecto de la www que apunta a otro disco) se
/// recrean como enlaces, no se copia lo que hay detrás; si apuntan dentro de la raíz, al sitio nuevo.
/// Las ACL protegidas (las claves privadas de la CA y de git, <see cref="PrivateFile"/>) se copian
/// tal cual: con las heredadas del destino, otros usuarios podrían leerlas (y OpenSSH las rechaza).
/// <para>
/// En Linux y macOS no hay uniones ni SDDL: los enlaces son simbólicos (un destino relativo se deja
/// relativo) y lo «protegido» son los modos 0600/0700, que se copian tal cual (lo demás nace con el
/// umask). La www de <c>/var/www/roost</c> está fuera de la raíz y no se mueve con ella. Como la copia
/// se renombra al final, el destino tiene que estar en el mismo sistema de ficheros que su carpeta de
/// trabajo (la de al lado), que es lo normal: la raíz vieja puede estar en otro disco.
/// </para>
/// </summary>
public static class RootMover
{
    /// <summary>Lo que hay en <paramref name="root"/> sin <paramref name="skip"/> (nombres de primer nivel). No sigue enlaces.</summary>
    public static RootSize Measure(string root, IReadOnlyCollection<string> skip, CancellationToken cancellation = default)
    {
        long bytes = 0;
        var files = 0;
        var links = new List<string>();
        foreach (var (entry, relative) in Walk(new DirectoryInfo(root), "", skip))
        {
            cancellation.ThrowIfCancellationRequested();
            if (IsLink(entry)) links.Add(relative);
            else if (entry is FileInfo file)
            {
                bytes += file.Length;
                files++;
            }
        }
        return new RootSize(bytes, files, links);
    }

    /// <summary>
    /// Copia <paramref name="from"/> a <paramref name="to"/> (que no debe existir, o estar vacía), la
    /// comprueba y la deja en su sitio. <paramref name="skip"/> son nombres del primer nivel que no
    /// se copian (el <c>roost.toml</c> y la caché, si viven dentro, como con <c>ROOST_HOME</c>).
    /// </summary>
    public static void Copy(string from, string to, IReadOnlyCollection<string> skip, IProgress<RootMoveProgress>? progress = null, CancellationToken cancellation = default)
    {
        from = Path.GetFullPath(from).TrimEnd(Path.DirectorySeparatorChar);
        to = Path.GetFullPath(to).TrimEnd(Path.DirectorySeparatorChar);
        if (!Directory.Exists(from)) throw new RoostException($"No existe {from}.");
        if (Directory.Exists(to) && Directory.EnumerateFileSystemEntries(to).Any())
            throw new RoostException($"{to} ya tiene cosas dentro: tiene que estar vacia o no existir.");
        if (File.Exists(to)) throw new RoostException($"{to} es un fichero.");

        var parent = Path.GetDirectoryName(to) ?? throw new RoostException($"{to} no vale como raiz: tiene que ser una carpeta, no un disco entero.");
        Directory.CreateDirectory(parent);
        var work = Path.Combine(parent, $".{Path.GetFileName(to)}.tmp-{Guid.NewGuid():N}");
        var total = Measure(from, skip, cancellation).Bytes;
        try
        {
            CopyTree(from, work, from, to, skip, total, progress, cancellation);
            Verify(from, work, from, to, skip, total, progress, cancellation);
            progress?.Report(new RootMoveProgress(RootMoveStep.Placing, total, total, null));
            if (Directory.Exists(to)) Directory.Delete(to);   // vacía (comprobado arriba)
            Directory.Move(work, to);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or RoostException)
        {
            try { DeleteTree(work); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { /* se queda la de trabajo: empieza por punto */ }
            if (ex is OperationCanceledException) throw;
            throw ex as RoostException ?? new RoostException($"No se pudo copiar {from} a {to}: {ex.Message}");
        }
    }

    /// <summary>
    /// Deja la raíz de antes como <c>&lt;raíz&gt;.antigua-AAAAMMDD-HHMMSS</c>, para que se vea que ya
    /// no es la que usa Roost y se pueda borrar a mano cuando se haya comprobado todo. Devuelve dónde
    /// ha quedado, o null si no se ha podido (algo tiene un fichero abierto dentro): entonces se queda
    /// donde estaba, intacta.
    /// </summary>
    public static string? Retire(string from, DateTime now)
    {
        from = Path.GetFullPath(from).TrimEnd(Path.DirectorySeparatorChar);
        var retired = $"{from}.antigua-{now:yyyyMMdd-HHmmss}";
        try
        {
            Directory.Move(from, retired);
            return retired;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Tras mover: lo que guardaba rutas de la raíz vieja apunta a la nueva. Los sitios cuya carpeta
    /// estaba dentro (los de la www) y <c>[git] exe</c> si era un git de dentro. Lo demás (nginx.conf,
    /// php.ini, my.ini, los vhost entre las marcas de Roost) lo vuelve a generar <c>roost up</c>.
    /// Devuelve cuántos sitios ha cambiado.
    /// </summary>
    public static int Rebase(RoostPaths moved, string from, string to)
    {
        from = Path.GetFullPath(from).TrimEnd(Path.DirectorySeparatorChar);
        to = Path.GetFullPath(to).TrimEnd(Path.DirectorySeparatorChar);
        var registry = new Sites.SiteRegistry(moved);
        var changed = 0;
        foreach (var site in registry.Load())
        {
            var folder = Retarget(site.Folder, from, to);
            if (folder == Path.GetFullPath(site.Folder)) continue;
            registry.Upsert(site with { Folder = folder });
            changed++;
        }
        var config = Config.RoostConfig.Load(moved.ConfigFile);
        if (config.GitExe is { } git && Path.IsPathFullyQualified(git) && Retarget(git, from, to) is var rebased && rebased != Path.GetFullPath(git))
            Config.RoostConfig.SetGitExe(moved.ConfigFile, rebased);
        return changed;
    }

    /// <summary>Borra una carpeta sin entrar en sus enlaces (se quita el enlace, nunca lo que hay detrás).</summary>
    public static void DeleteTree(string path)
    {
        var dir = new DirectoryInfo(path);
        if (!dir.Exists) return;
        if (IsLink(dir))
        {
            dir.Delete();
            return;
        }
        foreach (var entry in dir.EnumerateFileSystemInfos())
        {
            if (entry is DirectoryInfo sub && !IsLink(sub)) DeleteTree(sub.FullName);
            else
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReadOnly)) entry.Attributes &= ~FileAttributes.ReadOnly;
                entry.Delete();
            }
        }
        dir.Delete();
    }

    /// <summary>PURO: a dónde apunta un enlace en la raíz nueva (si apuntaba dentro de la vieja, al mismo sitio dentro de la nueva).</summary>
    public static string Retarget(string target, string from, string to)
    {
        if (!Path.IsPathFullyQualified(target)) return target;
        var full = Path.GetFullPath(target);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(full.TrimEnd(Path.DirectorySeparatorChar), from, comparison)) return to;
        var prefix = from + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, comparison) ? Path.Combine(to, full[prefix.Length..]) : full;
    }

    // Recorrer, copiar y comprobar

    private static bool IsLink(FileSystemInfo entry) => entry.Attributes.HasFlag(FileAttributes.ReparsePoint) && entry.LinkTarget is not null;

    /// <summary>Todo lo de <paramref name="dir"/>, con su ruta relativa; los enlaces se dan pero no se recorren.</summary>
    private static IEnumerable<(FileSystemInfo Entry, string Relative)> Walk(DirectoryInfo dir, string relative, IReadOnlyCollection<string>? skip)
    {
        foreach (var entry in dir.EnumerateFileSystemInfos().OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            if (relative.Length == 0 && skip?.Contains(entry.Name, StringComparer.OrdinalIgnoreCase) == true) continue;
            var path = relative.Length == 0 ? entry.Name : Path.Combine(relative, entry.Name);
            yield return (entry, path);
            if (entry is DirectoryInfo sub && !IsLink(sub))
                foreach (var child in Walk(sub, path, null)) yield return child;
        }
    }

    private static void CopyTree(string source, string work, string from, string to, IReadOnlyCollection<string> skip, long total,
        IProgress<RootMoveProgress>? progress, CancellationToken cancellation)
    {
        Directory.CreateDirectory(work);
        CopySecurity(new DirectoryInfo(source), new DirectoryInfo(work));
        long done = 0;
        foreach (var (entry, relative) in Walk(new DirectoryInfo(source), "", skip))
        {
            cancellation.ThrowIfCancellationRequested();
            var target = Path.Combine(work, relative);
            if (IsLink(entry))
            {
                CreateLink(entry, target, TargetFor(entry, from, to));
            }
            else if (entry is DirectoryInfo dir)
            {
                Directory.CreateDirectory(target);
                CopySecurity(dir, new DirectoryInfo(target));
            }
            else if (entry is FileInfo file)
            {
                progress?.Report(new RootMoveProgress(RootMoveStep.Copying, done, total, relative));
                try
                {
                    file.CopyTo(target, overwrite: false);
                }
                catch (IOException ex)
                {
                    throw new RoostException($"No se pudo copiar {file.FullName}: {ex.Message} (¿lo tiene abierto otro programa?)");
                }
                CopySecurity(file, new FileInfo(target));
                done += file.Length;
            }
        }
        progress?.Report(new RootMoveProgress(RootMoveStep.Copying, total, total, null));
    }

    private static void Verify(string source, string work, string from, string to, IReadOnlyCollection<string> skip, long total,
        IProgress<RootMoveProgress>? progress, CancellationToken cancellation)
    {
        long done = 0;
        var expected = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var (entry, relative) in Walk(new DirectoryInfo(source), "", skip))
        {
            cancellation.ThrowIfCancellationRequested();
            expected.Add(relative);
            var copy = Path.Combine(work, relative);
            if (IsLink(entry))
            {
                var made = entry is DirectoryInfo ? (FileSystemInfo)new DirectoryInfo(copy) : new FileInfo(copy);
                var want = TargetFor(entry, from, to);
                // Solo que el enlace esté y apunte a donde toca: lo de detrás puede no existir aún (apunta a la raíz
                // nueva, que todavía es la carpeta de trabajo) y en Linux Exists seguiría el enlace y diría que no.
                if (!IsLink(made) || !SameTarget(made.LinkTarget!, want))
                    throw new RoostException($"La copia no cuadra: el enlace {relative} no apunta a {want}.");
            }
            else if (entry is DirectoryInfo)
            {
                if (!Directory.Exists(copy)) throw new RoostException($"La copia no cuadra: falta la carpeta {relative}.");
            }
            else if (entry is FileInfo file)
            {
                progress?.Report(new RootMoveProgress(RootMoveStep.Verifying, done, total, relative));
                var made = new FileInfo(copy);
                if (!made.Exists || made.Length != file.Length || !SameContent(file.FullName, made.FullName))
                    throw new RoostException($"La copia no cuadra: {relative} no es igual que el original.");
                if (IsProtected(file) && !IsProtected(made))
                    throw new RoostException($"La copia no cuadra: {relative} ha perdido sus permisos (solo para tu usuario).");
                done += file.Length;
            }
        }
        // Y nada de más (lo copiado es solo lo que había).
        foreach (var (_, relative) in Walk(new DirectoryInfo(work), "", null))
            if (!expected.Contains(relative)) throw new RoostException($"La copia no cuadra: sobra {relative}.");
        progress?.Report(new RootMoveProgress(RootMoveStep.Verifying, total, total, null));
    }

    private static bool SameTarget(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
        || a == b;

    private static bool SameContent(string a, string b)
    {
        using var first = File.OpenRead(a);
        using var second = File.OpenRead(b);
        return SHA256.HashData(first).AsSpan().SequenceEqual(SHA256.HashData(second));
    }

    // Enlaces

    /// <summary>
    /// A dónde apunta la copia de un enlace: lo de dentro de la raíz vieja, a la nueva. Una carpeta
    /// va como unión, que lleva siempre la ruta completa (en Windows; en Linux y macOS, un enlace
    /// simbólico relativo sigue relativo, que vale igual en la copia porque está en el mismo sitio).
    /// </summary>
    private static string TargetFor(FileSystemInfo link, string from, string to)
    {
        var target = link.LinkTarget!;
        if (link is DirectoryInfo && OperatingSystem.IsWindows() && !Path.IsPathFullyQualified(target))
            target = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(link.FullName)!, target));
        return Retarget(target, from, to);
    }

    /// <summary>
    /// Un enlace como el de origen: una carpeta, como unión (no pide privilegios; un simbólico de
    /// carpeta los pediría); un fichero, como simbólico (puede pedir el modo de desarrollador).
    /// </summary>
    private static void CreateLink(FileSystemInfo original, string path, string target)
    {
        try
        {
            if (original is DirectoryInfo)
            {
                if (OperatingSystem.IsWindows()) CreateJunction(path, target);
                else Directory.CreateSymbolicLink(path, target);
            }
            else
            {
                File.CreateSymbolicLink(path, target);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new RoostException($"No se pudo recrear el enlace {original.FullName} -> {target}: {ex.Message}");
        }
    }

    [SupportedOSPlatform("windows")]
    internal static void CreateJunction(string path, string target)
    {
        Directory.CreateDirectory(path);
        var substitute = Encoding.Unicode.GetBytes(@"\??\" + Path.GetFullPath(target));
        var print = Encoding.Unicode.GetBytes(Path.GetFullPath(target));
        // REPARSE_DATA_BUFFER de un punto de montaje: cabecera de 8 bytes y luego los dos nombres con su NUL.
        var data = new byte[8 + substitute.Length + 2 + print.Length + 2];
        BitConverter.TryWriteBytes(data.AsSpan(0, 2), (ushort)0);
        BitConverter.TryWriteBytes(data.AsSpan(2, 2), (ushort)substitute.Length);
        BitConverter.TryWriteBytes(data.AsSpan(4, 2), (ushort)(substitute.Length + 2));
        BitConverter.TryWriteBytes(data.AsSpan(6, 2), (ushort)print.Length);
        substitute.CopyTo(data, 8);
        print.CopyTo(data, 8 + substitute.Length + 2);
        var buffer = new byte[8 + data.Length];
        BitConverter.TryWriteBytes(buffer.AsSpan(0, 4), 0xA0000003u);   // IO_REPARSE_TAG_MOUNT_POINT
        BitConverter.TryWriteBytes(buffer.AsSpan(4, 2), (ushort)data.Length);
        data.CopyTo(buffer, 8);

        using var handle = CreateFileW(path, 0x40000000 /* GENERIC_WRITE */, 0, IntPtr.Zero, 3 /* OPEN_EXISTING */,
            0x02000000 | 0x00200000 /* BACKUP_SEMANTICS | OPEN_REPARSE_POINT */, IntPtr.Zero);
        if (handle.IsInvalid) throw new IOException($"no se puede abrir {path}", Marshal.GetHRForLastWin32Error());
        if (!DeviceIoControl(handle, 0x000900A4 /* FSCTL_SET_REPARSE_POINT */, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            Directory.Delete(path);
            throw new IOException($"no se pudo crear la union (error {error})");
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int inputSize, IntPtr output, int outputSize, out int returned, IntPtr overlapped);

    // ACL (Windows) y modos (Linux y macOS)

    private const UnixFileMode OthersMask = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                                            | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    /// <summary>Windows: una ACL protegida (sin herencia); Linux y macOS: sin ningún permiso para el grupo ni para otros (0600, 0700).</summary>
    private static bool IsProtected(FileSystemInfo entry)
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return (entry.UnixFileMode & OthersMask) == 0;
            return entry is FileInfo file ? file.GetAccessControl().AreAccessRulesProtected : ((DirectoryInfo)entry).GetAccessControl().AreAccessRulesProtected;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Si el original tiene una ACL protegida (sin herencia: la de <see cref="PrivateFile"/>), la copia la
    /// lleva igual. En Linux y macOS, la copia lleva el mismo modo que el original (<c>File.Copy</c> ya lo
    /// conserva; las carpetas nacen con el umask y se igualan aquí).
    /// </summary>
    private static void CopySecurity(FileSystemInfo original, FileSystemInfo copy)
    {
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                var mode = original.UnixFileMode;
                if (copy.UnixFileMode != mode) File.SetUnixFileMode(copy.FullName, mode);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // Un sistema de ficheros sin modos (una /mnt/c de WSL): se queda como nazca.
            }
            return;
        }
        if (!IsProtected(original)) return;
        // Por SDDL en un objeto nuevo: el de GetAccessControl, puesto tal cual, no se escribe (no está «modificado»).
        const System.Security.AccessControl.AccessControlSections access = System.Security.AccessControl.AccessControlSections.Access;
        if (original is FileInfo file)
        {
            var security = new System.Security.AccessControl.FileSecurity();
            security.SetSecurityDescriptorSddlForm(file.GetAccessControl().GetSecurityDescriptorSddlForm(access), access);
            ((FileInfo)copy).SetAccessControl(security);
        }
        else
        {
            var security = new System.Security.AccessControl.DirectorySecurity();
            security.SetSecurityDescriptorSddlForm(((DirectoryInfo)original).GetAccessControl().GetSecurityDescriptorSddlForm(access), access);
            ((DirectoryInfo)copy).SetAccessControl(security);
        }
    }
}
