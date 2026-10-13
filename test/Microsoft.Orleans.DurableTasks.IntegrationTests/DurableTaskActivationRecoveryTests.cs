using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.DurableJobs;
using Orleans.DurableTasks;
using Orleans.Hosting;
using Orleans.Journaling;
using Orleans.TestingHost;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.IntegrationTests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DurableTaskRecoveryCollection
{
    public const string Name = "Durable task activation recovery";
}

/// <summary>Runs activation recovery coverage with task state stored in the default JSON journal format.</summary>
[Collection(DurableTaskRecoveryCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class JsonDurableTaskActivationRecoveryTests() : DurableTaskActivationRecoveryTests(DurableTaskRecoveryFixture.Json);

/// <summary>Runs activation recovery coverage with task state stored in the Orleans binary journal format.</summary>
[Collection(DurableTaskRecoveryCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class OrleansBinaryDurableTaskActivationRecoveryTests() : DurableTaskActivationRecoveryTests(DurableTaskRecoveryFixture.OrleansBinary);

public abstract class DurableTaskActivationRecoveryTests(string journalFormatKey) : IAsyncLifetime
{
    private readonly DurableTaskRecoveryFixture _fixture = new(journalFormatKey);

    public ValueTask InitializeAsync() => _fixture.InitializeAsync();
    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    [Theory]
    [InlineData("default-durable-rpc-round-trip")]
    [InlineData("escaped/root\\identity")]
    public async Task ExistingTerminalTaskRecoversAcrossActivationWithoutReexecution(string rootId)
    {
        const int argument = 41;
        const int expectedResult = 130;
        var cancellationToken = TestContext.Current.CancellationToken;
        var grain = _fixture.Client.GetGrain<IDurableTaskRecoveryTestGrain>(Guid.NewGuid());
        var grainId = grain.GetGrainId();
        var activationBefore = await grain.GetActivationIdAsync();

        var scheduled = await grain.ComputeAsync(argument).ScheduleAsync(rootId, cancellationToken);
        Assert.Equal(expectedResult, await scheduled.WaitAsync(cancellationToken));
        Assert.Equal(
            new DurableTaskInvocationSnapshot(1, activationBefore, argument),
            _fixture.Probe.GetInvocation(grainId));
        var attachedBefore = grain.GetDurableTask<int>(rootId);
        Assert.Equal(expectedResult, await attachedBefore.WaitAsync(cancellationToken));

        await grain.RequestDeactivationAsync();
        var activationAfter = await grain.GetActivationIdAsync();
        await _fixture.Probe.WaitForActivationCountAsync(grainId, 2, cancellationToken);
        var attached = grain.GetDurableTask<int>(rootId);
        var resultAfter = await attached.WaitAsync(cancellationToken);

        Assert.Equal(TaskId.CreateRoot(rootId), attached.Id);
        Assert.Equal(expectedResult, resultAfter);
        Assert.NotEqual(activationBefore, activationAfter);
        Assert.Equal(
            new DurableTaskInvocationSnapshot(1, activationBefore, argument),
            _fixture.Probe.GetInvocation(grainId));
    }

    [Fact]
    public async Task PendingTaskReplaysPersistedLogicalTimeAcrossActivation()
    {
        const string rootId = "logical-time-replay";
        var cancellationToken = TestContext.Current.CancellationToken;
        var grain = _fixture.Client.GetGrain<IDurableTaskRecoveryTestGrain>(Guid.NewGuid());
        var grainId = grain.GetGrainId();
        var scheduled = await grain.ObserveLogicalTimeAsync().ScheduleAsync(rootId, cancellationToken);
        await _fixture.Probe.WaitForLogicalTimeCountAsync(grainId, 1, cancellationToken);

        await grain.RequestDeactivationAsync();
        _ = await grain.GetActivationIdAsync();
        var attached = grain.GetDurableTask<DateTimeOffset>(rootId);
        await _fixture.Probe.WaitForLogicalTimeCountAsync(grainId, 2, cancellationToken);
        var observations = _fixture.Probe.GetLogicalTimes(grainId);

        Assert.Equal(2, observations.Count);
        Assert.Equal(observations[0], observations[1]);
        Assert.False(await attached.IsCompletedAsync(
            new PollingOptions { PollTimeout = TimeSpan.Zero },
            cancellationToken));
        await attached.CancelAsync(cancellationToken);
    }

    [Fact]
    public async Task DelayedTaskResumesWhenDue()
    {
        const string rootId = "delay-resume";
        const int argument = 5;
        var cancellationToken = TestContext.Current.CancellationToken;
        var grain = _fixture.Client.GetGrain<IDurableTaskRecoveryTestGrain>(Guid.NewGuid());
        var grainId = grain.GetGrainId();

        var scheduled = await grain.DelayThenComputeAsync(argument).ScheduleAsync(rootId, cancellationToken);
        var response = await scheduled.GetResponseAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);

        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, response.Status);
        Assert.Equal((argument * 3) + 7, response.GetResult<int>());
        Assert.Single(_fixture.Probe.GetLogicalTimes(grainId));
        Assert.Equal((argument * 3) + 7, await grain.GetDurableTask<int>(rootId).WaitAsync(cancellationToken));
    }

    [Fact]
    public async Task DelayedTaskSurvivesDeactivationAndCompletesWhenDueAfterReactivation()
    {
        const string rootId = "delay-across-deactivation";
        const int argument = 9;
        var cancellationToken = TestContext.Current.CancellationToken;
        var grain = _fixture.Client.GetGrain<IDurableTaskRecoveryTestGrain>(Guid.NewGuid());
        var grainId = grain.GetGrainId();
        var scheduled = await grain.DelayAcrossDeactivationAsync(argument).ScheduleAsync(rootId, cancellationToken);
        await _fixture.Probe.WaitForLogicalTimeCountAsync(grainId, 1, cancellationToken);

        // The delay is an hour long, so it stays pending across the deactivation and the replay which follows it.
        await DeactivateAsync(grain, cancellationToken);
        await _fixture.Probe.WaitForLogicalTimeCountAsync(grainId, 2, cancellationToken);
        var attached = grain.GetDurableTask<int>(rootId);
        Assert.False(await attached.IsCompletedAsync(new PollingOptions { PollTimeout = TimeSpan.Zero }, cancellationToken));

        // Once the Durable Jobs clock passes the due time, the next activation processes the scheduled resume.
        _fixture.JobClock.Advance(TimeSpan.FromHours(2));
        await DeactivateAsync(grain, cancellationToken);
        var response = await attached.GetResponseAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);

        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, response.Status);
        Assert.Equal((argument * 3) + 7, response.GetResult<int>());
        Assert.Equal((argument * 3) + 7, await scheduled.WaitAsync(cancellationToken));
        var observations = _fixture.Probe.GetLogicalTimes(grainId);
        Assert.True(observations.Count >= 3);
        Assert.All(observations, observation => Assert.Equal(observations[0], observation));
    }

    private async Task DeactivateAsync(IDurableTaskRecoveryTestGrain grain, CancellationToken cancellationToken)
    {
        var activations = _fixture.Probe.GetActivationCount(grain.GetGrainId());
        await grain.RequestDeactivationAsync();
        _ = await grain.GetActivationIdAsync();
        await _fixture.Probe.WaitForActivationCountAsync(grain.GetGrainId(), activations + 1, cancellationToken);
    }
}

public interface IDurableTaskRecoveryTestGrain : IGrainWithGuidKey
{
    DurableTask<int> ComputeAsync(int value);
    DurableTask<DateTimeOffset> ObserveLogicalTimeAsync();
    DurableTask<int> DelayThenComputeAsync(int value);
    DurableTask<int> DelayAcrossDeactivationAsync(int value);
    Task<Guid> GetActivationIdAsync();
    Task RequestDeactivationAsync();
}

public readonly record struct DurableTaskInvocationSnapshot(int Count, Guid ActivationId, int Argument);

public sealed class DurableTaskRecoveryProbe
{
    private readonly ConcurrentDictionary<GrainId, DurableTaskInvocationSnapshot> _invocations = [];
    private readonly ConcurrentDictionary<GrainId, int> _activations = [];
    private readonly ConcurrentDictionary<GrainId, List<DateTimeOffset>> _logicalTimes = [];
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void RecordActivation(GrainId grainId)
    {
        _activations.AddOrUpdate(grainId, 1, static (_, count) => count + 1);
        SignalChanged();
    }

    public void RecordInvocation(GrainId grainId, Guid activationId, int argument)
    {
        _invocations.AddOrUpdate(
            grainId,
            _ => new DurableTaskInvocationSnapshot(1, activationId, argument),
            (_, current) => new DurableTaskInvocationSnapshot(current.Count + 1, activationId, argument));
        SignalChanged();
    }

    public int GetActivationCount(GrainId grainId) => _activations.TryGetValue(grainId, out var count) ? count : 0;

    public DurableTaskInvocationSnapshot GetInvocation(GrainId grainId) =>
        _invocations.TryGetValue(grainId, out var snapshot) ? snapshot : default;

    public void RecordLogicalTime(GrainId grainId, DateTimeOffset value)
    {
        var values = _logicalTimes.GetOrAdd(grainId, static _ => []);
        lock (values)
        {
            values.Add(value);
        }

        SignalChanged();
    }

    public IReadOnlyList<DateTimeOffset> GetLogicalTimes(GrainId grainId)
    {
        if (!_logicalTimes.TryGetValue(grainId, out var values))
        {
            return [];
        }

        lock (values)
        {
            return [.. values];
        }
    }

    public async Task WaitForActivationCountAsync(
        GrainId grainId,
        int expected,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (!_activations.TryGetValue(grainId, out var count) || count < expected)
        {
            Task changed;
            lock (_activations)
            {
                if (_activations.TryGetValue(grainId, out count) && count >= expected)
                {
                    return;
                }

                changed = _changed.Task;
            }

            await changed.WaitAsync(timeout.Token);
        }
    }

    public async Task WaitForLogicalTimeCountAsync(
        GrainId grainId,
        int expected,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (GetLogicalTimes(grainId).Count < expected)
        {
            Task changed;
            lock (_activations)
            {
                if (GetLogicalTimes(grainId).Count >= expected)
                {
                    return;
                }

                changed = _changed.Task;
            }

            await changed.WaitAsync(timeout.Token);
        }
    }

    private void SignalChanged()
    {
        lock (_activations)
        {
            _changed.TrySetResult();
            _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}

[GrainType("durable-task-recovery-test")]
public sealed class DurableTaskRecoveryTestGrain(DurableTaskRecoveryProbe probe)
    : DurableGrain, IDurableTaskRecoveryTestGrain
{
    private readonly Guid _activationId = Guid.NewGuid();

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        probe.RecordActivation(this.GetGrainId());
        return base.OnActivateAsync(cancellationToken);
    }

    public DurableTask<int> ComputeAsync(int value)
    {
        probe.RecordInvocation(this.GetGrainId(), _activationId, value);
        return DurableTask.FromResult(checked((value * 3) + 7));
    }

    public async DurableTask<DateTimeOffset> ObserveLogicalTimeAsync()
    {
        var value = DurableExecutionContext.Current!.UtcNow;
        probe.RecordLogicalTime(this.GetGrainId(), value);
        await DurableTask.Delay(TimeSpan.FromHours(1)).WithId("wait");
        return value;
    }

    public async DurableTask<int> DelayThenComputeAsync(int value)
    {
        probe.RecordLogicalTime(this.GetGrainId(), DurableExecutionContext.Current!.UtcNow);
        await DurableTask.Delay(TimeSpan.FromSeconds(1)).WithId("pause");
        return checked((value * 3) + 7);
    }

    public async DurableTask<int> DelayAcrossDeactivationAsync(int value)
    {
        probe.RecordLogicalTime(this.GetGrainId(), DurableExecutionContext.Current!.UtcNow);
        await DurableTask.Delay(TimeSpan.FromHours(1)).WithId("pause");
        return checked((value * 3) + 7);
    }

    public Task<Guid> GetActivationIdAsync() => Task.FromResult(_activationId);

    public Task RequestDeactivationAsync()
    {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }
}

/// <summary>
/// Hosts an in-process cluster whose grains journal task state, messaging, and durable jobs in one journal format.
/// </summary>
internal sealed class DurableTaskRecoveryFixture : IAsyncLifetime
{
    public const string Json = "json";
    public const string OrleansBinary = "orleans-binary";

    public DurableTaskRecoveryFixture(string journalFormatKey, Action<ISiloBuilder>? configureSilo = null)
    {
        Probe = new();
        MessagingProbe = new();
        JobClock = new();
        Storage = new VolatileJournalStorageProvider(
            Options.Create(new JournaledStateManagerOptions { JournalFormatKey = journalFormatKey }));
        var clusterId = $"durable-task-recovery-{Guid.NewGuid():N}";
        var serviceId = $"durable-task-recovery-service-{Guid.NewGuid():N}";
        var builder = new InProcessTestClusterBuilder();
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
            siloBuilder.AddJournaling();
            siloBuilder.Services.Configure<JournaledStateManagerOptions>(options => options.JournalFormatKey = journalFormatKey);
            siloBuilder.UseInMemoryDurableJobs();
            siloBuilder.Services.AddKeyedSingleton<TimeProvider>(DurableJobTimeProviderNames.DurableJobs, JobClock);
            siloBuilder.AddDurableTasks();
            siloBuilder.Services.AddSingleton(Probe);
            siloBuilder.Services.AddSingleton(MessagingProbe);
            siloBuilder.Services.RemoveAll<IJournalStorageProvider>();
            siloBuilder.Services.RemoveAll<IJournalStorageCatalog>();
            siloBuilder.Services.AddSingleton(Storage);
            siloBuilder.Services.AddSingleton<IJournalStorageProvider>(
                services => services.GetRequiredService<VolatileJournalStorageProvider>());
            siloBuilder.Services.AddSingleton<IJournalStorageCatalog>(
                services => services.GetRequiredService<VolatileJournalStorageProvider>());
            configureSilo?.Invoke(siloBuilder);
        });
        Cluster = builder.Build();
    }

    public InProcessTestCluster Cluster { get; }
    public IClusterClient Client => Cluster.Client!;
    public DurableTaskRecoveryProbe Probe { get; }
    public DurableTaskMessagingProbe MessagingProbe { get; }

    /// <summary>Gets the Durable Jobs clock, which decides when scheduled messages such as delay resumes are due.</summary>
    public AdvanceableTimeProvider JobClock { get; }
    public VolatileJournalStorageProvider Storage { get; }

    public ValueTask InitializeAsync() => new(Cluster.DeployAsync());
    public ValueTask DisposeAsync() => Cluster.DisposeAsync();
}

/// <summary>
/// A clock which follows real time plus an offset that tests can advance.
/// </summary>
/// <remarks>
/// Timers and timestamps follow real time, so Durable Jobs keeps running; advancing moves due times into the past.
/// </remarks>
public sealed class AdvanceableTimeProvider : TimeProvider
{
    private long _offsetTicks;

    public override DateTimeOffset GetUtcNow() => base.GetUtcNow().AddTicks(Interlocked.Read(ref _offsetTicks));

    public void Advance(TimeSpan delta) => Interlocked.Add(ref _offsetTicks, delta.Ticks);
}
