using Microsoft.Extensions.DependencyInjection;
using Musterdepot.Core.Abstractions;
using Musterdepot.IntegrationTests.Infrastructure;

namespace Musterdepot.IntegrationTests;

public class TestHostTests
{
    [Fact]
    public async Task Host_UsesTemporaryDataDirectory_AndRestoresEnvironment()
    {
        string dataDir;
        await using (var host = TestHost.Create())
        {
            dataDir = host.DataDirectory;
            Assert.True(Directory.Exists(dataDir));
            Assert.Equal(dataDir, Environment.GetEnvironmentVariable(TestHost.DataDirVariable));
            Assert.Equal(dataDir, AppPaths.DataDirectory);
            Assert.StartsWith(dataDir, AppPaths.DatabasePath);
        }
        Assert.False(Directory.Exists(dataDir));
        // Zurückgesetzt (parallele Tests können inzwischen einen eigenen Host mit eigenem Verzeichnis haben).
        Assert.NotEqual(dataDir, Environment.GetEnvironmentVariable(TestHost.DataDirVariable));
    }

    [Fact]
    public async Task Host_ProvidesDeterministicInfrastructure()
    {
        var start = new DateTimeOffset(2026, 11, 3, 8, 0, 0, TimeSpan.Zero);
        await using var host = TestHost.Create(o => { o.StartUtc = start; o.Seed = 7; });
        Assert.Same(host.Clock, host.Get<IClock>());
        Assert.Equal(start, host.Get<IClock>().UtcNow);
        host.Clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(start.AddSeconds(5), host.Get<IClock>().UtcNow);
        var ids = host.Get<IIdGenerator>();
        Assert.Equal("ord_000001", ids.NewId("ord"));
        Assert.Equal("ord_000002", ids.NewId("ord"));
        Assert.Equal(new SeededRandom(7).NextDouble(), host.Get<IRandomSource>().NextDouble());
    }

    [Fact]
    public async Task Host_AllowsOverridingServices()
    {
        var fixed_ = new SequentialIdGenerator();
        fixed_.NewId("x");
        await using var host = TestHost.Create(o => o.ConfigureServices = s =>
            s.AddSingleton<IIdGenerator>(fixed_));
        Assert.Equal("x_000002", host.Get<IIdGenerator>().NewId("x"));
    }

    [Fact]
    public async Task Hosts_AreSerialized()
    {
        // Zwei Hosts nacheinander aus parallelen Aufgaben: jeder sieht sein eigenes Verzeichnis.
        async Task<bool> Run()
        {
            await using var host = TestHost.Create();
            await Task.Delay(20);
            return AppPaths.DataDirectory == host.DataDirectory;
        }
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(Run)));
        Assert.All(results, Assert.True);
    }
}
