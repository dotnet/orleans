using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Core.Internal;
using Orleans.TestingHost;
using Xunit;

namespace Orleans.Journaling.Tests;

[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Journaling")]
public sealed class JournaledGrainParticipantTests(JournaledGrainParticipantFixture fixture)
    : IClassFixture<JournaledGrainParticipantFixture>
{
    private static readonly string[] SuccessfulActivationEvents =
    [
        "first constructed", "second constructed", "failure constructed",
        "first initialized", "second initialized", "failure initialized",
        "before recovery", "activated"
    ];

    [Fact]
    public async Task DurableGrain_InitializesComposedParticipantsExactlyOnceBeforeRecovery()
    {
        var grain = fixture.Client.GetGrain<IComposedParticipantGrain>(Guid.NewGuid());

        var activationId = await grain.GetActivationId();
        Assert.Equal(SuccessfulActivationEvents, Assert.Single(fixture.GetEvents(grain.GetGrainId())));
        Assert.Equal(new string?[] { null, null }, await grain.GetActivationValues());
        await grain.SetValues("one", "two");
        Assert.Equal(new string?[] { "one", "two" }, await grain.GetValues());
        Assert.Equal(SuccessfulActivationEvents, Assert.Single(fixture.GetEvents(grain.GetGrainId())));

        await grain.Cast<IGrainManagementExtension>().DeactivateOnIdle(TestContext.Current.CancellationToken);

        Assert.NotEqual(activationId, await grain.GetActivationId());
        Assert.Equal(new string?[] { "one", "two" }, await grain.GetActivationValues());
        Assert.Equal(new string?[] { "one", "two" }, await grain.GetValues());
        var activations = fixture.GetEvents(grain.GetGrainId());
        Assert.Equal(2, activations.Length);
        Assert.All(activations, events => Assert.Equal(SuccessfulActivationEvents, events));
    }

    [Fact]
    public async Task DurableGrain_PropagatesParticipantConstructorFailure()
    {
        var grain = fixture.Client.GetGrain<IConstructorFailureParticipantGrain>(Guid.NewGuid());

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => grain.Ping());

        Assert.Contains(ParticipantFailure.ConstructorMessage, exception.ToString(), StringComparison.Ordinal);
        var activations = fixture.GetEvents(grain.GetGrainId());
        Assert.NotEmpty(activations);
        Assert.All(activations, events => Assert.Equal(
            ["first constructed", "second constructed", "failure constructed"], events));
    }

    [Fact]
    public async Task DurableGrain_PropagatesParticipantInitializeFailure()
    {
        var grain = fixture.Client.GetGrain<IInitializeFailureParticipantGrain>(Guid.NewGuid());

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => grain.Ping());

        Assert.Contains(ParticipantFailure.InitializeMessage, exception.ToString(), StringComparison.Ordinal);
        var activations = fixture.GetEvents(grain.GetGrainId());
        Assert.NotEmpty(activations);
        Assert.All(activations, events => Assert.Equal(
            ["first constructed", "second constructed", "failure constructed", "first initialized"], events));
    }

    [Fact]
    public async Task DurableGrain_InitializesParticipantsBeforePropagatingActivationFailure()
    {
        var grain = fixture.Client.GetGrain<IActivationFailureParticipantGrain>(Guid.NewGuid());

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => grain.Ping());

        Assert.Contains(ParticipantFailure.ActivationMessage, exception.ToString(), StringComparison.Ordinal);
        var activations = fixture.GetEvents(grain.GetGrainId());
        Assert.NotEmpty(activations);
        Assert.All(activations, events => Assert.Equal(
            [.. SuccessfulActivationEvents[..^1], "activation failed"], events));
    }

    [Fact]
    public async Task StateManagerFactory_RecoversExplicitJournalWithGrainParticipantsRegistered()
    {
        var services = fixture.Cluster.Silos.First().ServiceProvider;
        var factory = services.GetRequiredService<IJournaledStateManagerFactory>();
        var shared = services.GetRequiredService<JournaledStateManagerShared>();
        var journalId = new JournalId($"participant-factory-{Guid.NewGuid():N}");
        var cancellationToken = TestContext.Current.CancellationToken;

        await using (var manager = factory.Create(journalId))
        {
            var value = new DurableValue<string>("value", manager, shared, services);
            await manager.InitializeAsync(cancellationToken);
            value.Value = "persisted outside a grain";
            await manager.WriteStateAsync(cancellationToken);
        }

        await using (var manager = factory.Create(journalId))
        {
            var value = new DurableValue<string>("value", manager, shared, services);
            await manager.InitializeAsync(cancellationToken);
            Assert.Equal("persisted outside a grain", value.Value);
        }
    }
}

public sealed class JournaledGrainParticipantFixture : IntegrationTestFixture
{
    protected override void ConfigureTestCluster(InProcessTestClusterBuilder builder)
    {
        builder.ConfigureSilo((_, siloBuilder) =>
        {
            siloBuilder.Services.AddSingleton<ParticipantRecorder>();
            siloBuilder.Services.AddScoped<IJournaledGrainParticipant, FirstJournaledParticipant>();
            siloBuilder.Services.AddScoped<IJournaledGrainParticipant, SecondJournaledParticipant>();
            siloBuilder.Services.AddScoped<IJournaledGrainParticipant, ParticipantFailure>();
        });
    }

    public string[][] GetEvents(GrainId grainId) =>
        Cluster.Silos.SelectMany(silo =>
            silo.ServiceProvider.GetRequiredService<ParticipantRecorder>().GetEvents(grainId)).ToArray();
}

internal sealed class FirstJournaledParticipant : IJournaledGrainParticipant
{
    private readonly IGrainContext _grainContext;
    private readonly IServiceProvider _serviceProvider;
    private readonly ParticipantRecorder _recorder;

    public FirstJournaledParticipant(
        IGrainContext grainContext,
        IServiceProvider serviceProvider,
        ParticipantRecorder recorder)
    {
        _grainContext = grainContext;
        _serviceProvider = serviceProvider;
        _recorder = recorder;
        recorder.Record(grainContext, "first constructed");
    }

    public void Initialize()
    {
        _recorder.Record(_grainContext, "first initialized");
        if (_grainContext.GrainId.Type.ToString() == InitializeFailureParticipantGrain.GrainTypeName)
        {
            throw new InvalidOperationException(ParticipantFailure.InitializeMessage);
        }

        _ = _serviceProvider.GetRequiredKeyedService<IDurableValue<string>>("participant-one");
    }
}

internal sealed class SecondJournaledParticipant : IJournaledGrainParticipant
{
    private readonly IGrainContext _grainContext;
    private readonly IServiceProvider _serviceProvider;
    private readonly ParticipantRecorder _recorder;

    public SecondJournaledParticipant(
        IGrainContext grainContext,
        IServiceProvider serviceProvider,
        ParticipantRecorder recorder)
    {
        _grainContext = grainContext;
        _serviceProvider = serviceProvider;
        _recorder = recorder;
        recorder.Record(grainContext, "second constructed");
    }

    public void Initialize()
    {
        _recorder.Record(_grainContext, "second initialized");
        _ = _serviceProvider.GetRequiredKeyedService<IDurableValue<string>>("participant-two");
    }
}

internal sealed class ParticipantFailure : IJournaledGrainParticipant
{
    public const string ConstructorMessage = "Expected participant constructor failure.";
    public const string InitializeMessage = "Expected participant initialization failure.";
    public const string ActivationMessage = "Expected grain activation failure.";
    private readonly IGrainContext _grainContext;
    private readonly ParticipantRecorder _recorder;

    public ParticipantFailure(IGrainContext grainContext, ParticipantRecorder recorder)
    {
        _grainContext = grainContext;
        _recorder = recorder;
        recorder.Record(grainContext, "failure constructed");
        if (grainContext.GrainId.Type.ToString() == ConstructorFailureParticipantGrain.GrainTypeName)
        {
            throw new InvalidOperationException(ConstructorMessage);
        }

        grainContext.ObservableLifecycle.Subscribe(
            nameof(ParticipantFailure),
            GrainLifecycleStage.SetupState - 1,
            cancellationToken =>
            {
                var manager = grainContext.ActivationServices.GetRequiredService<IJournaledStateManager>();
                Assert.True(manager.TryGetState("participant-one", out _));
                Assert.True(manager.TryGetState("participant-two", out _));
                recorder.Record(grainContext, "before recovery");
                return Task.CompletedTask;
            });
    }

    public void Initialize() => _recorder.Record(_grainContext, "failure initialized");
}

public sealed class ParticipantRecorder
{
    private readonly ConcurrentDictionary<GrainId, ConcurrentDictionary<ActivationId, ConcurrentQueue<string>>> _events = new();

    public void Record(IGrainContext grainContext, string value) =>
        _events.GetOrAdd(grainContext.GrainId, static _ => new())
            .GetOrAdd(grainContext.ActivationId, static _ => new()).Enqueue(value);

    public IEnumerable<string[]> GetEvents(GrainId grainId) =>
        _events.TryGetValue(grainId, out var activations)
            ? activations.Values.Select(events => events.ToArray())
            : [];
}

[GrainType(GrainTypeName)]
public sealed class ComposedParticipantGrain : DurableGrain, IComposedParticipantGrain
{
    public const string GrainTypeName = "journaling-composed-participant";
    private readonly Guid _activationId = Guid.NewGuid();
    private string?[] _activationValues = [];

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _activationValues = ReadValues();
        ServiceProvider.GetRequiredService<ParticipantRecorder>().Record(GrainContext, "activated");
        return Task.CompletedTask;
    }

    public async Task SetValues(string first, string second)
    {
        GetValue("participant-one").Value = first;
        GetValue("participant-two").Value = second;
        await WriteStateAsync();
    }

    public Task<string?[]> GetValues() => Task.FromResult(ReadValues());

    public Task<string?[]> GetActivationValues() => Task.FromResult(_activationValues);

    private string?[] ReadValues() => [GetValue("participant-one").Value, GetValue("participant-two").Value];

    public Task<Guid> GetActivationId() => Task.FromResult(_activationId);

    private IDurableValue<string> GetValue(string name)
    {
        AssertStateExists(name, StateManager.TryGetState(name, out var state));
        return state as IDurableValue<string>
            ?? throw new InvalidOperationException($"The participant state '{name}' has an unexpected type.");

        static void AssertStateExists(string name, bool exists)
        {
            if (!exists)
            {
                throw new InvalidOperationException($"The participant state '{name}' was not initialized.");
            }
        }
    }
}

[GrainType(GrainTypeName)]
public sealed class ConstructorFailureParticipantGrain : DurableGrain, IConstructorFailureParticipantGrain
{
    public const string GrainTypeName = "journaling-constructor-failure-participant";

    public Task Ping() => Task.CompletedTask;
}

[GrainType(GrainTypeName)]
public sealed class InitializeFailureParticipantGrain : DurableGrain, IInitializeFailureParticipantGrain
{
    public const string GrainTypeName = "journaling-initialize-failure-participant";

    public Task Ping() => Task.CompletedTask;
}

[GrainType(GrainTypeName)]
public sealed class ActivationFailureParticipantGrain : DurableGrain, IActivationFailureParticipantGrain
{
    public const string GrainTypeName = "journaling-activation-failure-participant";

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        ServiceProvider.GetRequiredService<ParticipantRecorder>().Record(GrainContext, "activation failed");
        return Task.FromException(new InvalidOperationException(ParticipantFailure.ActivationMessage));
    }

    public Task Ping() => Task.CompletedTask;
}

public interface IComposedParticipantGrain : IGrainWithGuidKey
{
    Task SetValues(string first, string second);
    Task<string?[]> GetValues();
    Task<string?[]> GetActivationValues();
    Task<Guid> GetActivationId();
}

public interface IConstructorFailureParticipantGrain : IGrainWithGuidKey
{
    Task Ping();
}

public interface IInitializeFailureParticipantGrain : IGrainWithGuidKey
{
    Task Ping();
}

public interface IActivationFailureParticipantGrain : IGrainWithGuidKey
{
    Task Ping();
}
