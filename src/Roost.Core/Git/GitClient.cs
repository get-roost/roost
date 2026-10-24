using Roost.Core.Scanning;

namespace Roost.Core.Git;

/// <summary>Lo que se sabe de un remoto sin descargar nada (<c>ls-remote</c>).</summary>
/// <param name="Reachable">true si el servidor contestó con sus ramas.</param>
/// <param name="NeedsAuth">true si pide credenciales (o el repositorio no existe: no se distingue).</param>
public sealed record RemoteProbe(bool Reachable, bool NeedsAuth, string? DefaultBranch, IReadOnlyList<string> Branches, GitFailure? Failure)
{
    /// <summary>El commit de cada rama del remoto (<see cref="RemoteRefs.Heads"/>); vacío si no contestó.</summary>
    public IReadOnlyDictionary<string, string> Heads { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// git sobre <see cref="ICommandRunner"/>, siempre con el entorno aislado de <see cref="GitEnvironment"/>.
/// Nunca pisa trabajo del usuario: el pull es solo fast-forward y, como cambiar de rama, solo con el
/// árbol limpio; nada de <c>stash</c> ni <c>reset</c> por su cuenta. Lo que falla sale como
/// <see cref="GitException"/>, ya clasificado.
/// </summary>
public sealed class GitClient(string executable, ICommandRunner runner, GitEnvironment environment, GitAccess? access = null)
{
    /// <summary>Lo que se espera a un servidor para contestar a <c>ls-remote</c>.</summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Lo que se espera a las órdenes locales (status, log, config).</summary>
    public static readonly TimeSpan LocalTimeout = TimeSpan.FromSeconds(60);

    public string Executable => executable;

    /// <summary>Cómo entra en el remoto (el helper de credenciales, el ssh con su clave).</summary>
    public GitAccess Access => access ?? GitAccess.None;

    /// <summary>El mismo git, con otro acceso: el de cada sitio, o el que se prueba al clonar.</summary>
    public GitClient With(GitAccess other) => new(executable, runner, environment, other);

    /// <summary>true si la carpeta tiene un <c>.git</c> (carpeta, o fichero en un worktree).</summary>
    public static bool IsRepository(string folder) =>
        Directory.Exists(Path.Combine(folder, ".git")) || File.Exists(Path.Combine(folder, ".git"));

    /// <summary>La primera línea de <c>git --version</c>.</summary>
    public async Task<string> VersionAsync(CancellationToken cancellation)
    {
        var result = await RunAsync(null, LocalTimeout, null, cancellation, "--version");
        return GitOutput.Lines(result.Stdout).FirstOrDefault()?.Trim() ?? "";
    }

    /// <summary>
    /// Pregunta al remoto por su rama por defecto y sus ramas, sin descargar nada. No lanza por lo que
    /// conteste el servidor: lo cuenta en <see cref="RemoteProbe"/>.
    /// </summary>
    public async Task<RemoteProbe> ProbeAsync(GitUrl url, IProgress<string>? lines, CancellationToken cancellation)
    {
        var result = await runner.RunAsync(Spec(null, ProbeTimeout, "ls-remote", "--symref", "--", url.Normalized, "HEAD", "refs/heads/*"), lines, cancellation);
        if (result.TimedOut)
            return new RemoteProbe(false, false, null, [], GitFailure.TimedOut(ProbeTimeout, url));
        if (!result.Succeeded)
        {
            var failure = GitFailure.Classify(result.ExitCode, result.Stderr, url, access: Access);
            return new RemoteProbe(false, failure.Kind is GitFailureKind.AuthRequired or GitFailureKind.TokenRejected or GitFailureKind.TokenForbidden or GitFailureKind.PasswordNotAccepted or GitFailureKind.KeyRejected, null, [], failure);
        }

        var refs = GitOutput.ParseLsRemote(result.Stdout);
        return new RemoteProbe(true, false, refs.DefaultBranch, refs.Branches, null) { Heads = refs.Heads };
    }

    /// <summary>
    /// Clona en <paramref name="targetDir"/> (que no debe existir, o estar vacío) la rama
    /// <paramref name="branch"/>, o la por defecto. Trae todas las ramas, para poder cambiar después.
    /// Sin límite de tiempo: se corta con el token. El progreso de <c>--progress</c> sale en porcentaje.
    /// Deja <c>core.autocrlf=false</c> en el <c>.git/config</c> del clon: así el git del usuario (que
    /// suele llevar <c>autocrlf=true</c> en Windows) escribe los mismos bytes que el de Roost y un
    /// <c>git checkout</c> suyo no deja el árbol «con cambios» para Roost.
    /// </summary>
    public async Task CloneAsync(GitUrl url, string? branch, string targetDir, IProgress<CloneProgress>? progress, IProgress<string>? lines, CancellationToken cancellation)
    {
        var args = new List<string> { "clone", "--progress", "--config", "core.autocrlf=false" };
        if (branch is not null)
        {
            ValidateBranchName(branch);
            args.AddRange(["--branch", branch]);
        }
        args.AddRange(["--", url.Normalized, targetDir]);

        var tee = progress is null ? lines : new LineTee(lines, line =>
        {
            if (GitOutput.ParseProgress(line) is { } p) progress.Report(p);
        });
        var result = await runner.RunAsync(Spec(null, null, args.ToArray()), tee, cancellation);
        if (!result.Succeeded)
            throw new GitException(GitFailure.Classify(result.ExitCode, result.Stderr, url, targetDir, Access));
    }

    public async Task<RepoStatus> StatusAsync(string folder, CancellationToken cancellation)
    {
        var result = await RunAsync(folder, LocalTimeout, null, cancellation, "status", "--porcelain=v2", "--branch");
        return GitOutput.ParseStatusV2(result.Stdout);
    }

    /// <summary>El último commit de la rama actual, o null en un repositorio sin commits.</summary>
    public async Task<GitCommit?> HeadAsync(string folder, CancellationToken cancellation) =>
        (await LogAsync(folder, 1, cancellation)).FirstOrDefault();

    public async Task<IReadOnlyList<GitCommit>> LogAsync(string folder, int count, CancellationToken cancellation)
    {
        var result = await runner.RunAsync(Spec(folder, LocalTimeout, "log", $"-{Math.Max(1, count)}", "--format=" + GitOutput.LogFormat), null, cancellation);
        // Sin commits todavía, log falla con «does not have any commits yet»: no es un error nuestro.
        if (!result.Succeeded && result.Stderr.Contains("does not have any commits", StringComparison.OrdinalIgnoreCase)) return [];
        Ensure(result, folder: folder);
        return GitOutput.ParseLog(result.Stdout);
    }

    /// <summary>Trae lo nuevo del remoto (y quita las ramas remotas que ya no existen). No toca el checkout.</summary>
    public Task FetchAsync(string folder, IProgress<string>? lines, CancellationToken cancellation) =>
        RunAsync(folder, null, lines, cancellation, "fetch", "--progress", "--prune", "origin");

    /// <summary>
    /// Actualiza la rama actual solo si es un fast-forward y solo con el árbol limpio; si no, lanza
    /// <see cref="GitException"/> (<see cref="GitFailureKind.LocalChanges"/> o <see cref="GitFailureKind.Diverged"/>).
    /// </summary>
    public async Task<PullResult> PullFastForwardAsync(string folder, IProgress<string>? lines, CancellationToken cancellation)
    {
        var status = await StatusAsync(folder, cancellation);
        if (!status.IsClean) throw new GitException(GitFailure.LocalChanges(folder, status.Changed));

        var before = (await HeadAsync(folder, cancellation))?.Sha ?? "";
        await RunAsync(folder, null, lines, cancellation, "pull", "--ff-only", "--progress");
        var after = (await HeadAsync(folder, cancellation))?.Sha ?? "";
        if (before == after || before.Length == 0) return new PullResult(before, after, 0);

        var diff = await RunAsync(folder, LocalTimeout, null, cancellation, "diff", "--name-only", before, after);
        return new PullResult(before, after, GitOutput.Lines(diff.Stdout).Count());
    }

    /// <summary>
    /// Cambia a <paramref name="branch"/> (local, o la crea siguiendo a la del remoto), solo con el árbol
    /// limpio. Antes hace fetch, para que una rama recién creada en el servidor exista aquí.
    /// </summary>
    public async Task CheckoutAsync(string folder, string branch, IProgress<string>? lines, CancellationToken cancellation)
    {
        ValidateBranchName(branch);
        var status = await StatusAsync(folder, cancellation);
        if (!status.IsClean) throw new GitException(GitFailure.LocalChanges(folder, status.Changed));

        await FetchAsync(folder, lines, cancellation);
        var result = await runner.RunAsync(Spec(folder, null, "checkout", branch), lines, cancellation);
        if (result.Succeeded) return;

        var failure = GitFailure.Classify(result.ExitCode, result.Stderr, null, folder, Access);
        if (failure.Kind == GitFailureKind.BranchNotFound)
            failure = GitFailure.BranchNotFound(Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar)), branch, await RemoteBranchesAsync(folder, cancellation));
        throw new GitException(failure);
    }

    /// <summary>Las ramas de <c>origin</c> que ya se han traído (tras un fetch, las del servidor).</summary>
    public async Task<IReadOnlyList<string>> RemoteBranchesAsync(string folder, CancellationToken cancellation)
    {
        var result = await RunAsync(folder, LocalTimeout, null, cancellation, "branch", "-r", "--format=%(refname:short)");
        return GitOutput.ParseRemoteBranches(result.Stdout);
    }

    /// <summary><c>remote.origin.url</c>, o null si el repositorio no tiene <c>origin</c>.</summary>
    public async Task<string?> RemoteUrlAsync(string folder, CancellationToken cancellation)
    {
        var result = await runner.RunAsync(Spec(folder, LocalTimeout, "config", "--get", "remote.origin.url"), null, cancellation);
        if (result.ExitCode == 1 && !result.TimedOut) return null;   // la clave no existe
        Ensure(result, folder: folder);
        return GitOutput.Lines(result.Stdout).FirstOrDefault()?.Trim();
    }

    /// <summary>true si ese commit ya está en el repositorio local (<c>cat-file -e</c>: sin red).</summary>
    public async Task<bool> HasCommitAsync(string folder, string sha, CancellationToken cancellation)
    {
        ValidateSha(sha);
        var result = await runner.RunAsync(Spec(folder, LocalTimeout, "cat-file", "-e", sha + "^{commit}"), null, cancellation);
        if (result.TimedOut) throw new GitException(GitFailure.TimedOut(LocalTimeout));
        return result.Succeeded;
    }

    /// <summary>Los commits de <paramref name="to"/> que no están en <paramref name="from"/> (<c>rev-list --count from..to</c>, sin red).</summary>
    public async Task<int> CountCommitsAsync(string folder, string from, string to, CancellationToken cancellation)
    {
        ValidateSha(from);
        ValidateSha(to);
        var result = await RunAsync(folder, LocalTimeout, null, cancellation, "rev-list", "--count", $"{from}..{to}");
        return int.TryParse(GitOutput.Lines(result.Stdout).FirstOrDefault()?.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var count) ? count : 0;
    }

    /// <summary>La rama actual, o null si HEAD está suelto (detached).</summary>
    public async Task<string?> CurrentBranchAsync(string folder, CancellationToken cancellation)
    {
        var result = await RunAsync(folder, LocalTimeout, null, cancellation, "rev-parse", "--abbrev-ref", "HEAD");
        var name = GitOutput.Lines(result.Stdout).FirstOrDefault()?.Trim();
        return name is null or "HEAD" ? null : name;
    }

    private CommandSpec Spec(string? folder, TimeSpan? timeout, params string[] args) =>
        new(executable, args, folder, environment.Build(Access.Config, Access.SshCommand), timeout);

    private async Task<CommandResult> RunAsync(string? folder, TimeSpan? timeout, IProgress<string>? lines, CancellationToken cancellation, params string[] args)
    {
        var result = await runner.RunAsync(Spec(folder, timeout, args), lines, cancellation);
        Ensure(result, folder: folder, timeout: timeout);
        return result;
    }

    private void Ensure(CommandResult result, string? folder = null, TimeSpan? timeout = null)
    {
        if (result.TimedOut) throw new GitException(GitFailure.TimedOut(timeout ?? LocalTimeout));
        if (!result.Succeeded) throw new GitException(GitFailure.Classify(result.ExitCode, result.Stderr, null, folder, Access));
    }

    /// <summary>Un sha (lo que da <c>ls-remote</c> o <c>status</c>): nunca algo que git pudiera leer como una opción o un rango.</summary>
    private static void ValidateSha(string sha)
    {
        if (!GitOutput.ShaPattern().IsMatch(sha))
            throw new RoostException($"'{sha}' no vale como commit.");
    }

    private static void ValidateBranchName(string branch)
    {
        if (string.IsNullOrWhiteSpace(branch) || branch.StartsWith('-') || branch.Any(char.IsWhiteSpace) || branch.Contains(".."))
            throw new RoostException($"'{branch}' no vale como nombre de rama.");
    }

    /// <summary>Reparte cada línea entre quien la quiere para el log y quien la quiere para el progreso.</summary>
    private sealed class LineTee(IProgress<string>? inner, Action<string> also) : IProgress<string>
    {
        public void Report(string value)
        {
            inner?.Report(value);
            also(value);
        }
    }
}
