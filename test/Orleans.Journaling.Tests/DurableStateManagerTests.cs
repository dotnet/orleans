using System.Buffers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Serialization;
using Xunit;

namespace Orleans.Journaling.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
public sealed class DurableStateManagerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Registry_KeyedAndProgrammaticAccess_Converge(bool keyedFirst)
    {
        var builder = CreateBuilder();
        builder.AddVolatileJournalStorage();
        await using var services = builder.Services.BuildServiceProvider(validateScopes: true);
        await using var scope = CreateActivationScope(services, GrainId.Create("registry-test", "activation"));
        var owner = scope.ServiceProvider.GetRequiredService<IJournaledStateManager>();
        var manager = scope.ServiceProvider.GetRequiredService<IDurableStateManager>();
        Assert.Same(owner, manager);
        Assert.Same(scope.ServiceProvider, Assert.IsType<DurableStateManager>(owner).ServiceProvider);

        var first = keyedFirst
            ? scope.ServiceProvider.GetRequiredKeyedService<IDurableValue<int>>("value")
            : manager.GetOrAddValue<int>("value");
        Assert.Same(first, manager.GetOrAddValue<int>("value"));
        Assert.Same(first, manager.GetOrAddState<IDurableValue<int>>("value"));
        Assert.Same(first, manager.GetOrAddState<object>("value"));
        Assert.Same(first, scope.ServiceProvider.GetRequiredKeyedService<IDurableValue<int>>("value"));
        AssertExisting(manager, "value", first);

        var lower = manager.GetOrAddValue<int>("state");
        var upper = manager.GetOrAddValue<int>("State");
        Assert.Same(lower, manager.GetOrAddValue<int>("state"));
        Assert.NotSame(lower, upper);
        AssertExisting(manager, "state", lower);
        AssertExisting(manager, "State", upper);
        AssertMissing<IDurableValue<int>>(manager, "absent");

        var explicitState = new InertState();
        owner.RegisterStateMachine("explicit", explicitState);
        Assert.Same(explicitState, manager.GetOrAddState<InertState>("explicit"));
        AssertExisting(manager, "explicit", explicitState);
        var duplicate = Assert.Throws<InvalidOperationException>(
            () => owner.RegisterStateMachine("explicit", new InertState()));
        Assert.Contains("explicit", duplicate.Message);
        AssertExisting(manager, "explicit", explicitState);
    }

    [Theory]
    [InlineData("closed-generic")]
    [InlineData("contract")]
    [InlineData("unsupported")]
    public async Task Registry_InvalidRequests_PreserveOriginalState(string requestKind)
    {
        var builder = CreateBuilder();
        builder.AddVolatileJournalStorage();
        await using var services = builder.Services.BuildServiceProvider(validateScopes: true);
        await using var scope = CreateActivationScope(services, GrainId.Create("registry-test", "invalid"));
        var manager = scope.ServiceProvider.GetRequiredService<IDurableStateManager>();
        var original = manager.GetOrAddValue<int>("state");
        var owningServices = scope.ServiceProvider;

        switch (requestKind)
        {
            case "closed-generic":
                AssertIncompatible<IDurableValue<string>, DurableValue<string>>(manager, owningServices, original);
                break;
            case "contract":
                AssertIncompatible<IDurableList<int>, DurableList<int>>(manager, owningServices, original);
                break;
            case "unsupported":
                AssertMissing<IUnsupportedState>(manager, "missing");
                var error = Assert.Throws<InvalidOperationException>(
                    () => manager.GetOrAddState<IUnsupportedState>("missing"));
                Assert.Contains("No durable state implementation is registered", error.Message);
                Assert.Contains("missing", error.Message);
                Assert.Contains(typeof(IUnsupportedState).ToString(), error.Message);
                AssertMissing<IUnsupportedState>(manager, "missing");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(requestKind));
        }

        AssertExisting(manager, "state", original);
        Assert.Same(original, owningServices.GetRequiredKeyedService<IDurableValue<int>>("state"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Activation_CustomRegistration_ConstructsOnceAndScopeOwnsLifetime(bool useFactory)
    {
        var builder = CreateBuilder();
        var constructions = new ProbeCounter();
        builder.Services.AddSingleton(constructions);
        builder.Services.AddScoped<ScopedProbe>();
        var calls = new List<(IServiceProvider Services, string Name)>();
        ProbeState CreateProbe(IServiceProvider owningServices, string name)
        {
            calls.Add((owningServices, name));
            return new ProbeState(owningServices.GetRequiredService<ScopedProbe>());
        }

        builder.AddJournaling();
        if (useFactory)
        {
            builder.Services.AddStateMachine<IProbeState, ProbeState>(CreateProbe);
        }
        else
        {
            builder.Services.AddStateMachine<IProbeState, ProbeState>();
        }

        builder.Services.AddSingleton<IJournalStorageProvider, VolatileJournalStorageProvider>();
        await using var services = builder.Services.BuildServiceProvider(validateScopes: true);
        await using var callerScope = services.CreateAsyncScope();
        var callerMarker = callerScope.ServiceProvider.GetRequiredService<ScopedProbe>();
        var idA = GrainId.Create("scope-test", "A");
        var idB = GrainId.Create("scope-test", "B");
        await using var activationA = CreateActivationScope(services, idA);
        await using var activationB = CreateActivationScope(services, idB);
        var scopeA = activationA.ServiceProvider;
        var scopeB = activationB.ServiceProvider;
        var ownerA = scopeA.GetRequiredService<IJournaledStateManager>();
        var ownerB = scopeB.GetRequiredService<IJournaledStateManager>();
        var managerA = scopeA.GetRequiredService<IDurableStateManager>();
        var managerB = scopeB.GetRequiredService<IDurableStateManager>();
        AssertManagerAliases(ownerA, scopeA);
        AssertManagerAliases(ownerB, scopeB);
        Assert.Same(scopeA, Assert.IsType<DurableStateManager>(ownerA).ServiceProvider);
        Assert.Same(scopeB, Assert.IsType<DurableStateManager>(ownerB).ServiceProvider);

        var probeA = Assert.IsType<ProbeState>(managerA.GetOrAddState<IProbeState>("probe"));
        var probeB = Assert.IsType<ProbeState>(scopeB.GetRequiredKeyedService<IProbeState>("probe"));
        Assert.Same(probeA, managerA.GetOrAddState<IProbeState>("probe"));
        Assert.Same(probeA, scopeA.GetRequiredKeyedService<IProbeState>("probe"));
        Assert.Same(probeB, managerB.GetOrAddState<IProbeState>("probe"));
        Assert.Same(probeB, scopeB.GetRequiredKeyedService<IProbeState>("probe"));
        Assert.NotSame(probeA, probeB);
        Assert.Same(probeA.Marker, scopeA.GetRequiredService<ScopedProbe>());
        Assert.Same(probeB.Marker, scopeB.GetRequiredService<ScopedProbe>());
        Assert.NotSame(probeA.Marker, probeB.Marker);
        Assert.NotSame(callerMarker, probeA.Marker);
        Assert.NotSame(callerMarker, probeB.Marker);
        Assert.Equal(1, probeA.Marker.ConstructionCount);
        Assert.Equal(1, probeB.Marker.ConstructionCount);
        Assert.Equal(2, constructions.Count);
        if (useFactory)
        {
            Assert.Collection(calls,
                call => { Assert.Same(scopeA, call.Services); Assert.Equal("probe", call.Name); },
                call => { Assert.Same(scopeB, call.Services); Assert.Equal("probe", call.Name); });
        }
        else
        {
            Assert.Empty(calls);
        }

        var valueA = managerA.GetOrAddValue<int>("value");
        var valueB = managerB.GetOrAddValue<int>("value");
        Assert.NotSame(valueA, valueB);
        await WaitFor(ownerA.InitializeAsync(TestContext.Current.CancellationToken).AsTask(), "initialize scope A");
        await WaitFor(ownerB.InitializeAsync(TestContext.Current.CancellationToken).AsTask(), "initialize scope B");
        valueA.Value = 11;
        valueB.Value = 22;
        await WaitFor(managerA.WriteStateAsync(TestContext.Current.CancellationToken).AsTask(), "persist scope A");
        await WaitFor(managerB.WriteStateAsync(TestContext.Current.CancellationToken).AsTask(), "persist scope B");
        Assert.Equal(11, valueA.Value);
        Assert.Equal(22, valueB.Value);

        await ownerA.DisposeAsync();
        await ownerA.DisposeAsync();
        Assert.Equal(0, probeA.DisposalCount);
        Assert.Equal(0, probeA.Marker.DisposalCount);
        Assert.Equal(0, probeB.DisposalCount);
        Assert.Equal(0, probeB.Marker.DisposalCount);
        Assert.Equal(0, callerMarker.DisposalCount);

        await activationA.DisposeAsync();
        Assert.Equal(1, probeA.DisposalCount);
        Assert.Equal(0, probeB.DisposalCount);
        Assert.Equal(1, probeA.Marker.DisposalCount);
        Assert.Equal(0, probeB.Marker.DisposalCount);
        Assert.Equal(0, callerMarker.DisposalCount);
        valueB.Value = 23;
        await WaitFor(managerB.WriteStateAsync(TestContext.Current.CancellationToken).AsTask(), "persist surviving scope B");
        Assert.Equal(23, valueB.Value);
        await using (var freshCallerScope = services.CreateAsyncScope())
        {
            Assert.NotSame(callerMarker, freshCallerScope.ServiceProvider.GetRequiredService<ScopedProbe>());
        }

        await ownerB.DisposeAsync();
        Assert.Equal(0, probeB.DisposalCount);
        Assert.Equal(0, probeB.Marker.DisposalCount);
        await activationB.DisposeAsync();
        Assert.Equal(1, probeB.DisposalCount);
        Assert.Equal(1, probeA.DisposalCount);
        Assert.Equal(1, probeB.Marker.DisposalCount);
        Assert.Equal(1, probeA.Marker.DisposalCount);
        Assert.Equal(0, callerMarker.DisposalCount);
        Assert.Equal(2, constructions.Count);

        // Distinct live values alone would not catch two managers writing to the same journal.
        await using var recoveredScopeA = CreateActivationScope(services, idA);
        await using var recoveredScopeB = CreateActivationScope(services, idB);
        var recoveredA = recoveredScopeA.ServiceProvider.GetRequiredService<IDurableStateManager>();
        var recoveredB = recoveredScopeB.ServiceProvider.GetRequiredService<IDurableStateManager>();
        var persistedA = recoveredA.GetOrAddValue<int>("value");
        var persistedB = recoveredB.GetOrAddValue<int>("value");
        await WaitFor(recoveredScopeA.ServiceProvider.GetRequiredService<IJournaledStateManager>()
            .InitializeAsync(TestContext.Current.CancellationToken).AsTask(), "recover scope A");
        await WaitFor(recoveredScopeB.ServiceProvider.GetRequiredService<IJournaledStateManager>()
            .InitializeAsync(TestContext.Current.CancellationToken).AsTask(), "recover scope B");
        Assert.Equal(11, persistedA.Value);
        Assert.Equal(23, persistedB.Value);
    }

    [Fact]
    public async Task Factory_ManualState_ReplaysUsingSharedServicesAndCallerOwnedComponents()
    {
        var builder = CreateBuilder(withGrainContext: false);
        builder.AddVolatileJournalStorage();
        var constructions = new ProbeCounter();
        builder.Services.AddSingleton(constructions);
        builder.Services.AddScoped<ScopedProbe>();
        builder.Services.AddStateMachine<IProbeState, ProbeState>();
        await using var services = builder.Services.BuildServiceProvider(validateScopes: true);
        Assert.Null(services.GetService<IGrainContext>());
        var sharedServices = services.GetRequiredService<IServiceProvider>();
        var factory = services.GetRequiredService<IJournaledStateManagerFactory>();
        var codec = services.GetRequiredKeyedService<IDurableValueCommandCodec<int>>(OrleansBinaryJournalFormat.JournalFormatKey);
        var id = new JournalId("factory/manual");
        var supplied = new CallerOwnedState();
        DurableValue<int> originalValue;

        await using (var owner = factory.CreateStandalone(id))
        {
            Assert.False(owner is IDurableStateManager);
            var concrete = Assert.IsType<JournaledStateManager>(owner);
            Assert.Same(sharedServices, concrete.ServiceProvider);
            Assert.Same(sharedServices, new JournalReplayContext(concrete).ServiceProvider);
            originalValue = new DurableValue<int>("value", owner, codec);
            owner.RegisterStateMachine("supplied", supplied);
            Assert.True(owner.TryGetStateMachine("value", out var valueStateMachine));
            Assert.Same(originalValue, valueStateMachine);
            Assert.True(owner.TryGetStateMachine("supplied", out var suppliedStateMachine));
            Assert.Same(supplied, suppliedStateMachine);
            await WaitFor(owner.InitializeAsync(TestContext.Current.CancellationToken).AsTask(), $"initialize {id}");
            originalValue.Value = 42;
            await WaitFor(owner.WriteStateAsync(TestContext.Current.CancellationToken).AsTask(), $"persist {id}");
            Assert.Equal(0, constructions.ScopedConstructionCount);
            Assert.Equal(0, constructions.ScopedDisposalCount);
        }

        Assert.Equal(0, supplied.DisposalCount);
        var recoveredSupplied = new CallerOwnedState();
        await using (var owner = factory.CreateStandalone(id))
        {
            Assert.False(owner is IDurableStateManager);
            Assert.Same(sharedServices, Assert.IsType<JournaledStateManager>(owner).ServiceProvider);
            var value = new DurableValue<int>("value", owner, codec);
            owner.RegisterStateMachine("supplied", recoveredSupplied);
            Assert.NotSame(originalValue, value);
            Assert.NotSame(supplied, recoveredSupplied);
            await WaitFor(owner.InitializeAsync(TestContext.Current.CancellationToken).AsTask(), $"recover {id}");
            Assert.Equal(42, value.Value);
            Assert.True(owner.TryGetStateMachine("value", out var stateMachine));
            Assert.Same(value, stateMachine);
            Assert.True(owner.TryGetStateMachine("supplied", out stateMachine));
            Assert.Same(recoveredSupplied, stateMachine);
        }

        Assert.Equal(0, supplied.DisposalCount);
        Assert.Equal(0, recoveredSupplied.DisposalCount);
        Assert.Equal(0, constructions.Count);
        Assert.Equal(0, constructions.ScopedConstructionCount);
        Assert.Equal(0, constructions.ScopedDisposalCount);
        await supplied.DisposeAsync();
        await recoveredSupplied.DisposeAsync();
        Assert.Equal(1, supplied.DisposalCount);
        Assert.Equal(1, recoveredSupplied.DisposalCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Registry_InitializationBoundary_RejectsOnlyNewStates(bool duringRecovery)
    {
        var grainId = GrainId.Create("registry-test", "initialization");
        var id = JournalId.FromGrainId(grainId);
        var storage = new ControlledStorage();
        var readBarrier = new StorageBarrier();
        storage.NextReadBarrier = readBarrier;
        var constructions = new ProbeCounter();
        var factoryCalls = 0;
        var builder = CreateBuilder();
        builder.Services.AddSingleton(constructions);
        builder.Services.AddScoped<ScopedProbe>();
        builder.AddJournaling();
        builder.Services.AddStateMachine<IProbeState, ProbeState>((owningServices, _) =>
        {
            factoryCalls++;
            return new ProbeState(owningServices.GetRequiredService<ScopedProbe>());
        });
        builder.Services.AddSingleton<IJournalStorageProvider>(new SingleJournalProvider(id, storage));
        await using var services = builder.Services.BuildServiceProvider(validateScopes: true);
        await using var scope = CreateActivationScope(services, grainId);
        var owningServices = scope.ServiceProvider;
        var owner = owningServices.GetRequiredService<IJournaledStateManager>();
        var manager = owningServices.GetRequiredService<IDurableStateManager>();
        var value = manager.GetOrAddValue<int>("value");
        var probe = manager.GetOrAddState<IProbeState>("probe");
        var explicitState = new InertState();
        var initialization = owner.InitializeAsync(TestContext.Current.CancellationToken).AsTask();
        try
        {
            await WaitFor(readBarrier.Entered.Task, $"read started for {id}; duringRecovery={duringRecovery}");
            Assert.Equal(1, storage.ReadCount);
            Assert.False(initialization.IsCompleted);
            if (!duringRecovery)
            {
                readBarrier.Release();
                await WaitFor(initialization, $"read completed for {id}");
            }

            Assert.Same(value, manager.GetOrAddValue<int>("value"));
            Assert.Same(value, manager.GetOrAddState<IDurableValue<int>>("value"));
            Assert.Same(value, owningServices.GetRequiredKeyedService<IDurableValue<int>>("value"));
            AssertExisting(manager, "value", value);
            Assert.Same(probe, manager.GetOrAddState<IProbeState>("probe"));
            Assert.Same(probe, owningServices.GetRequiredKeyedService<IProbeState>("probe"));
            AssertExisting(manager, "probe", probe);

            AssertAdmissionRejected(() => manager.GetOrAddState<IProbeState>("late-programmatic"));
            AssertMissing<IProbeState>(manager, "late-programmatic");
            Assert.Equal(1, constructions.Count);
            Assert.Equal(1, factoryCalls);
            AssertAdmissionRejected(() => owningServices.GetRequiredKeyedService<IProbeState>("late-keyed"));
            AssertMissing<IProbeState>(manager, "late-keyed");
            Assert.Equal(1, constructions.Count);
            Assert.Equal(1, factoryCalls);
            AssertAdmissionRejected(() => owner.RegisterStateMachine("late-explicit", explicitState));
            AssertMissing<InertState>(manager, "late-explicit");
            AssertAdmissionRejected(() => manager.GetOrAddValue<int>("late-value"));
            AssertMissing<IDurableValue<int>>(manager, "late-value");
            AssertAdmissionRejected(() => owningServices.GetRequiredKeyedService<IDurableList<string>>("late-list"));
            AssertMissing<IDurableList<string>>(manager, "late-list");
            Assert.Equal(1, constructions.Count);
        }
        finally
        {
            readBarrier.Release();
        }

        await WaitFor(initialization, $"finish recovery for {id}");
        value.Value = 31;
        await WaitFor(manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask(), $"write after rejected registration for {id}");
        Assert.Equal(1, storage.SuccessfulAppendCount);
        await scope.DisposeAsync();

        await using var recoveredScope = CreateActivationScope(services, grainId);
        var recoveredOwner = recoveredScope.ServiceProvider.GetRequiredService<IJournaledStateManager>();
        var recovered = recoveredScope.ServiceProvider.GetRequiredService<IDurableStateManager>();
        var recoveredValue = recovered.GetOrAddValue<int>("value");
        recovered.GetOrAddState<IProbeState>("probe");
        await WaitFor(recoveredOwner.InitializeAsync(TestContext.Current.CancellationToken).AsTask(), $"fresh recovery for {id}");
        Assert.NotSame(value, recoveredValue);
        Assert.Equal(31, recoveredValue.Value);
        Assert.Equal(2, storage.ReadCount);
        Assert.Equal(2, constructions.Count);
        Assert.Equal(2, factoryCalls);
        AssertMissing<IProbeState>(recovered, "late-programmatic");
        AssertMissing<IProbeState>(recovered, "late-keyed");
        AssertMissing<InertState>(recovered, "late-explicit");
        AssertMissing<IDurableValue<int>>(recovered, "late-value");
        AssertMissing<IDurableList<string>>(recovered, "late-list");
    }

    [Fact]
    public async Task Activation_AllHelpers_ShareAcknowledgementAndRecover()
    {
        AssertNullManagerGuards();
        var grainId = GrainId.Create("activation-test", "all-helpers");
        var id = JournalId.FromGrainId(grainId);
        var storage = new ControlledStorage();
        var provider = new SingleJournalProvider(id, storage);
        var builder = CreateBuilder();
        builder.AddJournaling();
        builder.Services.AddSingleton<IJournalStorageProvider>(provider);
        await using var services = builder.Services.BuildServiceProvider(validateScopes: true);
        await using var scope = CreateActivationScope(services, grainId);
        var owner = scope.ServiceProvider.GetRequiredService<IJournaledStateManager>();
        var manager = scope.ServiceProvider.GetRequiredService<IDurableStateManager>();
        Assert.Equal(OrleansBinaryJournalFormat.JournalFormatKey,
            Assert.IsType<DurableStateManager>(owner).WriteJournalFormatKey);
        var states = new SevenStates(manager);
        await WaitFor(owner.InitializeAsync(TestContext.Current.CancellationToken).AsTask(), $"initialize {id}");
        Assert.Equal(0, storage.AppendCount);
        Assert.False(states.Completion.Task.IsCompleted);
        states.Dictionary["a"] = 11;
        states.List.Add("first");
        states.List.Add("second");
        states.Queue.Enqueue(3);
        states.Queue.Enqueue(5);
        Assert.True(states.Set.Add("a"));
        Assert.True(states.Set.Add("b"));
        states.Value.Value = 17;
        Assert.True(states.Completion.TrySetResult(19));
        states.Persistent.State = "saved";
        var appendBarrier = new StorageBarrier();
        storage.NextAppendBarrier = appendBarrier;
        var write = manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        try
        {
            await WaitFor(appendBarrier.Entered.Task, $"append entered for {id}");
            Assert.Equal(1, storage.AppendCount);
            Assert.Equal(0, storage.SuccessfulAppendCount);
            Assert.False(write.IsCompleted);
            Assert.False(states.Completion.Task.IsCompleted);
            Assert.False(states.Persistent.RecordExists);
            Assert.Equal("0", states.Persistent.Etag);
        }
        finally
        {
            appendBarrier.Release();
        }

        await WaitFor(write, $"shared acknowledgement for {id}");
        Assert.Equal(1, storage.SuccessfulAppendCount);
        await states.AssertPersisted();
        await scope.DisposeAsync();

        await using var recoveredScope = CreateActivationScope(services, grainId);
        var recoveredOwner = recoveredScope.ServiceProvider.GetRequiredService<IJournaledStateManager>();
        var recovered = recoveredScope.ServiceProvider.GetRequiredService<IDurableStateManager>();
        var recoveredStates = new SevenStates(recovered);
        Assert.NotSame(states.Dictionary, recoveredStates.Dictionary);
        Assert.NotSame(states.List, recoveredStates.List);
        Assert.NotSame(states.Queue, recoveredStates.Queue);
        Assert.NotSame(states.Set, recoveredStates.Set);
        Assert.NotSame(states.Value, recoveredStates.Value);
        Assert.NotSame(states.Completion, recoveredStates.Completion);
        Assert.NotSame(states.Persistent, recoveredStates.Persistent);
        await WaitFor(recoveredOwner.InitializeAsync(TestContext.Current.CancellationToken).AsTask(), $"recover seven states for {id}");
        await recoveredStates.AssertPersisted();
        Assert.Equal(2, provider.CreateCount);
        Assert.Equal(2, storage.ReadCount);
        Assert.Equal(1, storage.AppendCount);
        Assert.Equal(1, storage.SuccessfulAppendCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Activation_WriteFailure_FencesManagerAndRecoversCommit(bool committed)
    {
        var grainId = GrainId.Create("activation-test", "failure");
        var id = JournalId.FromGrainId(grainId);
        var storage = new ControlledStorage();
        var constructions = new ProbeCounter();
        var builder = CreateBuilder();
        builder.Services.AddSingleton(constructions);
        builder.Services.AddScoped<ScopedProbe>();
        builder.AddJournaling();
        builder.Services.AddStateMachine<IProbeState, ProbeState>();
        builder.Services.AddSingleton<IJournalStorageProvider>(new SingleJournalProvider(id, storage));
        await using var services = builder.Services.BuildServiceProvider(validateScopes: true);
        await using var scope = CreateActivationScope(services, grainId);
        var owner = scope.ServiceProvider.GetRequiredService<IJournaledStateManager>();
        var manager = scope.ServiceProvider.GetRequiredService<IDurableStateManager>();
        var value = manager.GetOrAddValue<int>("value");
        await WaitFor(owner.InitializeAsync(TestContext.Current.CancellationToken).AsTask(), $"initialize {id}");
        value.Value = 1;
        await WaitFor(manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask(), $"persist initial value for {id}");
        value.Value = 2;
        var injected = new IOException("Injected append acknowledgement failure.");
        storage.NextAppendFailure = injected;
        storage.FailAfterCommit = committed;
        var failure = await Assert.ThrowsAsync<IOException>(() =>
            WaitFor(manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask(), $"failed append for {id}, committed={committed}"));
        Assert.Same(injected, failure);
        Assert.Equal(2, value.Value);
        Assert.Equal(1, storage.ReadCount);
        Assert.Equal(2, storage.AppendCount);
        Assert.Equal(committed ? 2 : 1, storage.SuccessfulAppendCount);

        var writeRejection = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WaitFor(manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask(), $"fenced write for {id}"));
        AssertFenced(writeRejection, injected);
        var initializationRejection = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WaitFor(owner.InitializeAsync(TestContext.Current.CancellationToken).AsTask(), $"fenced recovery for {id}"));
        AssertFenced(initializationRejection, injected);
        var registrationRejection = Assert.Throws<InvalidOperationException>(
            () => manager.GetOrAddState<IProbeState>("late"));
        AssertFenced(registrationRejection, injected);
        Assert.Equal(0, constructions.Count);
        Assert.Equal(1, storage.ReadCount);
        Assert.Equal(2, storage.AppendCount);
        Assert.Equal(committed ? 2 : 1, storage.SuccessfulAppendCount);
        await scope.DisposeAsync();

        await using var recoveredScope = CreateActivationScope(services, grainId);
        var recoveredOwner = recoveredScope.ServiceProvider.GetRequiredService<IJournaledStateManager>();
        var recovered = recoveredScope.ServiceProvider.GetRequiredService<IDurableStateManager>();
        var recoveredValue = recovered.GetOrAddValue<int>("value");
        await WaitFor(recoveredOwner.InitializeAsync(TestContext.Current.CancellationToken).AsTask(), $"fresh recovery for {id}, committed={committed}");
        Assert.NotSame(value, recoveredValue);
        Assert.Equal(committed ? 2 : 1, recoveredValue.Value);
        AssertMissing<IProbeState>(recovered, "late");
        Assert.Equal(0, constructions.Count);
        Assert.Equal(2, storage.ReadCount);
        Assert.Equal(2, storage.AppendCount);
    }

    private static TestSiloBuilder CreateBuilder(bool withGrainContext = true)
    {
        var builder = new TestSiloBuilder();
        builder.Services.AddSerializer();
        builder.Services.AddLogging();
        builder.Services.AddKeyedSingleton<TimeProvider>(KeyedService.AnyKey, TimeProvider.System);
        builder.Services.Configure<JournaledStateManagerOptions>(
            options => options.JournalFormatKey = OrleansBinaryJournalFormat.JournalFormatKey);
        if (withGrainContext)
        {
            builder.Services.AddScoped<IGrainContext>(activationServices =>
            {
                var context = Substitute.For<IGrainContext>();
                context.ActivationServices.Returns(activationServices);
                context.ObservableLifecycle.Returns(Substitute.For<IGrainLifecycle>());
                return context;
            });
        }

        return builder;
    }

    private static AsyncServiceScope CreateActivationScope(ServiceProvider services, GrainId grainId)
    {
        var scope = services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IGrainContext>().GrainId.Returns(grainId);
        return scope;
    }

    private static void AssertManagerAliases(IJournaledStateManager manager, IServiceProvider services)
    {
        Assert.Same(manager, services.GetRequiredService<IJournaledStateManager>());
        Assert.Same(manager, services.GetRequiredService<IDurableStateManager>());
    }

    private static void AssertExisting<TState>(IDurableStateManager manager, string name, TState expected) where TState : class
    {
        Assert.True(manager.TryGetState<TState>(name, out var actual));
        Assert.Same(expected, actual);
    }

    private static void AssertMissing<TState>(IDurableStateManager manager, string name) where TState : class
    {
        Assert.False(manager.TryGetState<TState>(name, out var state));
        Assert.Null(state);
    }

    private static void AssertIncompatible<TState, TImplementation>(
        IDurableStateManager manager, IServiceProvider services, IDurableValue<int> original)
        where TState : class
    {
        var getError = Assert.Throws<InvalidOperationException>(() => manager.GetOrAddState<TState>("state"));
        Assert.Contains("state", getError.Message);
        Assert.Contains(typeof(TState).ToString(), getError.Message);
        AssertExisting(manager, "state", original);
        var lookupError = Assert.Throws<InvalidOperationException>(() => manager.TryGetState<TState>("state", out _));
        Assert.Contains("state", lookupError.Message);
        Assert.Contains(typeof(TState).ToString(), lookupError.Message);
        AssertExisting(manager, "state", original);
        var keyedError = Assert.Throws<InvalidOperationException>(() => services.GetRequiredKeyedService<TState>("state"));
        Assert.Contains("state", keyedError.Message);
        // Built-ins self-register: this path diagnoses the conflicting implementation, not its interface.
        Assert.Contains(typeof(TImplementation).ToString(), keyedError.Message);
        AssertExisting(manager, "state", original);
    }

    private static void AssertAdmissionRejected(Action action)
    {
        var error = Assert.Throws<InvalidOperationException>(action);
        Assert.Equal("New states cannot be registered after journaled state manager initialization has begun.", error.Message);
    }

    private static void AssertFenced(InvalidOperationException error, IOException cause)
    {
        Assert.Contains("Journaled state operations are fenced", error.Message);
        Assert.Same(cause, error.InnerException);
    }

    private static void AssertNullManagerGuards()
    {
        IDurableStateManager manager = null!;
        Assert.Equal("manager", Assert.Throws<ArgumentNullException>(() => manager.GetOrAddDictionary<string, int>("dictionary")).ParamName);
        Assert.Equal("manager", Assert.Throws<ArgumentNullException>(() => manager.GetOrAddList<string>("list")).ParamName);
        Assert.Equal("manager", Assert.Throws<ArgumentNullException>(() => manager.GetOrAddQueue<int>("queue")).ParamName);
        Assert.Equal("manager", Assert.Throws<ArgumentNullException>(() => manager.GetOrAddSet<string>("set")).ParamName);
        Assert.Equal("manager", Assert.Throws<ArgumentNullException>(() => manager.GetOrAddValue<int>("value")).ParamName);
        Assert.Equal("manager", Assert.Throws<ArgumentNullException>(() => manager.GetOrAddTaskCompletionSource<int>("tcs")).ParamName);
        Assert.Equal("manager", Assert.Throws<ArgumentNullException>(() => manager.GetOrAddPersistentState<string>("persistent")).ParamName);
    }

    private static async Task WaitFor(Task task, string phase)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }
        catch (TimeoutException error)
        {
            throw new TimeoutException($"Timed out waiting for {phase}; task status: {task.Status}.", error);
        }
    }

    private sealed class SevenStates(IDurableStateManager manager)
    {
        public IDurableDictionary<string, int> Dictionary { get; } = manager.GetOrAddDictionary<string, int>("dictionary");
        public IDurableList<string> List { get; } = manager.GetOrAddList<string>("list");
        public IDurableQueue<int> Queue { get; } = manager.GetOrAddQueue<int>("queue");
        public IDurableSet<string> Set { get; } = manager.GetOrAddSet<string>("set");
        public IDurableValue<int> Value { get; } = manager.GetOrAddValue<int>("value");
        public IDurableTaskCompletionSource<int> Completion { get; } = manager.GetOrAddTaskCompletionSource<int>("tcs");
        public IPersistentState<string> Persistent { get; } = manager.GetOrAddPersistentState<string>("persistent");

        public async Task AssertPersisted()
        {
            var entry = Assert.Single(Dictionary);
            Assert.Equal("a", entry.Key);
            Assert.Equal(11, entry.Value);
            Assert.Equal(new[] { "first", "second" }, List);
            Assert.Equal(2, Queue.Count);
            Assert.Equal(new[] { 3, 5 }, Queue);
            Assert.Equal(3, Queue.Peek());
            Assert.Equal(2, Set.Count);
            Assert.True(Set.SetEquals(["a", "b"]));
            Assert.Equal(17, Value.Value);
            Assert.Equal(DurableTaskCompletionSourceStatus.Completed, Completion.State.Status);
            Assert.Equal(19, Completion.State.Value);
            await WaitFor(Completion.Task, "durable TCS acknowledgement");
            Assert.Equal(19, await Completion.Task);
            Assert.Equal("saved", Persistent.State);
            Assert.True(Persistent.RecordExists);
            Assert.Equal("1", Persistent.Etag);
        }
    }

    public interface IUnsupportedState { }
    public interface IProbeState { }

    public class InertState : IStateMachine
    {
        public void ReplayEntry(JournalEntry entry, JournalReplayContext context)
            => throw new InvalidOperationException("The inert probe does not write replayable entries.");
        public void Reset(JournalStreamWriter writer) { }
        public void WritePendingEntries(JournalStreamWriter writer) { }
        public void WriteSnapshot(JournalStreamWriter writer) { }
    }

    public sealed class ProbeState : InertState, IProbeState, IAsyncDisposable
    {
        private int _disposalCount;

        public ProbeState(ScopedProbe marker)
        {
            Marker = marker;
            marker.OnConstructed();
        }

        public ScopedProbe Marker { get; }
        public int DisposalCount => Volatile.Read(ref _disposalCount);

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposalCount);
            return ValueTask.CompletedTask;
        }
    }

    public sealed class ProbeCounter
    {
        private int _count;
        private int _scopedConstructionCount;
        private int _scopedDisposalCount;
        public int Count => Volatile.Read(ref _count);
        public int ScopedConstructionCount => Volatile.Read(ref _scopedConstructionCount);
        public int ScopedDisposalCount => Volatile.Read(ref _scopedDisposalCount);
        public void Increment() => Interlocked.Increment(ref _count);
        public void OnScopedConstructed() => Interlocked.Increment(ref _scopedConstructionCount);
        public void OnScopedDisposed() => Interlocked.Increment(ref _scopedDisposalCount);
    }

    public sealed class ScopedProbe : IAsyncDisposable
    {
        private readonly ProbeCounter _counter;
        private int _constructionCount;
        private int _disposalCount;

        public ScopedProbe(ProbeCounter counter)
        {
            _counter = counter;
            counter.OnScopedConstructed();
        }

        public int ConstructionCount => Volatile.Read(ref _constructionCount);
        public int DisposalCount => Volatile.Read(ref _disposalCount);

        public void OnConstructed()
        {
            Interlocked.Increment(ref _constructionCount);
            _counter.Increment();
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposalCount);
            _counter.OnScopedDisposed();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CallerOwnedState : InertState, IAsyncDisposable
    {
        private int _disposalCount;
        public int DisposalCount => Volatile.Read(ref _disposalCount);

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposalCount);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestSiloBuilder : ISiloBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();
        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
    }

    private sealed class SingleJournalProvider(JournalId id, IJournalStorage storage) : IJournalStorageProvider
    {
        public int CreateCount { get; private set; }

        public IJournalStorage CreateStorage(JournalId journalId)
        {
            Assert.Equal(id, journalId);
            CreateCount++;
            return storage;
        }
    }

    private sealed class StorageBarrier
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => Released.TrySetResult();

        public async Task Enter(CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Released.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class ControlledStorage : IJournalStorage
    {
        private readonly VolatileJournalStorage _inner = new(OrleansBinaryJournalFormat.JournalFormatKey);
        private int _readCount;
        private int _appendCount;
        private int _successfulAppendCount;
        public int ReadCount => Volatile.Read(ref _readCount);
        public int AppendCount => Volatile.Read(ref _appendCount);
        public int SuccessfulAppendCount => Volatile.Read(ref _successfulAppendCount);
        public StorageBarrier? NextReadBarrier { get; set; }
        public StorageBarrier? NextAppendBarrier { get; set; }
        public IOException? NextAppendFailure { get; set; }
        public bool FailAfterCommit { get; set; }
        public bool IsCompactionRequested => _inner.IsCompactionRequested;

        public async ValueTask ReadAsync(IJournalStorageConsumer consumer, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _readCount);
            if (NextReadBarrier is { } barrier)
            {
                NextReadBarrier = null;
                await barrier.Enter(cancellationToken);
            }

            await _inner.ReadAsync(consumer, cancellationToken);
        }

        public async ValueTask AppendAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _appendCount);
            if (NextAppendBarrier is { } barrier)
            {
                NextAppendBarrier = null;
                await barrier.Enter(cancellationToken);
            }

            var failure = NextAppendFailure;
            NextAppendFailure = null;
            if (failure is not null && !FailAfterCommit)
                throw failure;

            await _inner.AppendAsync(value, cancellationToken);
            Interlocked.Increment(ref _successfulAppendCount);
            if (failure is not null)
                throw failure;
        }

        public ValueTask ReplaceAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
            => _inner.ReplaceAsync(value, cancellationToken);

        public ValueTask DeleteAsync(CancellationToken cancellationToken)
            => _inner.DeleteAsync(cancellationToken);
    }
}
