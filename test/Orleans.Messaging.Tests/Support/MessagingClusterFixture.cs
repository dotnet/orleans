using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Orleans.Configuration;
using Orleans.DurableJobs;
using Orleans.Hosting;
using Orleans.Journaling;
using Orleans.Messaging.Configuration;
using Orleans.TestingHost;
using Xunit;

namespace Orleans.Messaging.Tests.Support;

public class MessagingClusterFixture : IAsyncLifetime
{
    public MessagingClusterFixture() : this(receiverOnly: true)
    {
    }

    public MessagingClusterFixture(bool receiverOnly) : this(1, receiverOnly)
    {
    }

    protected MessagingClusterFixture(int initialSilos, bool receiverOnly = false, bool useServiceCollection = false)
    {
        Clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        Storage = new ControlledJournalStorageProvider();
        Metrics = new MessagingMetricProbe();
        JobManagerProbe = new DurableJobManagerProbe();
        HandlerProbe = new HandlerProbe();
        SnapshotProbe = new SnapshotProbe();
        ActivationProbe = new ActivationValidationProbe();
        var clusterId = $"messaging-{Guid.NewGuid():N}";
        var serviceId = $"messaging-service-{Guid.NewGuid():N}";
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
            // Advancing the test clock also advances the Durable Jobs clock, which inbox retry deadlines follow.
            siloBuilder.Services.AddKeyedSingleton<TimeProvider>(
                DurableJobTimeProviderNames.DurableJobs,
                new FollowingJobClock(Clock, Clock.GetUtcNow()));
            siloBuilder.Services.AddSingleton(HandlerProbe);
            siloBuilder.Services.AddSingleton(new TestHandlerConfiguration());
            siloBuilder.Services.AddSingleton(SnapshotProbe);
            siloBuilder.Services.AddSingleton(ActivationProbe);
            siloBuilder.UseInMemoryDurableJobs();
            // The test grains journal Orleans-serialized types.
            siloBuilder.Services.Configure<JournaledStateManagerOptions>(options => options.JournalFormatKey = "orleans-binary");
            if (receiverOnly)
            {
                ReceiverTestServices.Add(siloBuilder.Services, ConfigureOptions);
            }
            else if (useServiceCollection)
            {
                siloBuilder.Services.AddMessaging(ConfigureOptions);
            }
            else
            {
                siloBuilder.AddMessaging(ConfigureOptions);
            }
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
            ConfigureServices(siloBuilder.Services);
        });
        Cluster = builder.Build();
    }

    /// <summary>
    /// Makes the Durable Jobs clock read <see cref="Clock"/>, so retry deadlines are exact test-clock times. Durable Jobs
    /// timers keep following real time and run each wake-up once the test clock reaches its due time.
    /// </summary>
    protected void UseTestClockForDurableJobs(IServiceCollection services) =>
        services.AddKeyedSingleton<TimeProvider>(DurableJobTimeProviderNames.DurableJobs, new TestJobClock(Clock));

    /// <summary>A Durable Jobs clock which follows real time plus the test clock's advance since the fixture started.</summary>
    private sealed class FollowingJobClock(TimeProvider clock, DateTimeOffset clockStart) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + (clock.GetUtcNow() - clockStart);
    }

    private sealed class TestJobClock(TimeProvider clock) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => clock.GetUtcNow();
    }

    public InProcessTestCluster Cluster { get; }
    public IClusterClient Client => Cluster.Client!;
    public FakeTimeProvider Clock { get; }
    public ControlledJournalStorageProvider Storage { get; }
    public MessagingMetricProbe Metrics { get; }
    public DurableJobManagerProbe JobManagerProbe { get; }
    public HandlerProbe HandlerProbe { get; }
    public SnapshotProbe SnapshotProbe { get; }
    public ActivationValidationProbe ActivationProbe { get; }

    public Task<EndpointSnapshot> WaitForEffectCountAsync(IMessagingTestGrain grain, int expected) =>
        SnapshotProbe.WaitAsync(
            grain.GetGrainId(),
            snapshot => snapshot.Effects.Sum(static effect => effect.Count) >= expected);

    public Task<EndpointSnapshot> WaitForInboxCountAsync(IMessagingTestGrain grain, int expected) =>
        SnapshotProbe.WaitAsync(grain.GetGrainId(), snapshot => snapshot.InboxCount == expected);

    public Task<EndpointSnapshot> WaitForDeadLetterCountAsync(IMessagingTestGrain grain, int expected) =>
        SnapshotProbe.WaitAsync(
            grain.GetGrainId(),
            snapshot => snapshot.InboxDeadLetters.Count + snapshot.OutboxDeadLetters.Count >= expected);

    public Task<EndpointSnapshot> WaitForOutboxCountAsync(IMessagingTestGrain grain, int expected) =>
        SnapshotProbe.WaitAsync(grain.GetGrainId(), snapshot => snapshot.OutboxCount == expected);

    public EndpointSnapshot GetSnapshot(IMessagingTestGrain grain) =>
        GetGrainInstance(grain).GetSnapshotForTest();

    public OutboxMessage[] GetStagedOutput(IMessagingTestGrain grain) =>
        GetGrainContext(grain).ActivationServices.GetRequiredService<IOutbox>().Messages().ToArray();

    public ValueTask WriteStateAsync(IMessagingTestGrain grain) =>
        GetGrainContext(grain).ActivationServices
            .GetRequiredService<IJournaledStateManager>()
            .WriteStateAsync(TestContext.Current.CancellationToken);

    internal Inbox GetInbox(IAddressable grain) =>
        GetGrainContext(grain).ActivationServices.GetRequiredService<Inbox>();

    internal MessagingPump GetPump(IAddressable grain) =>
        GetGrainContext(grain).ActivationServices.GetRequiredService<MessagingPump>();

    /// <summary>
    /// Schedules an additional pump wake-up. Wake-ups are at-least-once hints, so extra ones must be harmless.
    /// </summary>
    public async Task WakeAsync(IAddressable grain)
    {
        var services = Cluster.TryGetGrainContext(grain.GetGrainId(), out var context)
            ? context.ActivationServices
            : Cluster.Silos[0].ServiceProvider;
        await services.GetRequiredService<ILocalDurableJobManager>().ScheduleJobAsync(
            new ScheduleJobRequest
            {
                Target = grain.GetGrainId(),
                JobName = MessagingPump.JobName,
                DueTime = DateTimeOffset.UtcNow
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>Executes the pump's durable job callback directly, as a spurious or duplicate wake-up would.</summary>
    internal async Task<DurableJobRunResult> RunPumpAsync(IAddressable grain)
    {
        var job = new DurableJob
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = MessagingPump.JobName,
            ShardId = "test",
            TargetGrainId = grain.GetGrainId(),
            DueTime = DateTimeOffset.UtcNow
        };
        var pump = GetPump(grain);
        var run = await OnGrainAsync(grain, () => pump.ExecuteJobAsync(
            new PumpRunContext(job),
            TestContext.Current.CancellationToken).AsTask());
        return await run.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
    }

    /// <summary>Runs <paramref name="func"/> on the grain's activation scheduler.</summary>
    internal Task<T> OnGrainAsync<T>(IAddressable grain, Func<T> func)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        GetGrainContext(grain).Scheduler.QueueAction(() =>
        {
            try
            {
                done.SetResult(func());
            }
            catch (Exception exception)
            {
                done.SetException(exception);
            }
        });
        return done.Task;
    }

    internal Task OnGrainAsync(IAddressable grain, Action action) =>
        OnGrainAsync(grain, () =>
        {
            action();
            return true;
        });

    /// <summary>Polls <paramref name="predicate"/> on the grain's scheduler until it holds.</summary>
    internal async Task WaitUntilAsync(IAddressable grain, Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!await OnGrainAsync(grain, predicate))
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Condition was not met for grain '{grain.GetGrainId()}'.");
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Forces idle activation collection until <paramref name="context"/> deactivates.</summary>
    internal async Task CollectAsync(IGrainContext context)
    {
        var management = Client.GetGrain<IManagementGrain>(0);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!context.Deactivated.IsCompleted)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Activation '{context.GrainId}' was not collected.");
            }

            await management.ForceActivationCollection(TimeSpan.Zero, TestContext.Current.CancellationToken);
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }

    internal IGrainContext GetGrainContext(IAddressable grain)
    {
        if (!Cluster.TryGetGrainContext(grain.GetGrainId(), out var context))
        {
            throw new InvalidOperationException($"Grain '{grain.GetGrainId()}' is not active.");
        }

        return context;
    }

    private MessagingTestGrain GetGrainInstance(IMessagingTestGrain grain) =>
        GetGrainContext(grain).GrainInstance as MessagingTestGrain
        ?? throw new InvalidOperationException($"Grain '{grain.GetGrainId()}' has an unexpected implementation.");

    private sealed class PumpRunContext(DurableJob job) : IJobRunContext
    {
        public DurableJob Job { get; } = job;
        public string RunId { get; } = Guid.NewGuid().ToString("N");
        public int DequeueCount => 1;
    }

    protected virtual void ConfigureServices(IServiceCollection services) { }

    protected virtual void ConfigureOptions(DurableMessagingOptions options)
    {
        options.MaxCapacity = 2;
        options.DeduplicationWindow = TimeSpan.FromMinutes(10);
        options.OutboxRetryTimeout = TimeSpan.FromSeconds(3);
        options.InboxRetryTimeout = TimeSpan.Zero;
        options.MaxRetainedDeadLetters = 2;
        options.DeadLetterRetentionPeriod = TimeSpan.FromHours(1);
        options.RetryInitialInterval = TimeSpan.FromMilliseconds(25);
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

public sealed class MultiSiloMessagingClusterFixture : MessagingClusterFixture
{
    public MultiSiloMessagingClusterFixture() : base(2)
    {
    }
}

public sealed class DedupeExpiryClusterFixture : MessagingClusterFixture
{
}

public sealed class InboxCapacityClusterFixture : MessagingClusterFixture
{
    protected override void ConfigureOptions(DurableMessagingOptions options)
    {
        base.ConfigureOptions(options);
        options.MaxCapacity = 1;
        options.InboxRetryTimeout = TimeSpan.FromHours(1);
        options.RetryInitialInterval = TimeSpan.FromHours(1);
    }

    protected override void ConfigureServices(IServiceCollection services)
    {
        base.ConfigureServices(services);
        UseTestClockForDurableJobs(services);
    }
}
