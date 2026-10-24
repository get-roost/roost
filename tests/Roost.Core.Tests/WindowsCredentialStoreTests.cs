using Roost.Core.Git;

namespace Roost.Core.Tests;

/// <summary>
/// Solo con <c>ROOST_TEST_CREDMAN=1</c> en Windows: el Administrador de credenciales de verdad, y
/// solo con targets que empiezan por <c>Roost-test:</c>, que se borran al acabar pase lo que pase.
/// Sin la variable, se omite (dotnet test nunca lo toca).
/// </summary>
public sealed class CredManFactAttribute : FactAttribute
{
    public const string Variable = "ROOST_TEST_CREDMAN";

    public CredManFactAttribute()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable(Variable) != "1")
            Skip = $"Define {Variable}=1 para probar el Administrador de credenciales de Windows (con targets Roost-test:).";
    }
}

public class WindowsCredentialStoreTests
{
    [CredManFact]
    public void EscribeLeeListaYBorra_ConTargetsDePrueba()
    {
        if (!OperatingSystem.IsWindows()) return;
        var store = new WindowsCredentialStore();
        var prefix = $"Roost-test:unit-{Guid.NewGuid():N}:";
        var credentials = new GitCredentials(store, prefix);
        var location = CredentialLocation.Create("https", "github.com", "acme/tienda");
        try
        {
            var target = credentials.Save(location, "TOKEN-DE-PEGA-ñ-€", "ana");

            var read = store.Read(target)!;
            Assert.Equal("TOKEN-DE-PEGA-ñ-€", read.Secret);
            Assert.Equal("ana", read.UserName);
            Assert.Equal(target, credentials.Find(GitUrl.Parse("https://github.com/acme/tienda.git"))!.Target);
            var listed = Assert.Single(credentials.List());
            Assert.Equal(target, listed.Target);
            Assert.NotNull(listed.LastWritten);

            Assert.True(store.Delete(target));
            Assert.False(store.Delete(target));
            Assert.Null(store.Read(target));
        }
        finally
        {
            foreach (var left in store.List(prefix)) store.Delete(left.Target);
            Assert.Empty(store.List(prefix));
        }
    }
}
