using Roost.Core.Git;
using Roost.Core.Platform;
using Roost.Core.Scanning;
using Roost.Core.Sites;

namespace Roost.Core.Tests;

/// <summary>
/// Solo con <c>ROOST_TEST_GIT</c>: <c>1</c> usa el git del PATH, y cualquier otro valor es la ruta de
/// un git.exe (el MinGit de Roost, por ejemplo). Sin la variable, se omite.
/// </summary>
public sealed class GitFactAttribute : FactAttribute
{
    public const string Variable = "ROOST_TEST_GIT";

    public GitFactAttribute()
    {
        if (Executable() is null) Skip = $"Define {Variable}=1 (el git del PATH) o la ruta de un git.exe para la integracion con git real.";
    }

    public static string? Executable()
    {
        var value = Environment.GetEnvironmentVariable(Variable);
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.Trim() == "1") return PathProbe.ResolveActive(["git.exe", "git"]);
        return File.Exists(value) ? value : null;
    }
}

/// <summary>
/// Integracion con un git REAL y sin red: un remoto con git init --bare en una carpeta temporal,
/// clonado por su ruta, pull fast-forward, cambios locales que bloquean, cambio de rama e historia
/// divergente. Es lo mismo que la prueba en real de la CLI, automatizado.
/// </summary>
public class GitIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "roost-gitreal-" + Guid.NewGuid().ToString("N"));
    private RoostPaths Paths => new(_root, Path.Combine(_root, "cache"));
    private string Remote => Path.Combine(_root, "remoto", "tienda.git");
    private string Work => Path.Combine(_root, "trabajo");

    private readonly string? _git = GitFactAttribute.Executable();

    private SiteManager Sites => new(Paths, new GitClient(_git!, CommandRunner.Default, new GitEnvironment(Paths)));

    /// <summary>git con el entorno aislado de Roost mas un autor, para preparar el remoto como lo haria el usuario.</summary>
    private async Task<string> GitAsync(string cwd, params string[] args)
    {
        var env = new Dictionary<string, string?>(new GitEnvironment(Paths).Build())
        {
            ["GIT_AUTHOR_NAME"] = "Ana Perez", ["GIT_AUTHOR_EMAIL"] = "ana@example.test",
            ["GIT_COMMITTER_NAME"] = "Ana Perez", ["GIT_COMMITTER_EMAIL"] = "ana@example.test",
        };
        var result = await CommandRunner.Default.RunAsync(new CommandSpec(_git!, args, cwd, env, TimeSpan.FromSeconds(60)), null, CancellationToken.None);
        Assert.True(result.Succeeded, $"git {string.Join(' ', args)} fallo: {result.Stderr}");
        return result.Stdout;
    }

    private async Task PrepareRemoteAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Remote)!);
        await GitAsync(_root, "init", "--bare", "--initial-branch=main", Remote);
        await GitAsync(_root, "clone", "--quiet", Remote, Work);
        File.WriteAllText(Path.Combine(Work, "index.php"), "<?php echo 'v1';\n");
        Directory.CreateDirectory(Path.Combine(Work, "public"));
        File.WriteAllText(Path.Combine(Work, "public", "robots.txt"), "ok\n");
        await GitAsync(Work, "add", ".");
        await GitAsync(Work, "commit", "--quiet", "-m", "Primer commit");
        await GitAsync(Work, "push", "--quiet", "origin", "main");
        await GitAsync(Work, "checkout", "--quiet", "-b", "feature/checkout");
        File.WriteAllText(Path.Combine(Work, "checkout.txt"), "checkout\n");
        await GitAsync(Work, "add", ".");
        await GitAsync(Work, "commit", "--quiet", "-m", "Ajusta el checkout");
        await GitAsync(Work, "push", "--quiet", "origin", "feature/checkout");
        await GitAsync(Work, "checkout", "--quiet", "main");
    }

    private async Task CommitOnRemoteAsync(string file, string content, string message)
    {
        File.WriteAllText(Path.Combine(Work, file), content);
        await GitAsync(Work, "add", ".");
        await GitAsync(Work, "commit", "--quiet", "-m", message);
        await GitAsync(Work, "push", "--quiet", "origin", "main");
    }

    [GitFact]
    public async Task ClonarPullCheckoutYDivergencia_ConUnGitReal()
    {
        await PrepareRemoteAsync();
        var url = GitUrl.Parse(Remote);
        Assert.True(url.IsLocal);

        // Comprobar: la rama por defecto y las ramas, sin descargar.
        var probe = await Sites.ProbeAsync(url, CancellationToken.None);
        Assert.True(probe.Reachable);
        Assert.Equal("main", probe.DefaultBranch);
        Assert.Equal(["feature/checkout", "main"], probe.Branches);

        // Clonar como sitio.
        var progress = new List<CloneProgress>();
        var cloned = await Sites.CloneAsync(new CloneRequest(url), new SyncProgress<CloneProgress>(progress.Add), null, CancellationToken.None);
        var site = cloned.Change.Site;
        Assert.Equal("tienda", site.Name);
        Assert.Equal("public", site.Root);
        Assert.Equal("main", cloned.Branch);
        Assert.Equal("Primer commit", cloned.Head?.Subject);
        Assert.True(GitClient.IsRepository(site.Folder));
        Assert.Empty(Directory.GetDirectories(Paths.Www, ".*"));

        // Al dia.
        var same = await Sites.PullAsync(site.Name, null, CancellationToken.None);
        Assert.False(same.Changed);

        // El remoto avanza: fast-forward.
        await CommitOnRemoteAsync("index.php", "<?php echo 'v2';\n", "Version 2");
        var behind = await Sites.GitStatusAsync(site.Name, fetch: true, CancellationToken.None);
        Assert.Equal(1, behind.Status.Behind);
        var pulled = await Sites.PullAsync(site.Name, null, CancellationToken.None);
        Assert.True(pulled.Changed);
        Assert.Equal(1, pulled.ChangedFiles);
        Assert.Equal("Version 2", Sites.Find(site.Name)!.Git!.LastCommit?.Subject);
        Assert.Contains("v2", File.ReadAllText(Path.Combine(site.Folder, "index.php")));

        // Un cambio local bloquea el pull y no se toca nada.
        File.WriteAllText(Path.Combine(site.Folder, "index.php"), "<?php echo 'local';\n");
        await CommitOnRemoteAsync("index.php", "<?php echo 'v3';\n", "Version 3");
        var blocked = await Assert.ThrowsAsync<GitException>(() => Sites.PullAsync(site.Name, null, CancellationToken.None));
        Assert.Equal(GitFailureKind.LocalChanges, blocked.Failure.Kind);
        Assert.Contains("index.php", blocked.Message);
        Assert.Contains("local", File.ReadAllText(Path.Combine(site.Folder, "index.php")));

        // Tampoco se cambia de rama con cambios.
        var blockedCheckout = await Assert.ThrowsAsync<GitException>(() => Sites.CheckoutAsync(site.Name, "feature/checkout", null, CancellationToken.None));
        Assert.Equal(GitFailureKind.LocalChanges, blockedCheckout.Failure.Kind);

        // Deshecho el cambio: cambiar de rama (una que solo esta en el remoto) y volver.
        await GitAsync(site.Folder, "checkout", "--quiet", "--", "index.php");
        var feature = await Sites.CheckoutAsync(site.Name, "feature/checkout", null, CancellationToken.None);
        Assert.Equal("feature/checkout", feature.Git?.Branch);
        Assert.Equal("Ajusta el checkout", feature.Git?.LastCommit?.Subject);
        Assert.True(File.Exists(Path.Combine(site.Folder, "checkout.txt")));
        var missing = await Assert.ThrowsAsync<GitException>(() => Sites.CheckoutAsync(site.Name, "nope", null, CancellationToken.None));
        Assert.Equal(GitFailureKind.BranchNotFound, missing.Failure.Kind);
        Assert.Contains("feature/checkout, main", missing.Message);
        await Sites.CheckoutAsync(site.Name, "main", null, CancellationToken.None);

        // Historia divergente: un commit local que el remoto no tiene.
        File.WriteAllText(Path.Combine(site.Folder, "mio.txt"), "mio\n");
        await GitAsync(site.Folder, "add", ".");
        await GitAsync(site.Folder, "commit", "--quiet", "-m", "Cambio local");
        var diverged = await Assert.ThrowsAsync<GitException>(() => Sites.PullAsync(site.Name, null, CancellationToken.None));
        Assert.Equal(GitFailureKind.Diverged, diverged.Failure.Kind);

        // Adoptar: el remoto y la rama de un .git que ya existe.
        var legacy = Sites.Add("legacy", Work).Site;
        var adopted = await Sites.AdoptGitAsync(legacy.Name, CancellationToken.None);
        Assert.Equal(url.Normalized, adopted.Git?.Remote);
        Assert.Equal("main", adopted.Git?.Branch);

        // El historial lo cuenta todo, y el log existe.
        var history = Sites.History.Load(site.Name);
        Assert.Contains(history, h => h.Kind == GitHistory.Clone && h.Ok);
        Assert.Contains(history, h => h.Kind == GitHistory.Pull && h.Ok && h.From != h.To);
        Assert.Contains(history, h => h.Kind == GitHistory.Pull && !h.Ok);
        Assert.Contains(history, h => h.Kind == GitHistory.Checkout && h.Ok);
        Assert.True(File.Exists(Sites.Log.FilePath(site.Name)));
    }

    public void Dispose()
    {
        Install.InstallFiles.TryDeleteDirectory(_root);
    }

    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
