using Cassandra;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.DurableJobs;
using Orleans.DurableJobs.Tests;
using Orleans.Hosting;
using Orleans.Journaling;
using Orleans.Runtime;
using TestExtensions;
using Xunit;

namespace Tester.Cassandra.Journaling;

[TestSuite("BVT")]
[TestProvider("Cassandra")]
[TestArea("DurableJobs")]
[TestCategory("BVT")]
[TestCategory("Cassandra")]
public sealed class CassandraDurableJobsTests(CassandraDurableJobsTestFixture fixture)
    : IClassFixture<CassandraDurableJobsTestFixture>
{
    [Fact]
    public async Task ShardIsDiscoveredAndRecoveredFromCassandraCatalog()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await fixture.CreateScopeAsync(cancellationToken);
        var owner = scope.CreateManager(scope.ActiveSilo);
        var now = scope.Now;
        await using var shard = await owner.CreateShardAsync(
            now.AddMinutes(-1),
            now.AddMinutes(5),
            new Dictionary<string, string> { ["purpose"] = "cassandra-catalog-test" },
            cancellationToken);
        var scheduled = await shard.TryScheduleJobAsync(
            new ScheduleJobRequest
            {
                Target = GrainId.Create("durable-job-test", "target"),
                JobName = "cassandra-catalog-test",
                DueTime = now.AddMinutes(-1),
                Metadata = new Dictionary<string, string> { ["key"] = "value" },
            },
            cancellationToken);
        Assert.NotNull(scheduled);

        scope.SetSiloStatus(scope.ActiveSilo, SiloStatus.Dead);
        var successor = scope.CreateManager(scope.SecondActiveSilo);
        await using var recovered = Assert.Single(await successor.AssignJobShardsAsync(now.AddMinutes(5), 1, cancellationToken));

        Assert.Equal(shard.Id, recovered.Id);
        Assert.Equal(1, await recovered.GetJobCountAsync());
        await using var runs = recovered.ConsumeDurableJobsAsync().GetAsyncEnumerator(cancellationToken);
        Assert.True(await runs.MoveNextAsync());
        Assert.Equal(scheduled.Id, runs.Current.Job.Id);
        Assert.Equal("cassandra-catalog-test", runs.Current.Job.Name);
        Assert.Equal("value", runs.Current.Job.Metadata!["key"]);
    }
}

public sealed class CassandraDurableJobsTestFixture : IAsyncLifetime, IJobShardManagerTestFixture
{
    private readonly CassandraJournalContainer _container = new();

    public async Task<IJobShardManagerTestScope> CreateScopeAsync(CancellationToken cancellationToken)
    {
        _container.EnsurePreconditionsMet();
        var session = await _container.GetSessionAsync(cancellationToken);
        var tableName = $"durablejobs_test_{Guid.NewGuid():N}";
        var catalogTableName = $"{tableName}_catalog";
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMetrics();
        services.AddSingleton<OrleansInstruments>();
        services.AddSingleton(TimeProvider.System);
        services.AddKeyedSingleton<TimeProvider>(KeyedService.AnyKey, static (sp, _) => sp.GetRequiredService<TimeProvider>());
        services.UseCassandraDurableJobs(options =>
        {
            options.TableName = tableName;
            options.CatalogTableName = catalogTableName;
            options.ConfigureClient(_ => Task.FromResult(session));
        });

        var storageRegistration = services.Last(
            descriptor => !descriptor.IsKeyedService && descriptor.ServiceType == typeof(IJournalStorageProvider));
        var createStorage = storageRegistration.ImplementationFactory;
        Assert.NotNull(createStorage);

        var serviceProvider = services.BuildServiceProvider();
        var lifecycle = new SiloLifecycleSubject(serviceProvider.GetRequiredService<ILogger<SiloLifecycleSubject>>());
        var storageProvider = (IJournalStorageProvider)createStorage(serviceProvider);
        Assert.IsAssignableFrom<ILifecycleParticipant<ISiloLifecycle>>(storageProvider).Participate(lifecycle);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(60));
        await lifecycle.OnStart(cts.Token);
        return new CassandraDurableJobsTestScope(serviceProvider, lifecycle, session, tableName, catalogTableName);
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    private sealed class CassandraDurableJobsTestScope(
        ServiceProvider services,
        SiloLifecycleSubject lifecycle,
        ISession session,
        string tableName,
        string catalogTableName) : JournaledJobShardManagerTestScope(services)
    {
        public override async ValueTask DisposeAsync()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await lifecycle.OnStop(cts.Token);
            await base.DisposeAsync();
            await session.ExecuteAsync(new SimpleStatement($"DROP TABLE IF EXISTS {tableName}"));
            await session.ExecuteAsync(new SimpleStatement($"DROP TABLE IF EXISTS {catalogTableName}"));
        }
    }
}
