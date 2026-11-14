using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Orleans.Hosting;
using Orleans.Providers.MongoDB.Reminders;
using Orleans.Reminders;
using Orleans.Reminders.TestKit;
using Orleans.Runtime;
using Orleans.Testing.Reminders;
using Orleans.TestingHost;
using Xunit;

namespace Orleans.MongoDB.Tests.Reminders;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MongoReminderServiceCollection
{
    public const string Name = "MongoDB reminder service conformance";
}

[TestProvider("MongoDB"), TestSuite("Functional"), TestArea("Reminders")]
[Collection(MongoReminderServiceCollection.Name)]
public sealed class MongoReminderServiceConformanceTests : IAsyncLifetime
{
    private MongoDatabase? _database;
    private InProcessTestCluster? _cluster;
    private ReminderTestClock? _clock;
    private ServiceRunner? _service;
    private LifecycleRunner? _lifecycle;

    public async ValueTask InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(MongoDatabase.ConnectionVariable))) return;
        _database = await MongoDatabase.CreateAsync();
        try
        {
            var loadingWindow = TimeSpan.FromSeconds(5);
            var builder = new InProcessTestClusterBuilder(1);
            _clock = builder.AddReminderTestClock(
                minimumReminderPeriod: TimeSpan.FromSeconds(1),
                refreshReminderListPeriod: TimeSpan.FromSeconds(1));
            builder.ConfigureSilo((_, silo) =>
            {
                silo.Services.AddSingleton<IMongoClient>(_database.Client);
                silo.UseMongoDBReminders(options => options.DatabaseName = _database.Name);
                silo.Configure<ReminderOptions>(options => options.ReminderLoadingWindow = loadingWindow);
            });
            _cluster = builder.Build();
            await _cluster.DeployAsync(TestContext.Current.CancellationToken);
            var services = _cluster.Silos[0].ServiceProvider;
            var table = Assert.IsType<MongoReminderTable>(services.GetRequiredService<IReminderTable>());
            _service = new ServiceRunner(_cluster.Client, table,
                services.GetRequiredKeyedService<TimeProvider>(ReminderTimeProviderNames.Reminders));
            _lifecycle = new LifecycleRunner(new ReminderServiceLifecycleHarness(
                _cluster, _clock, _clock.DiagnosticObserver, loadingWindow));
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        var cluster = _cluster;
        var clock = _clock;
        var database = _database;
        _cluster = null;
        _clock = null;
        _database = null;
        try
        {
            if (cluster is not null)
            {
                using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(1));
                await cluster.StopAllSilosAsync(cancellation.Token);
            }
        }
        finally
        {
            try
            {
                if (cluster is not null)
                {
                    using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(1));
                    await cluster.DisposeAsync().AsTask().WaitAsync(cancellation.Token);
                }
            }
            finally
            {
                try { clock?.Dispose(); }
                finally { if (database is not null) await database.DisposeAsync(); }
            }
        }
    }

    [Fact]
    public Task ReminderService_RegisterLookupEnumerateAndUnregister()
        => RunAsync(token => _service!.RunReminderService_RegisterLookupEnumerateAndUnregister(token));

    [Fact]
    public Task ReminderService_UpdateReplacesScheduleAndETagWithoutDuplicate()
        => RunAsync(token => _service!.RunReminderService_UpdateReplacesScheduleAndETagWithoutDuplicate(token));

    [Fact]
    public Task ReminderService_StartupReadiness()
        => RunAsync(token => _lifecycle!.RunReminderService_StartupReadiness(token));

    [Fact]
    public Task ReminderService_RegistrationHasSingleOwner()
        => RunAsync(token => _lifecycle!.RunReminderService_RegistrationHasSingleOwner(token));

    [Fact]
    public Task ReminderService_UpdateDoesNotRestartLocalOwner()
        => RunAsync(token => _lifecycle!.RunReminderService_UpdateDoesNotRestartLocalOwner(token));

    [Fact]
    public Task ReminderService_RemovalReachesQuiescence()
        => RunAsync(token => _lifecycle!.RunReminderService_RemovalReachesQuiescence(token));

    [Fact]
    public Task ReminderService_ExactDueRecovery()
        => RunAsync(token => _lifecycle!.RunReminderService_ExactDueRecovery(token));

    [Fact]
    public Task ReminderService_StaleOwnerRegistrationReconciles()
        => RunAsync(token => _lifecycle!.RunReminderService_StaleOwnerRegistrationReconciles(token));

    [Fact]
    public Task ReminderService_OneSiloJoinLeaveTransfersOwnership()
        => RunAsync(token => _lifecycle!.RunReminderService_OneSiloJoinLeaveTransfersOwnership(token));

    [Fact]
    public Task ReminderService_CleanupIsIsolated()
        => RunAsync(token => _lifecycle!.RunReminderService_CleanupIsIsolated(token));

    private static async Task RunAsync(Func<CancellationToken, Task> scenario)
    {
        MongoDatabase.RequireConnection();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromMinutes(2));
        await scenario(cancellation.Token);
    }

    private sealed class ServiceRunner(IGrainFactory grains, IReminderTable table, TimeProvider clock)
        : ReminderServiceTestRunner(grains, table, "MongoDB", seed: 6143, reminderTimeProvider: clock);

    private sealed class LifecycleRunner(IReminderServiceLifecycleHarness harness)
        : ReminderServiceLifecycleTestRunner(harness, "MongoDB", seed: 6143);

}
