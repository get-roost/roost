using Roost.Core;
using Roost.Core.Audit;
using Roost.Core.Config;
using Roost.Core.Platform;
using Roost.Core.Scanning;
using Roost.Core.Services;
using Spectre.Console.Cli;

namespace Roost.Extensibility.Tests;

/// <summary>
/// Testea los puntos de enganche: el cargador con una extensión de muestra compilada aparte (que
/// se copia a una carpeta bin/extensions temporal, como en una instalación de verdad) y que lo
/// que falla dentro de una extensión no tumba Roost.
/// </summary>
public class ExtensionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "roost-ext-" + Guid.NewGuid().ToString("N"));
    private RoostPaths Paths => new(_root, Path.Combine(_root, "cache"));

    private static readonly string SampleBuild = Path.Combine(AppContext.BaseDirectory, "sample-extension", "Roost.Extensibility.Sample");

    private void InstallSample()
    {
        var target = Path.Combine(Paths.Extensions, "Roost.Extensibility.Sample");
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(SampleBuild))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
    }

    [Fact]
    public void SinCarpeta_NingunaExtension_NiErrores()
    {
        var set = ExtensionSet.Load(Paths);

        Assert.Empty(set.Loaded);
        Assert.Empty(set.Errors);
    }

    [Fact]
    public void CargaLaMuestra_YRechazaLaQuePideUnContratoMasNuevo()
    {
        InstallSample();

        var set = ExtensionSet.Load(Paths);

        var sample = Assert.Single(set.Loaded);
        Assert.Equal("muestra", sample.Id);
        Assert.Equal("1.2.3", sample.Version);
        var error = Assert.Single(set.Errors);
        Assert.Contains($"pide la version {ExtensionApi.Current + 1} del contrato y este Roost tiene la {ExtensionApi.Current}", error.Message);
        // Los contratos son los de Roost (contexto compartido), no una copia: si no, el cast fallaría.
        Assert.IsAssignableFrom<IStackExtension>(sample);
    }

    [Fact]
    public void LaMuestra_AportaServiciosAvisosYComandos()
    {
        InstallSample();
        var set = ExtensionSet.Load(Paths);

        var services = set.StackServices(new StackContext(Paths, new RoostConfig()));
        Assert.Equal(["muestra"], services.Select(s => s.Name));
        Assert.Equal(6399, services[0].Port);

        var dataset = set.WithAdvisories(AuditDataset.LoadEmbedded(), Paths);
        Assert.Contains(dataset.Advisories, a => a.Id == "MUESTRA-1");
        Assert.Contains(dataset.Advisories, a => a.Id == "CVE-2026-109618");   // los de Roost siguen

        var app = new CommandApp();
        app.Configure(config => set.AddCommands(config));
        Assert.Equal(42, app.Run(["muestra"]));
    }

    [Fact]
    public void UnaDllRotaOUnaCarpetaSinDll_SonErrores_NoExcepciones()
    {
        Directory.CreateDirectory(Path.Combine(Paths.Extensions, "Rota"));
        File.WriteAllText(Path.Combine(Paths.Extensions, "Rota", "Rota.dll"), "esto no es una dll");
        Directory.CreateDirectory(Path.Combine(Paths.Extensions, "Vacia"));
        Directory.CreateDirectory(Path.Combine(Paths.Extensions, ".tmp-algo"));   // de trabajo: se ignora

        var set = ExtensionSet.Load(Paths);

        Assert.Empty(set.Loaded);
        Assert.Equal(["Rota", "Vacia"], set.Errors.Select(e => e.Source));
        Assert.StartsWith("no carga", set.Errors[0].Message);
        Assert.StartsWith("no hay Vacia.dll", set.Errors[1].Message);
    }

    private sealed class Faulty : IStackExtension, IAuditExtension, ICliExtension
    {
        public string Id => "rota";
        public string Name => "Extension rota";
        public string Version => "0.0.1";
        public IEnumerable<ServiceDefinition> Services(StackContext context) => throw new InvalidOperationException("sin licencia");
        public AuditDataset? Advisories(RoostPaths paths) => throw new InvalidOperationException("feed caido");
        public IEnumerable<IComponentScanner> Scanners(RoostPaths paths) => throw new InvalidOperationException("scanner roto");
        public void AddCommands(IConfigurator config) => throw new InvalidOperationException("comando roto");
    }

    [Fact]
    public void LoQueLanzaUnaExtension_SeApunta_YRoostSigue()
    {
        var set = new ExtensionSet([new Faulty()]);

        var dataset = set.WithAdvisories(AuditDataset.LoadEmbedded(), Paths);
        Assert.Equal(AuditDataset.LoadEmbedded().Advisories.Count, dataset.Advisories.Count);
        _ = set.Scanner(Paths);
        var app = new CommandApp();
        app.Configure(config => set.AddCommands(config));

        Assert.Equal(["fallo al dar sus avisos: feed caido", "fallo al dar sus scanners: scanner roto", "fallo al dar sus comandos: comando roto"],
            set.Errors.Select(e => e.Message));
        Assert.All(set.Errors, e => Assert.Equal("Extension rota", e.Source));
    }

    [Fact]
    public void SiNoPuedePrepararSusServicios_UpFallaDiciendoCual()
    {
        var set = new ExtensionSet([new Faulty()]);

        var ex = Assert.Throws<RoostException>(() => set.StackServices(new StackContext(Paths, new RoostConfig())));
        Assert.Contains("Extension rota", ex.Message);
        Assert.Contains("sin licencia", ex.Message);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
