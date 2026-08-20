using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
        var clusterId = $"durable-messaging-{Guid.NewGuid():N}";
        var serviceId = $"durable-messaging-service-{Guid.NewGuid():N}";
        var builder = new InProcessTestClusterBuilder((short)initialSilos);
        builder.ConfigureClient(clientBuilder =>
            clientBuilder.Configure<ClusterOptions>(options =>
            {
                options.ClusterId = clusterId;
                options.ServiceId = serviceId;
            }));
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
            siloBuilder.UseInMemoryDurableJobs();
            siloBuilder.AddDurableMessaging(ConfigureOptions);
            siloBuilder.ConfigureServices(services =>
                ControlledDurableJobManager.Decorate(services, JobManagerProbe));
            siloBuilder.Services.Configure<JournaledStateManagerOptions>(
                options => options.JournalFormatKey = "orleans-binary");
            siloBuilder.Services.RemoveAll<IJournalStorageProvider>();
            siloBuilder.Services.RemoveAll<IJournalStorageCatalog>();
            siloBuilder.Services.AddSingleton<IJournalStorageProvider>(Storage);
            siloBuilder.Services.AddSingleton<IJournalStorageCatalog>(Storage);
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

    protected virtual void ConfigureOptions(DurableInboxOptions options)
    {
        options.MaxCapacity = 2;
        options.DeduplicationWindow = TimeSpan.FromMinutes(10);
        options.MaxOutboxRetryAge = TimeSpan.FromMinutes(5);
        options.MaxProcessingAttempts = 1;
        options.MaxDeliveryAttempts = 3;
        options.BackpressureRetryDelay = TimeSpan.FromMilliseconds(25);
        options.InboxBatchSize = 8;
        options.OutboxBatchSize = 8;
    }

    public Task InitializeAsync() => Cluster.DeployAsync();
    public async Task DisposeAsync()
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
