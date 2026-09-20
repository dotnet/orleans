using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Concurrency;
using Orleans.Journaling;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Serialization.Invocation;

namespace Orleans.DurableMessaging.Tests.Support;

public sealed class BootstrapProbe
{
    private readonly ConcurrentDictionary<GrainId, ConcurrentQueue<BootstrapObservation>> _activations = new();
    public void Add(BootstrapObservation observation) =>
        _activations.GetOrAdd(observation.Context.GrainId, static _ => new()).Enqueue(observation);
    public BootstrapObservation[] Get(GrainId id) => _activations[id].ToArray();
}

public sealed class BootstrapObservation : IDisposable
{
    public BootstrapObservation(IGrainContext context, BootstrapProbe probe)
    {
        Context = context;
        probe.Add(this);
    }

    public IGrainContext Context { get; }
    public object? ConstructedGrain { get; private set; }
    public bool InstanceAvailableInConstructor { get; private set; }
    public IJournaledStateManager? Manager { get; set; }
    public IDurableValue<int>? Value { get; set; }
    public IDurableInbox? Inbox { get; set; }
    public IDurableOutbox? Outbox { get; set; }
    public object? Extension { get; set; }
    public Exception? ExpectedFailure { get; set; }
    public int Activations { get; set; }
    public int GrainDisposals { get; set; }
    public int Disposals { get; private set; }

    public void Constructed(object grain)
    {
        ConstructedGrain = grain;
        InstanceAvailableInConstructor = Context.GrainInstance is not null;
    }

    public void Dispose() => Disposals++;
}

public sealed class BootstrapState : IInboxHandler, IDisposable
{
    public const string Route = "bootstrap";
    private readonly HandlerProbe _handlers;
    public BootstrapState(BootstrapObservation observation, IJournaledStateManager manager, HandlerProbe handlers,
        [FromKeyedServices("bootstrap-value")] IDurableValue<int> value, IDurableInbox inbox, IDurableOutbox outbox)
    {
        _handlers = handlers;
        Observation = observation;
        observation.Manager = manager;
        observation.Value = value;
        observation.Inbox = inbox;
        observation.Outbox = outbox;
        var journal = (ObservedJournalValue<int>)value;
        journal.Initializing = OnRecoveryStarted;
        journal.Recovered = OnRecoveryCompleted;
        journal.Written = OnWriteCompleted;
        inbox.RegisterHandler(Route, this);
    }

    public BootstrapObservation Observation { get; }
    public int RecoveryStarts { get; private set; }
    public int RecoveryCompletions { get; private set; }
    public int PrimaryStateCountAtRecovery { get; private set; }
    public object? GrainAtRecovery { get; private set; }
    public int ActivationValue { get; private set; }
    public int HandlerCalls { get; private set; }
    public int Disposals { get; private set; }
    public TaskCompletionSource Handled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<int> Read() => Task.FromResult(Observation.Value!.Value);
    public async Task Set(int value)
    {
        Observation.Value!.Value = value;
        await Observation.Manager!.WriteStateAsync(CancellationToken.None);
    }
    public Task Activate()
    {
        Observation.Activations++;
        ActivationValue = Observation.Value!.Value;
        return Task.CompletedTask;
    }
    public void OnRecoveryStarted()
    {
        RecoveryStarts++;
        GrainAtRecovery = Observation.Context.GrainInstance;
        PrimaryStateCountAtRecovery = ReadMessagingStates(Observation.Manager!).Count(static observer =>
            observer.GetType().Name == "InboxJournalState");
    }
    public void OnRecoveryCompleted() => RecoveryCompletions++;
    public void OnWriteStarted() { }
    public void OnWriteCompleted()
    {
        if (HandlerCalls > 0) Handled.TrySetResult();
    }
    public bool CanHandle(IInboxHandlerContext context) => context.Envelope.RouteKey == Route;
    public async ValueTask<Action> PrepareAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
    {
        if (_handlers.TryGet(context.GrainId, Route, out var barrier))
        {
            barrier.Entered.TrySetResult();
            await barrier.Continue.Task.WaitAsync(cancellationToken);
        }
        var value = Observation.Value!.Value + 1;
        var outgoing = context.CreateEnvelope().To(GrainId.Create("bootstrap-output", "capture"), "output").WithBody(value).Build();
        return () =>
        {
            Observation.Value.Value = value;
            context.Send(outgoing);
            HandlerCalls++;
        };
    }
    public void Dispose() => Disposals++;
    public static IEnumerable<IStateMachine> ReadMessagingStates(IJournaledStateManager manager)
    {
        if (manager.TryGetStateMachine("__orleans.durable-messaging.inbox", out var inbox)) yield return inbox;
        if (manager.TryGetStateMachine("test-handler-output", out var outbox)) yield return outbox;
    }

}

public interface IBootstrapTestGrain : IGrainWithGuidKey
{
    Task<int> GetValueAsync();
    Task SetValueAsync(int value);
    Task DeactivateAsync();
}
public interface IMarkedBootstrapTestGrain : IBootstrapTestGrain, IDurableMessagingGrain;
public interface IGenericBootstrapTestGrain<T> : IBootstrapTestGrain;

public abstract class BootstrapGrainBase : Grain, IBootstrapTestGrain, IDisposable
{
    private readonly BootstrapState _state;
    protected BootstrapGrainBase(BootstrapState state)
    {
        _state = state;
        state.Observation.Constructed(this);
    }
    public override Task OnActivateAsync(CancellationToken cancellationToken) => _state.Activate();
    public Task<int> GetValueAsync() => _state.Read();
    public Task SetValueAsync(int value) => _state.Set(value);
    public Task DeactivateAsync() { DeactivateOnIdle(); return Task.CompletedTask; }
    public void Dispose() => _state.Observation.GrainDisposals++;
}
public sealed class PlainBootstrapGrain(BootstrapState state) : BootstrapGrainBase(state), IDurableMessagingGrain;
public abstract class ApplicationBootstrapBase(BootstrapState state) : BootstrapGrainBase(state), IDurableMessagingGrain;
public sealed class ApplicationBootstrapGrain(BootstrapState state) : ApplicationBootstrapBase(state);
public sealed class InterfaceBootstrapGrain(BootstrapState state) : BootstrapGrainBase(state), IMarkedBootstrapTestGrain;
public sealed class GenericBootstrapGrain<T>(BootstrapState state) : BootstrapGrainBase(state), IGenericBootstrapTestGrain<T>, IDurableMessagingGrain;

public class LegacyBootstrapGrain : DurableGrain, IBootstrapTestGrain, IDisposable
{
    private readonly BootstrapState _state;
    public LegacyBootstrapGrain(BootstrapState state)
    {
        _state = state;
        state.Observation.Constructed(this);
    }
    public override Task OnActivateAsync(CancellationToken cancellationToken) => _state.Activate();
    public Task<int> GetValueAsync() => _state.Read();
    public Task SetValueAsync(int value) => _state.Set(value);
    public Task DeactivateAsync() { DeactivateOnIdle(); return Task.CompletedTask; }
    public void Dispose() => _state.Observation.GrainDisposals++;
}

public sealed class LegacyMarkedBootstrapGrain(BootstrapState state) : LegacyBootstrapGrain(state), IDurableMessagingGrain;

public interface IBootstrapControlGrain : IGrainWithGuidKey { Task PingAsync(); }
public interface IInterleavingBootstrapControlGrain : IBootstrapControlGrain
{
    [AlwaysInterleave] Task InterleaveAsync();
}
public abstract class BootstrapControlGrain : Grain, IBootstrapControlGrain, IDisposable
{
    protected BootstrapObservation Observation { get; }
    protected BootstrapControlGrain(BootstrapObservation observation)
    {
        Observation = observation;
        observation.Constructed(this);
    }
    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        Observation.Activations++;
        return Task.CompletedTask;
    }
    public Task PingAsync() => Task.CompletedTask;
    public void Dispose() => Observation.GrainDisposals++;
}
public sealed class EagerBootstrapGrain(BootstrapObservation observation) : BootstrapControlGrain(observation), IDurableMessagingGrain;
public sealed class UnselectedBootstrapGrain(BootstrapObservation observation) : BootstrapControlGrain(observation);
public sealed class FailingBootstrapGrain(BootstrapObservation observation) : BootstrapControlGrain(observation), IDurableMessagingGrain;
public sealed class UnselectedEndpointBootstrapGrain : BootstrapControlGrain
{
    public UnselectedEndpointBootstrapGrain(BootstrapObservation observation,
        [FromKeyedServices(typeof(IDurableInboxExtension))] IGrainExtension extension) : base(observation) => observation.Extension = extension;
}
public sealed class JournalOnlyBootstrapGrain : BootstrapControlGrain
{
    public JournalOnlyBootstrapGrain(BootstrapObservation observation, IJournaledStateManager manager,
        [FromKeyedServices("bootstrap-journal-only")] IDurableValue<int> value) : base(observation)
    {
        observation.Manager = manager;
        observation.Value = value;
    }
}
[Reentrant]
public sealed class ReentrantBootstrapGrain(BootstrapObservation observation) : BootstrapControlGrain(observation), IDurableMessagingGrain;
[StatelessWorker]
public sealed class StatelessBootstrapGrain(BootstrapObservation observation) : BootstrapControlGrain(observation), IDurableMessagingGrain;
[MayInterleave(nameof(Interleave))]
public sealed class MayInterleaveBootstrapGrain(BootstrapObservation observation) : BootstrapControlGrain(observation), IDurableMessagingGrain
{
    public static bool Interleave(IInvokable request) => true;
}
public sealed class AlwaysInterleaveBootstrapGrain(BootstrapObservation observation)
    : BootstrapControlGrain(observation), IInterleavingBootstrapControlGrain, IDurableMessagingGrain
{
    public Task InterleaveAsync() => Task.CompletedTask;
}

[ExecutionProperty(WellKnownGrainTypeProperties.Reentrant, "true")]
public sealed class MetadataReentrantBootstrapGrain(BootstrapObservation observation)
    : BootstrapControlGrain(observation), IDurableMessagingGrain;

[ExecutionProperty(WellKnownGrainTypeProperties.MayInterleavePredicate, nameof(Interleave))]
public sealed class MetadataMayInterleaveBootstrapGrain(BootstrapObservation observation)
    : BootstrapControlGrain(observation), IDurableMessagingGrain
{
    public static bool Interleave(IInvokable request) => true;
}

[ExecutionProperty(WellKnownGrainTypeProperties.PlacementStrategy, "StatelessWorkerPlacement")]
public sealed class MetadataStatelessPlacementBootstrapGrain(BootstrapObservation observation)
    : BootstrapControlGrain(observation), IDurableMessagingGrain;

[ExecutionProperty(WellKnownGrainTypeProperties.PlacementStrategy, BootstrapClusterFixture.StatelessPlacementAlias)]
public sealed class AliasedStatelessPlacementBootstrapGrain(BootstrapObservation observation)
    : BootstrapControlGrain(observation), IDurableMessagingGrain;

[ExecutionProperty(WellKnownGrainTypeProperties.PlacementStrategy, nameof(RandomPlacement))]
public sealed class MetadataOrdinaryPlacementBootstrapGrain(BootstrapObservation observation)
    : BootstrapControlGrain(observation), IDurableMessagingGrain;

[ExecutionProperty(WellKnownGrainTypeProperties.PlacementStrategy, BootstrapClusterFixture.OrdinaryPlacementAlias)]
public sealed class AliasedOrdinaryPlacementBootstrapGrain(BootstrapObservation observation)
    : BootstrapControlGrain(observation), IDurableMessagingGrain;

public sealed class BootstrapClusterFixture : DurableMessagingClusterFixture
{
    public const string StatelessPlacementAlias = "bootstrap-worker-alias";
    public const string OrdinaryPlacementAlias = "bootstrap-directory-alias";
    public BootstrapProbe Probe { get; } = new();
    protected override void ConfigureServices(IServiceCollection services)
    {
        ReceiverTestServices.Add(services, ConfigureOptions);
        services.AddKeyedSingleton<PlacementStrategy>(StatelessPlacementAlias, new StatelessWorkerAttribute(1).PlacementStrategy);
        services.AddKeyedSingleton<PlacementStrategy>(OrdinaryPlacementAlias, new RandomPlacement());
        services.AddSingleton(Probe);
        services.AddScoped<BootstrapObservation>();
        services.AddScoped<BootstrapState>();
        var extensionType = ReceiverTestServices.GetImplementationType("DurableInboxExtension");
        var descriptor = services.Last(entry => entry.ServiceType == extensionType);
        services.AddScoped(extensionType, provider =>
        {
            var extension = descriptor.ImplementationFactory!(provider);
            var context = provider.GetRequiredService<IGrainContext>();
            if (context.GrainInstance is FailingBootstrapGrain)
            {
                var observation = provider.GetRequiredService<BootstrapObservation>();
                observation.Manager = provider.GetRequiredService<IJournaledStateManager>();
                observation.Inbox = provider.GetRequiredService<IDurableInbox>();
                observation.Outbox = provider.GetRequiredService<IDurableOutbox>();
                observation.Extension = extension;
                observation.ExpectedFailure = new IOException("Expected bootstrap runtime construction failure.");
                ((IDisposable)extension).Dispose();
                throw observation.ExpectedFailure;
            }
            return extension;
        });
    }
}
