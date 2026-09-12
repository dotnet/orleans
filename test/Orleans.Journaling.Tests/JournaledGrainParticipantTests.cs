using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Core.Internal;
using Orleans.TestingHost;
using Xunit;

namespace Orleans.Journaling.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Journaling")]
public sealed class JournaledGrainParticipantTests(JournaledGrainParticipantFixture fixture)
    : IClassFixture<JournaledGrainParticipantFixture>
{
    [Fact]
    public async Task DurableGrain_InitializesComposedParticipantsExactlyOnceBeforeRecovery()
    {
        var grain = fixture.Client.GetGrain<IComposedParticipantGrain>(Guid.NewGuid());
        var beforeFirstActivation = fixture.GetCounts(ComposedParticipantGrain.GrainTypeName);

        var activationId = await grain.GetActivationId();
        var afterFirstActivation = fixture.GetCounts(ComposedParticipantGrain.GrainTypeName);
        AssertActivationDelta(beforeFirstActivation, afterFirstActivation);
        await grain.SetValues("one", "two");
        Assert.Equal(["one", "two"], await grain.GetValues());

        await grain.Cast<IGrainManagementExtension>().DeactivateOnIdle(TestContext.Current.CancellationToken);

        Assert.NotEqual(activationId, await grain.GetActivationId());
        Assert.Equal(["one", "two"], await grain.GetValues());
        AssertActivationDelta(
            afterFirstActivation,
            fixture.GetCounts(ComposedParticipantGrain.GrainTypeName));

        static void AssertActivationDelta(ParticipantCounts beforeActivation, ParticipantCounts afterActivation)
        {
            Assert.Equal(
                new ParticipantCounts(
                    FirstConstructions: 1,
                    SecondConstructions: 1,
                    FailureConstructions: 1,
                    FirstInitializations: 1,
                    SecondInitializations: 1),
                afterActivation - beforeActivation);
        }
    }

    [Fact]
    public async Task DurableGrain_PropagatesParticipantConstructorFailure()
    {
        var grain = fixture.Client.GetGrain<IConstructorFailureParticipantGrain>(Guid.NewGuid());

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => grain.Ping());

        Assert.Contains(ParticipantFailure.ConstructorMessage, exception.ToString(), StringComparison.Ordinal);
        var counts = fixture.GetCounts(ConstructorFailureParticipantGrain.GrainTypeName);
        Assert.True(counts.FailureConstructions > 0);
        Assert.Equal(0, counts.FirstInitializations + counts.SecondInitializations);
    }

    [Fact]
    public async Task DurableGrain_PropagatesParticipantInitializeFailure()
    {
        var grain = fixture.Client.GetGrain<IInitializeFailureParticipantGrain>(Guid.NewGuid());

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => grain.Ping());

        Assert.Contains(ParticipantFailure.InitializeMessage, exception.ToString(), StringComparison.Ordinal);
        var counts = fixture.GetCounts(InitializeFailureParticipantGrain.GrainTypeName);
        Assert.True(counts.FirstInitializations > 0);
        Assert.Equal(counts.FirstConstructions, counts.FirstInitializations);
        Assert.Equal(0, counts.SecondInitializations);
    }

    [Fact]
    public async Task DurableGrain_InitializesParticipantsBeforePropagatingActivationFailure()
    {
        var grain = fixture.Client.GetGrain<IActivationFailureParticipantGrain>(Guid.NewGuid());

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => grain.Ping());

        Assert.Contains(ParticipantFailure.ActivationMessage, exception.ToString(), StringComparison.Ordinal);
        var counts = fixture.GetCounts(ActivationFailureParticipantGrain.GrainTypeName);
        Assert.True(counts.FirstInitializations > 0);
        Assert.Equal(counts.FirstConstructions, counts.FirstInitializations);
        Assert.Equal(counts.SecondConstructions, counts.SecondInitializations);
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

    public ParticipantCounts GetCounts(string grainType)
    {
        var result = new ParticipantCounts();
        foreach (var silo in Cluster.Silos)
        {
            result += silo.ServiceProvider.GetRequiredService<ParticipantRecorder>().GetCounts(grainType);
        }

        return result;
    }
}

internal sealed class FirstJournaledParticipant : IJournaledGrainParticipant
{
    private readonly string _grainType;
    private readonly IServiceProvider _serviceProvider;
    private readonly ParticipantRecorder _recorder;

    public FirstJournaledParticipant(
        IGrainContext grainContext,
        IServiceProvider serviceProvider,
        ParticipantRecorder recorder)
    {
        _grainType = grainContext.GrainId.Type.ToString();
        _serviceProvider = serviceProvider;
        _recorder = recorder;
        recorder.RecordFirstConstruction(_grainType);
    }

    public void Initialize()
    {
        _recorder.RecordFirstInitialization(_grainType);
        if (_grainType == InitializeFailureParticipantGrain.GrainTypeName)
        {
            throw new InvalidOperationException(ParticipantFailure.InitializeMessage);
        }

        _ = _serviceProvider.GetRequiredKeyedService<IDurableValue<string>>("participant-one");
    }
}

internal sealed class SecondJournaledParticipant : IJournaledGrainParticipant
{
    private readonly string _grainType;
    private readonly IServiceProvider _serviceProvider;
    private readonly ParticipantRecorder _recorder;

    public SecondJournaledParticipant(
        IGrainContext grainContext,
        IServiceProvider serviceProvider,
        ParticipantRecorder recorder)
    {
        _grainType = grainContext.GrainId.Type.ToString();
        _serviceProvider = serviceProvider;
        _recorder = recorder;
        recorder.RecordSecondConstruction(_grainType);
    }

    public void Initialize()
    {
        _recorder.RecordSecondInitialization(_grainType);
        _ = _serviceProvider.GetRequiredKeyedService<IDurableValue<string>>("participant-two");
    }
}

internal sealed class ParticipantFailure : IJournaledGrainParticipant
{
    public const string ConstructorMessage = "Expected participant constructor failure.";
    public const string InitializeMessage = "Expected participant initialization failure.";
    public const string ActivationMessage = "Expected grain activation failure.";

    public ParticipantFailure(IGrainContext grainContext, ParticipantRecorder recorder)
    {
        var grainType = grainContext.GrainId.Type.ToString();
        recorder.RecordFailureConstruction(grainType);
        if (grainType == ConstructorFailureParticipantGrain.GrainTypeName)
        {
            throw new InvalidOperationException(ConstructorMessage);
        }
    }

    public void Initialize()
    {
    }
}

public sealed class ParticipantRecorder
{
    private readonly ConcurrentDictionary<string, ParticipantCounts> _counts = new(StringComparer.Ordinal);

    public void RecordFirstConstruction(string grainType) =>
        _counts.AddOrUpdate(
            grainType,
            static _ => new() { FirstConstructions = 1 },
            static (_, value) => value with { FirstConstructions = value.FirstConstructions + 1 });

    public void RecordSecondConstruction(string grainType) =>
        _counts.AddOrUpdate(
            grainType,
            static _ => new() { SecondConstructions = 1 },
            static (_, value) => value with { SecondConstructions = value.SecondConstructions + 1 });

    public void RecordFailureConstruction(string grainType) =>
        _counts.AddOrUpdate(
            grainType,
            static _ => new() { FailureConstructions = 1 },
            static (_, value) => value with { FailureConstructions = value.FailureConstructions + 1 });

    public void RecordFirstInitialization(string grainType) =>
        _counts.AddOrUpdate(
            grainType,
            static _ => new() { FirstInitializations = 1 },
            static (_, value) => value with { FirstInitializations = value.FirstInitializations + 1 });

    public void RecordSecondInitialization(string grainType) =>
        _counts.AddOrUpdate(
            grainType,
            static _ => new() { SecondInitializations = 1 },
            static (_, value) => value with { SecondInitializations = value.SecondInitializations + 1 });

    public ParticipantCounts GetCounts(string grainType) => _counts.GetValueOrDefault(grainType);
}

public readonly record struct ParticipantCounts(
    int FirstConstructions = 0,
    int SecondConstructions = 0,
    int FailureConstructions = 0,
    int FirstInitializations = 0,
    int SecondInitializations = 0)
{
    public static ParticipantCounts operator +(ParticipantCounts left, ParticipantCounts right) =>
        new(
            left.FirstConstructions + right.FirstConstructions,
            left.SecondConstructions + right.SecondConstructions,
            left.FailureConstructions + right.FailureConstructions,
            left.FirstInitializations + right.FirstInitializations,
            left.SecondInitializations + right.SecondInitializations);

    public static ParticipantCounts operator -(ParticipantCounts left, ParticipantCounts right) =>
        new(
            left.FirstConstructions - right.FirstConstructions,
            left.SecondConstructions - right.SecondConstructions,
            left.FailureConstructions - right.FailureConstructions,
            left.FirstInitializations - right.FirstInitializations,
            left.SecondInitializations - right.SecondInitializations);
}

[GrainType(GrainTypeName)]
public sealed class ComposedParticipantGrain : DurableGrain, IComposedParticipantGrain
{
    public const string GrainTypeName = "journaling-composed-participant";
    private readonly Guid _activationId = Guid.NewGuid();

    public async Task SetValues(string first, string second)
    {
        GetValue("participant-one").Value = first;
        GetValue("participant-two").Value = second;
        await WriteStateAsync();
    }

    public Task<string[]> GetValues() =>
        Task.FromResult(new[] { GetValue("participant-one").Value!, GetValue("participant-two").Value! });

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

    public override Task OnActivateAsync(CancellationToken cancellationToken) =>
        Task.FromException(new InvalidOperationException(ParticipantFailure.ActivationMessage));

    public Task Ping() => Task.CompletedTask;
}

public interface IComposedParticipantGrain : IGrainWithGuidKey
{
    Task SetValues(string first, string second);
    Task<string[]> GetValues();
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
