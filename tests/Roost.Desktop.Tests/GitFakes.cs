using Roost.Core.Git;
using Roost.Core.Platform;
using Roost.Core.Scanning;
using Roost.Core.Sites;
using Roost.Desktop.Services;
using Roost.Desktop.ViewModels;

namespace Roost.Desktop.Tests;

/// <summary>
/// Git-app: un <see cref="ICommandRunner"/> guionizado por prefijo de argumentos (como el
/// FakeCommandRunner de Core): a cada prefijo, su código, su stdout y su stderr, unas líneas de
/// progreso que se dan antes y, si hace falta, una espera (para capturar un clonado a medias) y algo
/// que hacer con la orden (crear los ficheros de un clone). Gana el prefijo más largo y, a igualdad,
/// el último registrado. Nada se ejecuta: ni git, ni ssh, ni ssh-keygen.
/// </summary>
internal sealed class GitFakeRunner : ICommandRunner
{
    private sealed record Step(string Prefix, int ExitCode, string Stdout, string Stderr, Action<CommandSpec>? OnRun, TaskCompletionSource? Hold, string[] Lines);

    private readonly List<Step> _steps = [];
    private readonly List<CommandSpec> _calls = [];
    private readonly Lock _gate = new();   // la app lanza órdenes desde dos hilos a la vez (poner al día y mirar novedades)

    /// <summary>Las órdenes recibidas hasta ahora (una copia).</summary>
    public IReadOnlyList<CommandSpec> Calls
    {
        get { lock (_gate) return _calls.ToList(); }
    }

    public IEnumerable<string> Commands => Calls.Select(c => string.Join(' ', c.Arguments));

    public GitFakeRunner On(string prefix, int exitCode = 0, string stdout = "", string stderr = "", Action<CommandSpec>? onRun = null,
        TaskCompletionSource? hold = null, params string[] lines)
    {
        lock (_gate) _steps.Add(new Step(prefix, exitCode, stdout, stderr, onRun, hold, lines));
        return this;
    }

    public async Task<CommandResult> RunAsync(CommandSpec spec, IProgress<string>? lines, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var joined = string.Join(' ', spec.Arguments);
        Step step;
        lock (_gate)
        {
            _calls.Add(spec);
            var matches = _steps.Where(s => joined.StartsWith(s.Prefix, StringComparison.Ordinal)).ToList();
            if (matches.Count == 0) throw new InvalidOperationException($"GitFakeRunner sin guion para: {spec.Exe} {joined}");
            var longest = matches.Max(s => s.Prefix.Length);
            step = matches.Last(s => s.Prefix.Length == longest);
        }

        foreach (var line in step.Lines) lines?.Report(line);
        if (step.Hold is { } hold)
        {
            var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellation.Register(() => canceled.TrySetResult());
            await Task.WhenAny(hold.Task, canceled.Task);
            cancellation.ThrowIfCancellationRequested();
        }
        step.OnRun?.Invoke(spec);
        if (lines is not null)
        {
            foreach (var line in step.Stdout.Split('\n')) if (line.Length > 0) lines.Report(line.TrimEnd('\r'));
            foreach (var line in step.Stderr.Split('\n')) if (line.Length > 0) lines.Report(line.TrimEnd('\r'));
        }
        return new CommandResult(step.ExitCode, step.Stdout, step.Stderr, false);
    }
}

/// <summary>Los tokens de los tests: en memoria y sin distinguir mayúsculas en el target, como el Administrador de credenciales. Nunca el de verdad.</summary>
internal sealed class MemoryCredentialStore : ICredentialStore
{
    private readonly Dictionary<string, StoredCredential> _items = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<StoredCredential> Items => _items.Values;

    public StoredCredential? Read(string target) => _items.GetValueOrDefault(target);

    public void Write(StoredCredential credential) => _items[credential.Target] = credential;

    public bool Delete(string target) => _items.Remove(target);

    public IReadOnlyList<CredentialInfo> List(string prefix) =>
        _items.Values.Where(c => c.Target.StartsWith(prefix, StringComparison.Ordinal))
            .Select(c => new CredentialInfo(c.Target, c.UserName, null))
            .OrderBy(c => c.Target, StringComparer.OrdinalIgnoreCase)
            .ToList();
}

/// <summary>Lo de git que necesitan las pantallas: el runner, los tokens, lo que se abriría en el navegador.</summary>
internal sealed class GitFixture
{
    public required GitFakeRunner Runner { get; init; }
    public required MemoryCredentialStore Store { get; init; }
    public required GitContext Context { get; init; }
    public List<string> Opened { get; } = [];

    // Salidas de git (de Hello-World de octocat, como las de los tests de Core).
    public const string Head = "7fd1a60b01f91b314f59955a4e4d4e80d8edf11d";
    public const string Next = "b3cbd5bbd7e81436d2eee04537ea2b4c0cad4cdf";
    public const string Patch = "b1b3f9723831141a31a1a7252a213e216ea76e56";

    public static string LsRemote(string master = Head) =>
        $"ref: refs/heads/master\tHEAD\n{master}\tHEAD\n{master}\trefs/heads/master\n{Patch}\trefs/heads/octocat-patch-1\n{Next}\trefs/heads/test\n";

    public static string Status(string oid = Head, int ahead = 0, int behind = 0, params string[] changed) =>
        $"# branch.oid {oid}\n# branch.head master\n# branch.upstream origin/master\n# branch.ab +{ahead} -{behind}\n"
        + string.Concat(changed.Select(f => $"1 .M N... 100644 100644 100644 aaaa aaaa {f}\n"));

    public static string Log(string sha, string subject) => $"{sha}\u001f{sha[..7]}\u001fThe Octocat\u001f2012-03-06T23:06:50-08:00\u001f{subject}\n";

    public const string RemoteBranches = "origin/HEAD\norigin/master\norigin/octocat-patch-1\norigin/test\n";

    // Una clave ed25519 de verdad (la de servidor de GitHub: vale para calcular huellas; aquí nadie la usa para nada).
    public const string Ed25519 = "AAAAC3NzaC1lZDI1NTE5AAAAIOMqqnkVzrm0SdG6UOoqKLsabgH5C9okWi0dh2l9GKJl";

    // La de servidor de GitLab, para hacer de la de un servidor propio.
    public const string OtherEd25519 = "AAAAC3NzaC1lZDI1NTE5AAAAIAfuCHKVTjquxvt6CM6tdG4SLp1Btn/nOeHHE5UOzRdf";

    /// <summary>Lo que hace un clone de verdad: un .git y los ficheros del proyecto en la carpeta destino.</summary>
    public static void FakeClone(CommandSpec spec)
    {
        var target = spec.Arguments[^1];
        Directory.CreateDirectory(Path.Combine(target, ".git", "objects"));
        File.WriteAllText(Path.Combine(target, "README"), "Hello World!");
    }

    /// <summary>Lo que hace ssh-keygen: la clave y su .pub en el fichero que se le pide.</summary>
    public static void FakeKeygen(CommandSpec spec)
    {
        var file = spec.Arguments[^1];
        var comment = spec.Arguments[spec.Arguments.ToList().IndexOf("-C") + 1];
        File.WriteAllText(file, "-----BEGIN OPENSSH PRIVATE KEY-----\nde pega\n-----END OPENSSH PRIVATE KEY-----\n");
        File.WriteAllText(file + ".pub", $"ssh-ed25519 {Ed25519} {comment}\n");
    }

    /// <summary>
    /// Pone el git de pega en la app: un MinGit «instalado» (un git.exe vacío), un OpenSSH que no
    /// existe (nada lo ejecuta), una carpeta .ssh de usuario de pega, el runner guionizado y los
    /// tokens en memoria; ni el navegador ni el stack de verdad.
    /// </summary>
    public static GitFixture Use(MainViewModel main, FakeRoost fake)
    {
        var exe = Path.Combine(fake.Paths.GitBin, GitCatalog.Windows.Version, "cmd", "git.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllText(exe, "");
        var userSsh = Path.Combine(fake.Paths.Root, "usuario-ssh");
        Directory.CreateDirectory(userSsh);
        File.WriteAllText(Path.Combine(userSsh, "id_ed25519"), "de pega");
        File.WriteAllText(Path.Combine(userSsh, "id_ed25519.pub"), "ssh-ed25519 de pega");

        var runner = new GitFakeRunner();
        var store = new MemoryCredentialStore();
        var context = new GitContext(fake.Paths, runner, store, new OpenSshTools(Path.Combine(fake.Paths.Root, "openssh-de-pega")), userSsh, () => "parado");
        var fixture = new GitFixture { Runner = runner, Store = store, Context = context };
        main.Sites.Git = context;
        main.Sites.OpenBrowser = fixture.Opened.Add;
        main.Sites.ApplyForClone = () => Task.FromResult(true);
        return fixture;
    }

    /// <summary>Un sitio que viene de git, ya clonado (una carpeta con .git) y registrado.</summary>
    public static Site Register(FakeRoost fake, string name, SiteGit git)
    {
        var folder = fake.Project(name, ".git/", "README");
        var site = new Site { Name = name, Folder = folder, Git = git };
        new SiteRegistry(fake.Paths).Upsert(site);
        return site;
    }

    public static SiteGit HelloGit(string access = SiteGit.AccessNone, string? keyName = null, string? target = null, string remote = "https://github.com/octocat/Hello-World.git") => new()
    {
        Remote = remote,
        Branch = "master",
        Provider = GitProvider.GitHub,
        Access = access,
        KeyName = keyName,
        CredentialTarget = target,
        LastCommit = new GitCommit(Head, Head[..7], "The Octocat", new DateTimeOffset(2012, 3, 6, 23, 6, 50, TimeSpan.FromHours(-8)), "Merge pull request #6 from Spaceghost/patch-1"),
    };
}
