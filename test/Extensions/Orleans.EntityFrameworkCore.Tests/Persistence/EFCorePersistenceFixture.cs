using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Orleans.EntityFrameworkCore.Tests.Infrastructure;
using Orleans.Hosting;
using Orleans.Persistence.EntityFrameworkCore.Data;
using Orleans.TestingHost;
using TestExtensions;

namespace Orleans.EntityFrameworkCore.Tests.Persistence;

public sealed class EFCorePersistenceFixture<TDbContext, TETag, TProvider> : BaseTestClusterFixture
    where TDbContext : GrainStateDbContext<TDbContext, TETag>
    where TProvider : EFCoreProviderConfiguration<TETag>, new()
{
    public const string GrainStorageName = "GrainStorageForTest";
    private const string ConnectionStringKey = "EFCorePersistenceConnectionString";
    private EFCoreDatabaseFixture<TDbContext>? _databaseFixture;

    protected override void CheckPreconditionsOrThrow() =>
        new TProvider().Database.RequireConnectionString();

    public override async ValueTask InitializeAsync()
    {
        if (!PreconditionsMet)
        {
            return;
        }

        var provider = new TProvider();
        _databaseFixture = new EFCoreDatabaseFixture<TDbContext>(
            provider.Database,
            "persistence_cluster",
            $"{typeof(TProvider).Name}_{GetTargetFramework()}");
        await _databaseFixture.InitializeAsync();

        await base.InitializeAsync();
    }

    protected override void ConfigureTestCluster(TestClusterBuilder builder)
    {
        builder.Options.InitialSilosCount = 4;
        builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                [ConnectionStringKey] = _databaseFixture?.ConnectionString
                    ?? throw new InvalidOperationException("The persistence database has not been initialized.")
            }));
        builder.AddSiloBuilderConfigurator<SiloConfigurator>();
    }

    public override async ValueTask DisposeAsync()
    {
        try
        {
            await base.DisposeAsync();
        }
        finally
        {
            if (_databaseFixture is not null)
            {
                await _databaseFixture.DisposeAsync();
            }
        }
    }

    public sealed class SiloConfigurator : IHostConfigurator
    {
        public void Configure(IHostBuilder hostBuilder)
        {
            var connectionString = hostBuilder.GetConfiguration()[ConnectionStringKey]
                ?? throw new InvalidOperationException("The persistence connection string was not supplied.");

            hostBuilder.UseOrleans((_, siloBuilder) =>
            {
                var provider = new TProvider();
                provider.UseGrainStorage(
                    siloBuilder,
                    GrainStorageName,
                    options => provider.Database.ConfigureOptions(
                        options,
                        connectionString,
                        typeof(TDbContext).Assembly.GetName().Name!));
                siloBuilder.AddMemoryGrainStorage("MemoryStore");
            });
        }
    }

    private static string GetTargetFramework()
    {
#if NET8_0
        return "net8";
#elif NET10_0
        return "net10";
#else
        return "unknown";
#endif
    }
}
