using Roost.Core.Git;

namespace Roost.Core.Tests;

/// <summary>
/// El almacen de credenciales de los tests: un diccionario en memoria que, como el Administrador de
/// credenciales de Windows, no distingue mayusculas en el target. Ningun test toca el de verdad.
/// </summary>
public sealed class InMemoryCredentialStore : ICredentialStore
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
