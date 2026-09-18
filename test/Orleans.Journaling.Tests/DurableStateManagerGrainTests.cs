using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Orleans.Core.Internal;
using Orleans.Hosting;
using Orleans.Journaling.Json;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.TestingHost;
using Xunit;

namespace Orleans.Journaling.Tests;

[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Journaling")]
public sealed class DurableStateManagerGrainTests(DurableStateManagerIntegrationFixture fixture)
    : IClassFixture<DurableStateManagerIntegrationFixture>
{
    [Fact]
    public async Task Lifecycle_RepeatedHostingAndParticipation_SubscribeSetupStateOnce()
    {
        var builder = new LifecycleTestSiloBuilder();
        builder.Services.AddSerializer();
        builder.Services.AddLogging();
        builder.Services.AddKeyedSingleton<TimeProvider>(JournalingTimeProviderNames.Journaling, TimeProvider.System);
        builder.Services.Configure<JsonJournalOptions>(
            options => options.AddTypeInfoResolver(JournalingTestsJsonContext.Default));
        builder.AddJournaling();
        builder.AddJournaling();
        builder.AddJournaling();

        Assert.DoesNotContain(builder.Services, descriptor =>
            descriptor.ServiceType == typeof(IConfigureGrainTypeComponents));

        // Keep the real empty-journal read protocol, while counting reads independently
        // of recovery completion so an extra read cannot hide behind an idempotent observer.
        var innerStorage = new VolatileJournalStorage();
        var storage = Substitute.For<IJournalStorage>();
        storage.ReadAsync(Arg.Any<IJournalStorageConsumer>(), Arg.Any<CancellationToken>())
            .Returns(call => innerStorage.ReadAsync(
                call.ArgAt<IJournalStorageConsumer>(0), call.ArgAt<CancellationToken>(1)));
        var storageProvider = Substitute.For<IJournalStorageProvider>();
        storageProvider.CreateStorage(Arg.Any<JournalId>()).Returns(storage);
        builder.Services.AddSingleton(storageProvider);

        await using var services = builder.Services.BuildServiceProvider();
        var journalId = new JournalId($"lifecycle/{Guid.NewGuid():N}");
        await using var manager = services.GetRequiredService<IJournaledStateManagerFactory>().CreateStandalone(journalId);
        var probe = new DurableManagerRecoveryProbe();
        manager.RegisterStateMachine("recovery", probe);
        var lifecycle = Substitute.For<IGrainLifecycle>();
        var subscriptions = new List<(int Stage, ILifecycleObserver Observer)>();
        lifecycle.Subscribe(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<ILifecycleObserver>())
            .Returns(call =>
            {
                subscriptions.Add((call.ArgAt<int>(1), call.ArgAt<ILifecycleObserver>(2)));
                return Substitute.For<IDisposable>();
            });

        var participant = Assert.IsAssignableFrom<ILifecycleParticipant<IGrainLifecycle>>(manager);
        participant.Participate(lifecycle);
        participant.Participate(lifecycle);
        participant.Participate(lifecycle);

        var subscription = Assert.Single(subscriptions);
        Assert.Equal(GrainLifecycleStage.SetupState, subscription.Stage);
        Assert.Same(manager, subscription.Observer);
        Assert.Equal(0, probe.ResetCount);
        Assert.Equal(0, probe.RecoveryCount);

        var token = TestContext.Current.CancellationToken;
        await AwaitLifecycleAsync(subscription.Observer.OnStart(token), $"first start of {journalId}");
        Assert.Equal(1, probe.ResetCount);
        Assert.Equal(1, probe.RecoveryCount);

        await AwaitLifecycleAsync(subscription.Observer.OnStart(token), $"repeated start of {journalId}");
        participant.Participate(lifecycle);
        Assert.Single(subscriptions);
        await storage.Received(1).ReadAsync(Arg.Any<IJournalStorageConsumer>(), Arg.Any<CancellationToken>());
        Assert.Equal(1, probe.ResetCount);
        Assert.Equal(1, probe.RecoveryCount);
        Assert.Equal(0, probe.WriteCompletionCount);
        Assert.True(manager.TryGetStateMachine("recovery", out var stateMachine));
        Assert.Same(probe, stateMachine);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InjectedManager_RecoversBeforeApplicationUse_ExactlyOnce(bool directGrainBase)
    {
        var key = Guid.NewGuid();
        IDurableManagerGrainProbe grain = directGrainBase
            ? fixture.Client.GetGrain<IDirectManagerGrain>(key)
            : fixture.Client.GetGrain<IInjectedManagerGrain>(key);
        var phase = "first application read on an empty journal";

        try
        {
            await VerifyActivationsAsync().WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException(
                $"Timed out during {phase}; grain key={key}, direct IGrainBase={directGrainBase}.", exception);
        }

        async Task VerifyActivationsAsync()
        {
            Assert.Equal(0, await grain.GetValue());
            Assert.Equal(1, await grain.GetFirstReadRecoveryCount());
            Assert.Empty(await grain.GetItems());
            Assert.Equal(1, await grain.GetRecoveryCount());
            Assert.Equal(1, await grain.GetResetCount());
            Assert.Equal(0, await grain.GetWriteCompletionCount());
            Assert.True(await grain.HasConvergedReferences());
            var originalActivation = await grain.GetActivationId();

            phase = "one shared write of the value and list";
            await grain.StageAndWriteAsync(42, "saved");
            Assert.Equal(1, await grain.GetWriteCompletionCount());
            Assert.Equal(1, await grain.GetRecoveryCount());

            phase = "deactivation after the acknowledged shared write";
            await grain.Cast<IGrainManagementExtension>().DeactivateOnIdle(TestContext.Current.CancellationToken);

            phase = "first application read on a fresh activation";
            // Do not call another grain method before this read: recovery must precede it.
            Assert.Equal(42, await grain.GetValue());
            Assert.Equal(1, await grain.GetFirstReadRecoveryCount());
            Assert.Equal(new[] { "saved" }, await grain.GetItems());
            var recoveredActivation = await grain.GetActivationId();
            Assert.NotEqual(originalActivation, recoveredActivation);
            Assert.Equal(1, await grain.GetRecoveryCount());
            Assert.Equal(0, await grain.GetWriteCompletionCount());
            Assert.True(await grain.HasConvergedReferences());

            phase = "repeated application reads without reinitialization";
            var resetCountAfterRecovery = await grain.GetResetCount();
            Assert.Equal(42, await grain.GetValue());
            Assert.Equal(new[] { "saved" }, await grain.GetItems());
            Assert.Equal(recoveredActivation, await grain.GetActivationId());
            Assert.Equal(1, await grain.GetRecoveryCount());
            Assert.Equal(resetCountAfterRecovery, await grain.GetResetCount());
            Assert.Equal(0, await grain.GetWriteCompletionCount());
        }
    }

    private static async Task AwaitLifecycleAsync(Task operation, string phase)
    {
        try
        {
            await operation.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException($"Timed out awaiting lifecycle {phase}.", exception);
        }
    }

    private sealed class LifecycleTestSiloBuilder : ISiloBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();
        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
    }
}

public sealed class DurableStateManagerIntegrationFixture : IntegrationTestFixture
{
    protected override void ConfigureTestCluster(InProcessTestClusterBuilder builder)
    {
        // The base constructor calls this hook. Do not depend on derived instance fields.
        builder.ConfigureSilo((_, siloBuilder) =>
        {
            siloBuilder.AddJournaling();
            siloBuilder.AddJournaling();
            siloBuilder.Services.AddStateMachine<DurableManagerRecoveryProbe, DurableManagerRecoveryProbe>();
        });
    }
}

public interface IDurableManagerGrainProbe : IGrainWithGuidKey
{
    Task<Guid> GetActivationId();
    Task<int> GetValue();
    Task<string[]> GetItems();
    Task<int> GetFirstReadRecoveryCount();
    Task<int> GetRecoveryCount();
    Task<int> GetResetCount();
    Task<int> GetWriteCompletionCount();
    Task<bool> HasConvergedReferences();
    Task StageAndWriteAsync(int value, string item);
}

public interface IInjectedManagerGrain : IDurableManagerGrainProbe
{
}

public interface IDirectManagerGrain : IDurableManagerGrainProbe
{
}

public sealed class InjectedManagerGrain : Grain, IInjectedManagerGrain
{
    private readonly DurableManagerGrainState _state;

    public InjectedManagerGrain(
        IDurableStateManager manager,
        IJournaledStateManager owner,
        [FromKeyedServices("value")] IDurableValue<int> value,
        [FromKeyedServices("list")] IDurableList<string> list)
    {
        _state = new(manager, owner, value, list);
    }

    public Task<Guid> GetActivationId() => Task.FromResult(_state.ActivationId);
    public Task<int> GetValue() => Task.FromResult(_state.ReadValue());
    public Task<string[]> GetItems() => Task.FromResult(_state.Items.ToArray());
    public Task<int> GetFirstReadRecoveryCount() => Task.FromResult(_state.FirstReadRecoveryCount);
    public Task<int> GetRecoveryCount() => Task.FromResult(_state.Probe.RecoveryCount);
    public Task<int> GetResetCount() => Task.FromResult(_state.Probe.ResetCount);
    public Task<int> GetWriteCompletionCount() => Task.FromResult(_state.Probe.WriteCompletionCount);
    public Task<bool> HasConvergedReferences() => Task.FromResult(_state.HasConvergedReferences());
    public Task StageAndWriteAsync(int value, string item) => _state.StageAndWriteAsync(value, item);
}

public sealed class DirectManagerGrain : IGrainBase, IDirectManagerGrain
{
    private readonly DurableManagerGrainState _state;

    public DirectManagerGrain(
        IGrainContext grainContext,
        IDurableStateManager manager,
        IJournaledStateManager owner,
        [FromKeyedServices("value")] IDurableValue<int> value,
        [FromKeyedServices("list")] IDurableList<string> list)
    {
        GrainContext = grainContext;
        _state = new(manager, owner, value, list);
    }

    public IGrainContext GrainContext { get; }
    public Task<Guid> GetActivationId() => Task.FromResult(_state.ActivationId);
    public Task<int> GetValue() => Task.FromResult(_state.ReadValue());
    public Task<string[]> GetItems() => Task.FromResult(_state.Items.ToArray());
    public Task<int> GetFirstReadRecoveryCount() => Task.FromResult(_state.FirstReadRecoveryCount);
    public Task<int> GetRecoveryCount() => Task.FromResult(_state.Probe.RecoveryCount);
    public Task<int> GetResetCount() => Task.FromResult(_state.Probe.ResetCount);
    public Task<int> GetWriteCompletionCount() => Task.FromResult(_state.Probe.WriteCompletionCount);
    public Task<bool> HasConvergedReferences() => Task.FromResult(_state.HasConvergedReferences());
    public Task StageAndWriteAsync(int value, string item) => _state.StageAndWriteAsync(value, item);
}

internal sealed class DurableManagerGrainState
{
    private readonly IDurableStateManager _manager;
    private readonly IJournaledStateManager _owner;
    private readonly IDurableValue<int> _injectedValue;
    private readonly IDurableList<string> _injectedList;
    private readonly IDurableValue<int> _programmaticValue;

    public DurableManagerGrainState(
        IDurableStateManager manager,
        IJournaledStateManager owner,
        IDurableValue<int> value,
        IDurableList<string> list)
    {
        _manager = manager;
        _owner = owner;
        _injectedValue = value;
        _injectedList = list;
        _programmaticValue = manager.GetOrAddValue<int>("value");
        Items = manager.GetOrAddList<string>("list");
        Probe = manager.GetOrAddState<DurableManagerRecoveryProbe>("recovery");
    }

    public Guid ActivationId { get; } = Guid.NewGuid();
    public IDurableList<string> Items { get; }
    public DurableManagerRecoveryProbe Probe { get; }
    public int FirstReadRecoveryCount { get; private set; } = -1;

    public int ReadValue()
    {
        if (FirstReadRecoveryCount == -1)
        {
            FirstReadRecoveryCount = Probe.RecoveryCount;
        }

        return _injectedValue.Value;
    }

    public bool HasConvergedReferences() =>
        ReferenceEquals(_manager, _owner)
        && ReferenceEquals(_injectedValue, _programmaticValue)
        && ReferenceEquals(_injectedList, Items)
        && ReferenceEquals(_injectedValue, _manager.GetOrAddValue<int>("value"))
        && ReferenceEquals(_injectedList, _manager.GetOrAddList<string>("list"))
        && ReferenceEquals(Probe, _manager.GetOrAddState<DurableManagerRecoveryProbe>("recovery"));

    public async Task StageAndWriteAsync(int value, string item)
    {
        _injectedValue.Value = value;
        _injectedList.Add(item);
        await _manager.WriteStateAsync();
    }
}

public sealed class DurableManagerRecoveryProbe : IStateMachine
{
    public int ResetCount { get; private set; }
    public int RecoveryCount { get; private set; }
    public int WriteCompletionCount { get; private set; }

    public void Reset(JournalStreamWriter writer) => ResetCount++;
    public void OnRecoveryCompleted() => RecoveryCount++;
    public void OnWriteCompleted() => WriteCompletionCount++;

    public void ReplayEntry(JournalEntry entry, JournalReplayContext context) =>
        throw new InvalidOperationException("The lifecycle probe does not write journal entries.");

    public void WritePendingEntries(JournalStreamWriter writer)
    {
    }

    public void WriteSnapshot(JournalStreamWriter writer)
    {
    }
}
