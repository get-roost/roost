using Roost.Core.Git;

namespace Roost.Core.Sites;

/// <summary>
/// Lo que dijo la comprobación de novedades de un sitio: <c>ls-remote</c> de su remoto (sin descargar
/// nada ni tocar la carpeta) comparado con el checkout.
/// </summary>
/// <param name="Branch">La rama del checkout (o la que recuerda Roost si HEAD está suelto).</param>
/// <param name="LocalSha">El commit del checkout; null en un repositorio sin commits.</param>
/// <param name="RemoteSha">El commit de esa rama en el remoto; null si el remoto no la tiene o no contestó.</param>
/// <param name="Behind">
/// Los commits del remoto que no tiene el checkout, si se pueden contar sin red (el commit del remoto
/// ya está aquí, porque se trajo antes); null si no se pueden contar.
/// </param>
/// <param name="Branches">Las ramas del remoto.</param>
/// <param name="Failure">Por qué no contestó el remoto (sin acceso, sin red…); null si contestó.</param>
public sealed record SiteUpdateCheck(
    string Site, string? Branch, string? LocalSha, string? RemoteSha, int? Behind,
    IReadOnlyList<string> Branches, GitFailure? Failure)
{
    public DateTimeOffset When { get; init; } = DateTimeOffset.Now;

    /// <summary>El estado del checkout que se leyó para compararlo (cambios locales incluidos); null si el remoto no contestó.</summary>
    public RepoStatus? Status { get; init; }

    public bool Ok => Failure is null;

    /// <summary>La rama del remoto está en el mismo commit que el checkout.</summary>
    public bool UpToDate => Ok && RemoteSha is not null && RemoteSha == LocalSha;

    /// <summary>
    /// El remoto tiene commits que el checkout no: los contados (<see cref="Behind"/> &gt; 0) o, si su
    /// commit no está aquí todavía, sin contar. Con commits propios por delante y nada nuevo, false.
    /// </summary>
    public bool HasNews => Ok && RemoteSha is not null && RemoteSha != LocalSha && Behind is not 0;
}

public sealed partial class SiteManager
{
    /// <summary>
    /// ¿Hay commits nuevos en el remoto? Pregunta con <c>ls-remote</c> y el acceso del sitio (como
    /// <see cref="ProbeSiteAsync"/>: nunca descarga nada ni toca la carpeta) y compara el commit de su
    /// rama con el del checkout. Si ese commit ya está en el repositorio local (lo trajo antes un fetch,
    /// o el git del usuario), cuenta cuántos faltan. Lo que conteste el servidor va en
    /// <see cref="SiteUpdateCheck.Failure"/>; lanza <see cref="RoostException"/> si el sitio no tiene
    /// git o le falta la clave o el OpenSSH.
    /// </summary>
    public async Task<SiteUpdateCheck> CheckUpdatesAsync(string name, CancellationToken cancellation)
    {
        var (site, info) = RequireGit(name);
        RemoteProbe probe;
        try
        {
            probe = await ProbeSiteAsync(site.Name, cancellation);
        }
        catch (GitException ex)
        {
            return new SiteUpdateCheck(site.Name, info.Branch, info.LastCommit?.Sha, null, null, [], ex.Failure);
        }
        if (!probe.Reachable)
            return new SiteUpdateCheck(site.Name, info.Branch, info.LastCommit?.Sha, null, null, [], probe.Failure ?? GitFailure.Classify(1, "", null));

        var status = await Client.StatusAsync(site.Folder, cancellation);
        var branch = status.Branch ?? info.Branch;
        var local = status.Oid?.ToLowerInvariant();
        var remote = probe.Heads.GetValueOrDefault(branch);
        int? behind = null;
        if (remote is null || remote == local) behind = 0;
        else if (local is not null && await Client.HasCommitAsync(site.Folder, remote, cancellation))
            behind = await Client.CountCommitsAsync(site.Folder, local, remote, cancellation);
        return new SiteUpdateCheck(site.Name, branch, local, remote, behind, probe.Branches, null) { Status = status };
    }
}
