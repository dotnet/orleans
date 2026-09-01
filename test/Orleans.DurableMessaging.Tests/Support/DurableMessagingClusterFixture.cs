using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Orleans.Configuration;
using Orleans.DurableJobs;
using Orleans.DurableMessaging.Configuration;
using Orleans.Hosting;
using Orleans.Journaling;
using Orleans.TestingHost;
using Xunit;

namespace Orleans.DurableMessaging.Tests.Support;

public class DurableMessagingClusterFixture : IAsyncLifetime
{
    public DurableMessagingClusterFixture() : this(1)
    {
    }

    protected DurableMessagingClusterFixture(int initialSilos)
    {
        Clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        Storage = new ControlledJournalStorageProvider();
        Metrics = new DurableMessagingMetricProbe();
        JobManagerProbe = new DurableJobManagerProbe();
        HandlerProbe = new HandlerProbe();
        SnapshotProbe = new SnapshotProbe();
        DurableTaskExecutionProbe = new DurableTaskExecutionProbe();
        var clusterId = $"durable-messaging-{Guid.NewGuid():N}";
        var serviceId = $"durable-messaging-service-{Guid.NewGuid():N}";
        var builder = new InProcessTestClusterBuilder((short)initialSilos);
        builder.ConfigureClient(clientBuilder =>
        {
            clientBuilder.AddDurableTasks();
            clientBuilder.Configure<ClusterOptions>(options =>
            {
                options.ClusterId = clusterId;
                options.ServiceId = serviceId;
            });
        });
        builder.ConfigureSilo((_, siloBuilder) =>
        {
            siloBuilder.Configure<ClusterOptions>(options =>
            {
                options.ClusterId = clusterId;
                options.ServiceId = serviceId;
            });
            siloBuilder.Services.AddSingleton<TimeProvider>(Clock);
            siloBuilder.Services.UseTimeProviderForBackgroundAreas(TimeProvider.System);
            siloBuilder.Services.AddSingleton(HandlerProbe);
            siloBuilder.Services.AddSingleton(SnapshotProbe);
            siloBuilder.Services.AddSingleton(DurableTaskExecutionProbe);
            siloBuilder.UseInMemoryDurableJobs();
            siloBuilder.AddDurableTasks();
            siloBuilder.Services.Configure<DurableInboxOptions>(ConfigureOptions);
            siloBuilder.ConfigureServices(services =>
                ControlledDurableJobManager.Decorate(services, JobManagerProbe));
            siloBuilder.Services.RemoveAll<IJournalStorageProvider>();
            siloBuilder.Services.RemoveAll<IJournalStorageCatalog>();
            siloBuilder.Services.AddSingleton(Storage);
            siloBuilder.Services.AddSingleton<IJournalStorageProvider>(serviceProvider =>
            {
                Storage.Configure(serviceProvider.GetRequiredService<IOptions<JournaledStateManagerOptions>>());
                return Storage;
            });
            siloBuilder.Services.AddSingleton<IJournalStorageCatalog>(
                serviceProvider => (IJournalStorageCatalog)serviceProvider.GetRequiredService<IJournalStorageProvider>());
        });
        Cluster = builder.Build();
    }

    public InProcessTestCluster Cluster { get; }
    public IClusterClient Client => Cluster.Client!;
    public FakeTimeProvider Clock { get; }
    public ControlledJournalStorageProvider Storage { get; }
    public DurableMessagingMetricProbe Metrics { get; }
    public DurableJobManagerProbe JobManagerProbe { get; }
    public HandlerProbe HandlerProbe { get; }
    public SnapshotProbe SnapshotProbe { get; }
    public DurableTaskExecutionProbe DurableTaskExecutionProbe { get; }

    public Task<DurableEndpointSnapshot> WaitForEffectCountAsync(IDurableMessagingTestGrain grain, int expected) =>
        SnapshotProbe.WaitAsync(
            grain.GetGrainId(),
            snapshot => snapshot.Effects.Sum(static effect => effect.Count) >= expected);

    public Task<DurableEndpointSnapshot> WaitForInboxCountAsync(IDurableMessagingTestGrain grain, int expected) =>
        SnapshotProbe.WaitAsync(grain.GetGrainId(), snapshot => snapshot.InboxCount == expected);

    public Task<DurableEndpointSnapshot> WaitForDeadLetterCountAsync(IDurableMessagingTestGrain grain, int expected) =>
        SnapshotProbe.WaitAsync(
            grain.GetGrainId(),
            snapshot => snapshot.InboxDeadLetters.Count + snapshot.OutboxDeadLetters.Count >= expected);

    public Task<DurableEndpointSnapshot> WaitForOutboxCountAsync(IDurableMessagingTestGrain grain, int expected) =>
        SnapshotProbe.WaitAsync(grain.GetGrainId(), snapshot => snapshot.OutboxCount == expected);

    public DurableEndpointSnapshot GetSnapshot(IDurableMessagingTestGrain grain) =>
        GetGrainInstance(grain).GetSnapshotForTest();

    public ValueTask RevertStateAsync(IDurableMessagingTestGrain grain) =>
        GetGrainContext(grain).ActivationServices
            .GetRequiredService<IJournaledStateManager>()
            .RevertPendingChangesAsync(TestContext.Current.CancellationToken);

    public ValueTask WriteStateAsync(IDurableMessagingTestGrain grain) =>
        GetGrainContext(grain).ActivationServices
            .GetRequiredService<IJournaledStateManager>()
            .WriteStateAsync(TestContext.Current.CancellationToken);

    public void DeactivateOnNextRecovery(IDurableMessagingTestGrain grain) =>
        GetGrainInstance(grain).DeactivateOnNextRecoveryForTest();

    private IGrainContext GetGrainContext(IDurableMessagingTestGrain grain)
    {
        if (!Cluster.TryGetGrainContext(grain.GetGrainId(), out var context))
        {
            throw new InvalidOperationException($"Grain '{grain.GetGrainId()}' is not active.");
        }

        return context;
    }

    private DurableMessagingTestGrain GetGrainInstance(IDurableMessagingTestGrain grain) =>
        GetGrainContext(grain).GrainInstance as DurableMessagingTestGrain
        ?? throw new InvalidOperationException($"Grain '{grain.GetGrainId()}' has an unexpected implementation.");

    protected virtual void ConfigureOptions(DurableInboxOptions options)
    {
        options.MaxCapacity = 2;
        options.DeduplicationWindow = TimeSpan.FromMinutes(10);
        options.MaxOutboxRetryAge = TimeSpan.FromMinutes(5);
        options.MaxProcessingAttempts = 1;
        options.MaxDeliveryAttempts = 3;
        options.MaxRetainedDeadLetters = 2;
        options.DeadLetterRetentionPeriod = TimeSpan.FromHours(1);
        options.BackpressureRetryDelay = TimeSpan.FromMilliseconds(25);
        options.InboxBatchSize = 8;
        options.OutboxBatchSize = 8;
    }

    public ValueTask InitializeAsync() => new(Cluster.DeployAsync());
    public async ValueTask DisposeAsync()
    {
        await Cluster.DisposeAsync();
        Metrics.Dispose();
    }
}

public sealed class MultiSiloDurableMessagingClusterFixture : DurableMessagingClusterFixture
{
    public MultiSiloDurableMessagingClusterFixture() : base(2)
    {
    }
}

public sealed class DedupeExpiryClusterFixture : DurableMessagingClusterFixture
{
}

public sealed class InboxCapacityClusterFixture : DurableMessagingClusterFixture
{
    protected override void ConfigureOptions(DurableInboxOptions options)
    {
        base.ConfigureOptions(options);
        options.MaxCapacity = 1;
        options.MaxProcessingAttempts = 2;
        options.BackpressureRetryDelay = TimeSpan.FromHours(1);
    }
}
