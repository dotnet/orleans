using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.CodeGeneration;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace UnitTests.ConcurrencyTests;

[TestSuite("Functional")]
[TestProvider("None")]
[TestArea("Runtime")]
public class ReadOnlyAttributeTests : OrleansTestingBase, IClassFixture<ReadOnlyAttributeTests.Fixture>
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(30);
    private readonly Fixture _fixture;
    private readonly RequestOptionsRecorder _recorder;

    public ReadOnlyAttributeTests(Fixture fixture)
    {
        _fixture = fixture;
        _recorder = fixture.Recorder;
        _recorder.Clear();
    }

    [Fact, TestCategory("Functional"), TestCategory("ReadOnly")]
    public async Task RequestFlags_AreSetOnlyForEffectiveReadOnlyMethods()
    {
        var grain = GetGrain<IReadOnlySchedulingGrain>();

        Assert.Equal("read:alpha", await Await(grain.ReadOnlyEcho("alpha"), "plain ReadOnly echo"));
        Assert.Equal("write:beta", await Await(grain.WritableEcho("beta"), "writable echo"));
        Assert.Equal("interleave:gamma", await Await(grain.AlwaysInterleaveEcho("gamma"), "AlwaysInterleave echo"));
        Assert.Equal(
            "read-interleave:delta",
            await Await(grain.ReadOnlyAlwaysInterleaveEcho("delta"), "combined ReadOnly and AlwaysInterleave echo"));

        var readOnly = Find(
            typeof(IReadOnlySchedulingGrain),
            nameof(IReadOnlySchedulingGrain.ReadOnlyEcho),
            typeof(Task<string>),
            typeof(string));
        var writable = Find(
            typeof(IReadOnlySchedulingGrain),
            nameof(IReadOnlySchedulingGrain.WritableEcho),
            typeof(Task<string>),
            typeof(string));
        var alwaysInterleave = Find(
            typeof(IReadOnlySchedulingGrain),
            nameof(IReadOnlySchedulingGrain.AlwaysInterleaveEcho),
            typeof(Task<string>),
            typeof(string));
        var combined = Find(
            typeof(IReadOnlySchedulingGrain),
            nameof(IReadOnlySchedulingGrain.ReadOnlyAlwaysInterleaveEcho),
            typeof(Task<string>),
            typeof(string));

        Assert.Equal(InvokeMethodOptions.ReadOnly, readOnly.Options);
        Assert.Equal(InvokeMethodOptions.None, writable.Options);
        Assert.Equal(InvokeMethodOptions.AlwaysInterleave, alwaysInterleave.Options);
        Assert.Equal(InvokeMethodOptions.ReadOnly | InvokeMethodOptions.AlwaysInterleave, combined.Options);
        Assert.False(writable.Options.HasFlag(InvokeMethodOptions.ReadOnly));
        Assert.False(alwaysInterleave.Options.HasFlag(InvokeMethodOptions.ReadOnly));
    }

    [Fact, TestCategory("Functional"), TestCategory("ReadOnly")]
    public async Task InheritedInterfaceMethod_PreservesBaseReadOnlyFlag()
    {
        var grain = GetGrain<IReadOnlyInheritedInterfaceDerivedGrain>();

        Assert.Equal("inherited-interface", await Await(grain.InheritedReadOnly(), "inherited interface call"));

        var request = Find(
            typeof(IReadOnlyInheritedInterfaceBaseGrain),
            nameof(IReadOnlyInheritedInterfaceBaseGrain.InheritedReadOnly),
            typeof(Task<string>));
        Assert.Equal(InvokeMethodOptions.ReadOnly, request.Options);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [TestCategory("Functional"), TestCategory("ReadOnly")]
    public async Task RedeclaredInterfaceMethods_UseInvokedDeclarationMetadata(bool baseDeclarationIsReadOnly)
    {
        if (baseDeclarationIsReadOnly)
        {
            var derived = GetGrain<IReadOnlyRedeclaredDerivedWritableGrain>();
            var baseView = (IReadOnlyRedeclaredBaseReadOnlyGrain)derived;

            Assert.Equal("base-readonly-derived-writable", await Await(baseView.Redeclared(), "base ReadOnly declaration"));
            Assert.Equal("base-readonly-derived-writable", await Await(derived.Redeclared(), "derived writable declaration"));

            Assert.Equal(
                InvokeMethodOptions.ReadOnly,
                Find(
                    typeof(IReadOnlyRedeclaredBaseReadOnlyGrain),
                    nameof(IReadOnlyRedeclaredBaseReadOnlyGrain.Redeclared),
                    typeof(Task<string>)).Options);
            Assert.Equal(
                InvokeMethodOptions.None,
                Find(
                    typeof(IReadOnlyRedeclaredDerivedWritableGrain),
                    nameof(IReadOnlyRedeclaredDerivedWritableGrain.Redeclared),
                    typeof(Task<string>)).Options);
        }
        else
        {
            var derived = GetGrain<IReadOnlyRedeclaredDerivedReadOnlyGrain>();
            var baseView = (IReadOnlyRedeclaredBaseWritableGrain)derived;

            Assert.Equal("base-writable-derived-readonly", await Await(baseView.Redeclared(), "base writable declaration"));
            Assert.Equal("base-writable-derived-readonly", await Await(derived.Redeclared(), "derived ReadOnly declaration"));

            Assert.Equal(
                InvokeMethodOptions.None,
                Find(
                    typeof(IReadOnlyRedeclaredBaseWritableGrain),
                    nameof(IReadOnlyRedeclaredBaseWritableGrain.Redeclared),
                    typeof(Task<string>)).Options);
            Assert.Equal(
                InvokeMethodOptions.ReadOnly,
                Find(
                    typeof(IReadOnlyRedeclaredDerivedReadOnlyGrain),
                    nameof(IReadOnlyRedeclaredDerivedReadOnlyGrain.Redeclared),
                    typeof(Task<string>)).Options);
        }
    }

    [Fact, TestCategory("Functional"), TestCategory("ReadOnly")]
    public async Task DerivedGrainInheritsBaseImplementation_UsesInterfaceReadOnlyMetadata()
    {
        var grain = GetGrain<IReadOnlyInheritedImplementationGrain>();

        Assert.Equal(
            "inherited-implementation",
            await Await(grain.InheritedImplementation(), "inherited grain implementation call"));

        var request = Find(
            typeof(IReadOnlyInheritedImplementationGrain),
            nameof(IReadOnlyInheritedImplementationGrain.InheritedImplementation),
            typeof(Task<string>));
        Assert.Equal(InvokeMethodOptions.ReadOnly, request.Options);
    }

    [Fact, TestCategory("Functional"), TestCategory("ReadOnly")]
    public async Task ImplementationMethodAttribute_DoesNotSetReadOnlyRequestFlag()
    {
        var grain = GetGrain<IImplementationOnlyReadOnlySchedulingGrain>();

        Assert.Equal("implementation-only", await Await(grain.ImplementationOnly(), "implementation-only attribute call"));

        var request = Find(
            typeof(IImplementationOnlyReadOnlySchedulingGrain),
            nameof(IImplementationOnlyReadOnlySchedulingGrain.ImplementationOnly),
            typeof(Task<string>));
        Assert.Equal(InvokeMethodOptions.None, request.Options);
        Assert.False(request.Options.HasFlag(InvokeMethodOptions.ReadOnly));
    }

    [Fact, TestCategory("Functional"), TestCategory("ReadOnly")]
    public async Task Overloads_KeepReadOnlyMetadataBySignature()
    {
        var grain = GetGrain<IReadOnlyOverloadSchedulingGrain>();

        Assert.Equal("readonly-int:42", await Await(grain.Overloaded(42), "ReadOnly integer overload"));
        Assert.Equal("writable-string:forty-two", await Await(grain.Overloaded("forty-two"), "writable string overload"));

        var readOnly = Find(
            typeof(IReadOnlyOverloadSchedulingGrain),
            nameof(IReadOnlyOverloadSchedulingGrain.Overloaded),
            typeof(Task<string>),
            typeof(int));
        var writable = Find(
            typeof(IReadOnlyOverloadSchedulingGrain),
            nameof(IReadOnlyOverloadSchedulingGrain.Overloaded),
            typeof(Task<string>),
            typeof(string));

        Assert.Equal(InvokeMethodOptions.ReadOnly, readOnly.Options);
        Assert.Equal(InvokeMethodOptions.None, writable.Options);
        Assert.NotEqual(readOnly.ParameterTypes, writable.ParameterTypes);
    }

    [Fact, TestCategory("Functional"), TestCategory("ReadOnly")]
    public async Task GenericMethodAndGenericGrainType_PreserveReadOnlyFlag()
    {
        var genericMethodGrain = GetGrain<IReadOnlyGenericMethodSchedulingGrain>();
        var genericTypeGrain = GetGrain<IReadOnlyGenericSchedulingGrain<string>>();

        Assert.Equal(42, await Await(genericMethodGrain.GenericReadOnly(42), "constructed generic method"));
        Assert.Equal("forty-two", await Await(genericTypeGrain.GenericReadOnly("forty-two"), "constructed generic grain type"));

        var genericMethod = Find(
            typeof(IReadOnlyGenericMethodSchedulingGrain),
            nameof(IReadOnlyGenericMethodSchedulingGrain.GenericReadOnly),
            typeof(Task<int>),
            1,
            typeof(int));
        var genericType = Find(
            typeof(IReadOnlyGenericSchedulingGrain<string>),
            nameof(IReadOnlyGenericSchedulingGrain<string>.GenericReadOnly),
            typeof(Task<string>),
            typeof(string));

        Assert.Equal(1, genericMethod.GenericArity);
        Assert.Equal(0, genericType.GenericArity);
        Assert.Equal(InvokeMethodOptions.ReadOnly, genericMethod.Options);
        Assert.Equal(InvokeMethodOptions.ReadOnly, genericType.Options);
        Assert.NotEqual(genericMethod.DeclaringInterface, genericType.DeclaringInterface);
        Assert.NotEqual(genericMethod.ParameterTypes, genericType.ParameterTypes);
    }

    [Fact, TestCategory("Functional"), TestCategory("ReadOnly")]
    public async Task ReadOnlyCalls_OverlapBeforeEitherIsReleased()
    {
        var grain = GetGrain<IReadOnlySchedulingGrain>();

        var first = grain.BlockReadOnly("R1");
        await WaitForEntry(grain, "R1");
        var second = grain.BlockReadOnly("R2");
        await WaitForEntry(grain, "R2");

        Assert.Equal(
            ImmutableArray.Create("enter:R1", "enter:R2"),
            await Await(grain.GetEvents(), "both ReadOnly operations to enter"));

        await Release(grain, "R1");
        await Await(first, "first ReadOnly operation to exit");
        await Release(grain, "R2");
        await Await(second, "second ReadOnly operation to exit");

        Assert.Equal(
            ImmutableArray.Create("enter:R1", "enter:R2", "exit:R1", "exit:R2"),
            await Await(grain.GetEvents(), "both ReadOnly operations to exit"));
    }

    [Fact, TestCategory("Functional"), TestCategory("ReadOnly")]
    public async Task ReadOnlyThenWritable_WritableWaitsForReadOnly()
    {
        var grain = GetGrain<IReadOnlySchedulingGrain>();

        var read = grain.BlockReadOnly("R");
        await WaitForEntry(grain, "R");
        var write = grain.BlockWritable("W");
        await WaitForWaitingRequestCount(grain, 1, "writable operation to be queued");
        await Checkpoint(grain, "write-queued");

        Assert.Equal(
            ImmutableArray.Create("enter:R", "checkpoint:write-queued"),
            await Await(grain.GetEvents(), "checkpoint after writable operation was queued"));

        await Release(grain, "R");
        await Await(read, "ReadOnly operation to exit");
        await WaitForEntry(grain, "W");
        await Release(grain, "W");
        await Await(write, "writable operation to exit");

        Assert.Equal(
            ImmutableArray.Create(
                "enter:R",
                "checkpoint:write-queued",
                "exit:R",
                "enter:W",
                "exit:W"),
            await Await(grain.GetEvents(), "ReadOnly then writable operations to exit"));
    }

    [Fact, TestCategory("Functional"), TestCategory("ReadOnly")]
    public async Task WritableThenReadOnly_ReadOnlyWaitsForWritable()
    {
        var grain = GetGrain<IReadOnlySchedulingGrain>();

        var write = grain.BlockWritable("W");
        await WaitForEntry(grain, "W");
        var read = grain.BlockReadOnly("R");
        await WaitForWaitingRequestCount(grain, 1, "ReadOnly operation to be queued");
        await Checkpoint(grain, "read-queued");

        Assert.Equal(
            ImmutableArray.Create("enter:W", "checkpoint:read-queued"),
            await Await(grain.GetEvents(), "checkpoint after ReadOnly operation was queued"));

        await Release(grain, "W");
        await Await(write, "writable operation to exit");
        await WaitForEntry(grain, "R");
        await Release(grain, "R");
        await Await(read, "ReadOnly operation to exit");

        Assert.Equal(
            ImmutableArray.Create(
                "enter:W",
                "checkpoint:read-queued",
                "exit:W",
                "enter:R",
                "exit:R"),
            await Await(grain.GetEvents(), "writable then ReadOnly operations to exit"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [TestCategory("Functional"), TestCategory("ReadOnly")]
    public async Task ReadOnlyAndAlwaysInterleave_WritableCallOverlapsInBothArrivalOrders(bool combinedCallFirst)
    {
        var grain = GetGrain<IReadOnlySchedulingGrain>();
        var firstId = combinedCallFirst ? "RI" : "W";
        var secondId = combinedCallFirst ? "W" : "RI";

        var first = combinedCallFirst
            ? grain.BlockReadOnlyAlwaysInterleave(firstId)
            : grain.BlockWritable(firstId);
        await WaitForEntry(grain, firstId);
        var second = combinedCallFirst
            ? grain.BlockWritable(secondId)
            : grain.BlockReadOnlyAlwaysInterleave(secondId);
        await WaitForEntry(grain, secondId);

        Assert.Equal(
            ImmutableArray.Create($"enter:{firstId}", $"enter:{secondId}"),
            await Await(grain.GetEvents(), "combined and writable operations to enter"));
        Assert.Equal(
            InvokeMethodOptions.ReadOnly | InvokeMethodOptions.AlwaysInterleave,
            Find(
                typeof(IReadOnlySchedulingGrain),
                nameof(IReadOnlySchedulingGrain.BlockReadOnlyAlwaysInterleave),
                typeof(Task),
                typeof(string)).Options);

        await Release(grain, firstId);
        await Await(first, $"first operation '{firstId}' to exit");
        await Release(grain, secondId);
        await Await(second, $"second operation '{secondId}' to exit");

        Assert.Equal(
            ImmutableArray.Create(
                $"enter:{firstId}",
                $"enter:{secondId}",
                $"exit:{firstId}",
                $"exit:{secondId}"),
            await Await(grain.GetEvents(), "combined and writable operations to exit"));
    }

    [Fact, TestCategory("Functional"), TestCategory("ReadOnly")]
    public async Task AlwaysInterleaveRequest_RemainsNonBlockingAfterInitialBlockerCompletes()
    {
        var grain = GetGrain<IReadOnlySchedulingGrain>();

        var initialWrite = grain.BlockWritable("W1");
        await WaitForEntry(grain, "W1");
        var interleavingRead = grain.BlockReadOnlyAlwaysInterleave("RI");
        await WaitForEntry(grain, "RI");

        await Release(grain, "W1");
        await Await(initialWrite, "initial writable operation to exit");

        var laterWrite = grain.BlockWritable("W2");
        await WaitForEntry(grain, "W2");

        Assert.Equal(
            ImmutableArray.Create("enter:W1", "enter:RI", "exit:W1", "enter:W2"),
            await Await(grain.GetEvents(), "later writable operation to enter"));

        await Release(grain, "W2");
        await Await(laterWrite, "later writable operation to exit");
        await Release(grain, "RI");
        await Await(interleavingRead, "AlwaysInterleave operation to exit");

        Assert.Equal(
            ImmutableArray.Create(
                "enter:W1",
                "enter:RI",
                "exit:W1",
                "enter:W2",
                "exit:W2",
                "exit:RI"),
            await Await(grain.GetEvents(), "all operations to exit without a replacement blocker"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [TestCategory("Functional"), TestCategory("ReadOnly")]
    public async Task ReadOnlyOnReentrantGrain_WritableCallOverlaps(bool readOnlyCallFirst)
    {
        var grain = GetGrain<IReadOnlyReentrantSchedulingGrain>();
        var firstId = readOnlyCallFirst ? "R" : "W";
        var secondId = readOnlyCallFirst ? "W" : "R";

        var first = readOnlyCallFirst
            ? grain.BlockReadOnly(firstId)
            : grain.BlockWritable(firstId);
        await WaitForEntry(grain, firstId);
        var second = readOnlyCallFirst
            ? grain.BlockWritable(secondId)
            : grain.BlockReadOnly(secondId);
        await WaitForEntry(grain, secondId);

        Assert.Equal(
            ImmutableArray.Create($"enter:{firstId}", $"enter:{secondId}"),
            await Await(grain.GetEvents(), "Reentrant ReadOnly and writable operations to enter"));
        Assert.Equal(
            InvokeMethodOptions.ReadOnly,
            Find(
                typeof(IReadOnlyPolicySchedulingGrain),
                nameof(IReadOnlyPolicySchedulingGrain.BlockReadOnly),
                typeof(Task),
                typeof(string)).Options);

        await Release(grain, firstId);
        await Await(first, $"first Reentrant operation '{firstId}' to exit");
        await Release(grain, secondId);
        await Await(second, $"second Reentrant operation '{secondId}' to exit");

        Assert.Equal(
            ImmutableArray.Create(
                $"enter:{firstId}",
                $"enter:{secondId}",
                $"exit:{firstId}",
                $"exit:{secondId}"),
            await Await(grain.GetEvents(), "Reentrant ReadOnly and writable operations to exit"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [TestCategory("Functional"), TestCategory("ReadOnly")]
    public async Task ReadOnlyAndMayInterleave_TruePredicateAllowsWritableOverlapInBothArrivalOrders(
        bool readOnlyCallFirst)
    {
        var grain = GetGrain<IReadOnlyMayInterleaveTrueSchedulingGrain>();
        var firstId = readOnlyCallFirst ? "R" : "W";
        var secondId = readOnlyCallFirst ? "W" : "R";

        var first = readOnlyCallFirst
            ? grain.BlockReadOnly(firstId)
            : grain.BlockWritable(firstId);
        await WaitForEntry(grain, firstId);
        var second = readOnlyCallFirst
            ? grain.BlockWritable(secondId)
            : grain.BlockReadOnly(secondId);
        await WaitForEntry(grain, secondId);

        Assert.Equal(
            ImmutableArray.Create($"enter:{firstId}", $"enter:{secondId}"),
            await Await(grain.GetEvents(), "MayInterleave ReadOnly and writable operations to enter"));

        await Release(grain, firstId);
        await Await(first, $"first MayInterleave operation '{firstId}' to exit");
        await Release(grain, secondId);
        await Await(second, $"second MayInterleave operation '{secondId}' to exit");

        Assert.Equal(
            ImmutableArray.Create(
                $"enter:{firstId}",
                $"enter:{secondId}",
                $"exit:{firstId}",
                $"exit:{secondId}"),
            await Await(grain.GetEvents(), "MayInterleave ReadOnly and writable operations to exit"));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [TestCategory("Functional"), TestCategory("ReadOnly")]
    public async Task ReadOnlyAndMayInterleave_FalsePredicateStillUsesReadOnlyPairRule(
        bool firstCallIsReadOnly,
        bool secondCallIsReadOnly)
    {
        var grain = GetGrain<IReadOnlyMayInterleaveFalseSchedulingGrain>();
        var firstId = firstCallIsReadOnly ? "R1" : "W1";
        var secondId = secondCallIsReadOnly ? (firstCallIsReadOnly ? "R2" : "R1") : "W2";

        var first = firstCallIsReadOnly
            ? grain.BlockReadOnly(firstId)
            : grain.BlockWritable(firstId);
        await WaitForEntry(grain, firstId);
        var second = secondCallIsReadOnly
            ? grain.BlockReadOnly(secondId)
            : grain.BlockWritable(secondId);

        if (firstCallIsReadOnly && secondCallIsReadOnly)
        {
            await WaitForEntry(grain, secondId);
            Assert.Equal(
                ImmutableArray.Create("enter:R1", "enter:R2"),
                await Await(grain.GetEvents(), "two ReadOnly operations to enter with a false predicate"));
        }
        else
        {
            await WaitForWaitingRequestCount(grain, 1, "incompatible operation to be queued");
            await Checkpoint(grain, "incompatible-call-queued");
            Assert.Equal(
                ImmutableArray.Create($"enter:{firstId}", "checkpoint:incompatible-call-queued"),
                await Await(grain.GetEvents(), "incompatible call to remain queued with a false predicate"));
        }

        await Release(grain, firstId);
        await Await(first, $"first false-predicate operation '{firstId}' to exit");
        if (!(firstCallIsReadOnly && secondCallIsReadOnly))
        {
            await WaitForEntry(grain, secondId);
        }

        await Release(grain, secondId);
        await Await(second, $"second false-predicate operation '{secondId}' to exit");

        var expected = firstCallIsReadOnly && secondCallIsReadOnly
            ? ImmutableArray.Create("enter:R1", "enter:R2", "exit:R1", "exit:R2")
            : ImmutableArray.Create(
                $"enter:{firstId}",
                "checkpoint:incompatible-call-queued",
                $"exit:{firstId}",
                $"enter:{secondId}",
                $"exit:{secondId}");
        Assert.Equal(
            expected,
            await Await(grain.GetEvents(), "false-predicate operations to exit in scheduler order"));
    }

    [Fact, TestCategory("Functional"), TestCategory("ReadOnly")]
    public async Task MayInterleave_WhenInterleavableBlockerCompletes_RequiresCompatibilityWithAllSurvivors()
    {
        var grain = GetGrain<IReadOnlyMayInterleaveReadSchedulingGrain>();

        var initialRead = grain.BlockReadOnly("R1");
        await WaitForEntry(grain, "R1");
        var remainingRead = grain.BlockReadOnly("R2");
        await WaitForEntry(grain, "R2");
        var initialWrite = grain.BlockWritable("W1");
        await WaitForEntry(grain, "W1");

        await Release(grain, "R1");
        await Await(initialRead, "initial ReadOnly operation to exit");

        var laterWrite = grain.BlockWritable("W2");
        await WaitForWaitingRequestCount(grain, 1, "later writable operation to be queued");
        await Checkpoint(grain, "write-queued");

        Assert.Equal(
            ImmutableArray.Create(
                "enter:R1",
                "enter:R2",
                "enter:W1",
                "exit:R1",
                "checkpoint:write-queued"),
            await Await(grain.GetEvents(), "later writable operation to remain queued"));

        await Release(grain, "W1");
        await Await(initialWrite, "initial writable operation to exit");
        await WaitForEntry(grain, "W2");
        await Release(grain, "W2");
        await Await(laterWrite, "later writable operation to exit");
        await Release(grain, "R2");
        await Await(remainingRead, "remaining ReadOnly operation to exit");

        Assert.Equal(
            ImmutableArray.Create(
                "enter:R1",
                "enter:R2",
                "enter:W1",
                "exit:R1",
                "checkpoint:write-queued",
                "exit:W1",
                "enter:W2",
                "exit:W2",
                "exit:R2"),
            await Await(grain.GetEvents(), "all MayInterleave operations to exit in compatible order"));
    }

    [Fact, TestCategory("Functional"), TestCategory("ReadOnly")]
    public async Task ReadOnlyBatch_WhenLaterReadCompletesFirst_WritableWaitsForInitialRead()
    {
        var grain = GetGrain<IReadOnlySchedulingGrain>();

        var firstRead = grain.BlockReadOnly("R1");
        await WaitForEntry(grain, "R1");
        var secondRead = grain.BlockReadOnly("R2");
        await WaitForEntry(grain, "R2");

        await Release(grain, "R2");
        await Await(secondRead, "later ReadOnly operation to exit");

        var write = grain.BlockWritable("W");
        await WaitForWaitingRequestCount(grain, 1, "writable operation to be queued");
        await Checkpoint(grain, "write-queued");
        var eventsWhileInitialReadIsActive =
            await Await(grain.GetEvents(), "event snapshot while the initial ReadOnly operation is active");

        await Release(grain, "R1");
        await Await(firstRead, "initial ReadOnly operation to exit");
        await WaitForEntry(grain, "W");
        await Release(grain, "W");
        await Await(write, "writable operation to exit");

        Assert.Equal(
            ImmutableArray.Create("enter:R1", "enter:R2", "exit:R2", "checkpoint:write-queued"),
            eventsWhileInitialReadIsActive);
        Assert.Equal(
            ImmutableArray.Create(
                "enter:R1",
                "enter:R2",
                "exit:R2",
                "checkpoint:write-queued",
                "exit:R1",
                "enter:W",
                "exit:W"),
            await Await(grain.GetEvents(), "all operations to exit in blocker-preserving order"));
    }

    [Fact, TestCategory("Functional"), TestCategory("ReadOnly")]
    public async Task ReadOnlyBatch_WhenInitialReadCompletes_WritableWaitsForRemainingRead()
    {
        var grain = GetGrain<IReadOnlySchedulingGrain>();

        var firstRead = grain.BlockReadOnly("R1");
        await WaitForEntry(grain, "R1");
        var secondRead = grain.BlockReadOnly("R2");
        await WaitForEntry(grain, "R2");

        await Release(grain, "R1");
        await Await(firstRead, "initial ReadOnly operation to exit");

        var write = grain.BlockWritable("W");
        await WaitForWaitingRequestCount(grain, 1, "writable operation to be queued");
        await Checkpoint(grain, "write-queued");
        var eventsWhileSecondReadIsActive =
            await Await(grain.GetEvents(), "event snapshot while the remaining ReadOnly operation is active");

        await Release(grain, "R2");
        await Await(secondRead, "remaining ReadOnly operation to exit");
        await WaitForEntry(grain, "W");
        await Release(grain, "W");
        await Await(write, "writable operation to exit");

        Assert.Equal(
            ImmutableArray.Create("enter:R1", "enter:R2", "exit:R1", "checkpoint:write-queued"),
            eventsWhileSecondReadIsActive);
        Assert.Equal(
            ImmutableArray.Create(
                "enter:R1",
                "enter:R2",
                "exit:R1",
                "checkpoint:write-queued",
                "exit:R2",
                "enter:W",
                "exit:W"),
            await Await(grain.GetEvents(), "all operations to exit"));
    }

    private TGrain GetGrain<TGrain>() where TGrain : IGrainWithStringKey =>
        _fixture.GrainFactory.GetGrain<TGrain>($"{nameof(ReadOnlyAttributeTests)}-{Guid.NewGuid():N}");

    private RequestOptionsRecord Find(
        Type declaringInterface,
        string methodName,
        Type returnType,
        params Type[] parameterTypes) =>
        Find(declaringInterface, methodName, returnType, 0, parameterTypes);

    private RequestOptionsRecord Find(
        Type declaringInterface,
        string methodName,
        Type returnType,
        int genericArity,
        params Type[] parameterTypes)
    {
        var matches = _recorder.Records.Where(record =>
            record.DeclaringInterface == declaringInterface
            && record.MethodName == methodName
            && record.GenericArity == genericArity
            && record.ParameterTypes.SequenceEqual(parameterTypes)
            && record.ReturnType == returnType);

        return Assert.Single(matches);
    }

    private static async Task Await(Task task, string phase)
    {
        try
        {
            await task.WaitAsync(OperationTimeout);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException($"Timed out after {OperationTimeout} while waiting for {phase}.", exception);
        }
    }

    private static async Task<T> Await<T>(Task<T> task, string phase)
    {
        try
        {
            return await task.WaitAsync(OperationTimeout);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException($"Timed out after {OperationTimeout} while waiting for {phase}.", exception);
        }
    }

    private static Task WaitForEntry(IReadOnlySchedulingGrain grain, string operationId) =>
        Await(grain.WaitForEntry(operationId), $"operation '{operationId}' to enter");

    private static Task Release(IReadOnlySchedulingGrain grain, string operationId) =>
        Await(grain.Release(operationId), $"operation '{operationId}' to be released");

    private static Task Checkpoint(IReadOnlySchedulingGrain grain, string checkpointId) =>
        Await(grain.Checkpoint(checkpointId), $"checkpoint '{checkpointId}'");

    private static Task WaitForEntry(IReadOnlyPolicySchedulingGrain grain, string operationId) =>
        Await(grain.WaitForEntry(operationId), $"operation '{operationId}' to enter");

    private static Task Release(IReadOnlyPolicySchedulingGrain grain, string operationId) =>
        Await(grain.Release(operationId), $"operation '{operationId}' to be released");

    private static Task Checkpoint(IReadOnlyPolicySchedulingGrain grain, string checkpointId) =>
        Await(grain.Checkpoint(checkpointId), $"checkpoint '{checkpointId}'");

    private async Task WaitForWaitingRequestCount(IAddressable grain, int expectedCount, string phase)
    {
        var grainId = ((GrainReference)grain).GrainId;
        var stopwatch = Stopwatch.StartNew();
        int? actualCount = null;

        while (stopwatch.Elapsed < OperationTimeout)
        {
            if (_fixture.HostedCluster.TryGetGrainContext(grainId, out var context)
                && context is ActivationData activation)
            {
                lock (activation)
                {
                    actualCount = activation.WaitingCount;
                    if (actualCount >= expectedCount)
                    {
                        return;
                    }
                }
            }

            await Task.Delay(10);
        }

        throw new TimeoutException(
            $"Timed out after {OperationTimeout} while waiting for {phase}. Expected at least {expectedCount} waiting request(s), observed {actualCount?.ToString() ?? "no activation"}.");
    }

    public sealed class Fixture : BaseTestClusterFixture
    {
        public RequestOptionsRecorder Recorder => Client.ServiceProvider.GetRequiredService<RequestOptionsRecorder>();

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.Options.InitialSilosCount = 1;
            builder.AddClientBuilderConfigurator<ClientConfigurator>();
        }

        private sealed class ClientConfigurator : IClientBuilderConfigurator
        {
            public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
            {
                clientBuilder.ConfigureServices(services =>
                {
                    services.AddSingleton<RequestOptionsRecorder>();
                    services.AddSingleton<IOutgoingGrainCallFilter, RequestOptionsOutgoingFilter>();
                });
            }
        }
    }

    public sealed class RequestOptionsOutgoingFilter(RequestOptionsRecorder recorder) : IOutgoingGrainCallFilter
    {
        public async Task Invoke(IOutgoingGrainCallContext context)
        {
            var request = Assert.IsAssignableFrom<IRequest>(context.Request);
            var method = context.InterfaceMethod;
            recorder.Record(
                new RequestOptionsRecord(
                    method.DeclaringType ?? throw new InvalidOperationException($"Method '{method}' has no declaring type."),
                    method.Name,
                    method.IsGenericMethod ? method.GetGenericArguments().Length : 0,
                    method.GetParameters().Select(static parameter => parameter.ParameterType).ToImmutableArray(),
                    method.ReturnType,
                    request.Options));

            await context.Invoke();
        }
    }

    public sealed class RequestOptionsRecorder
    {
        private readonly ConcurrentQueue<RequestOptionsRecord> _records = new();

        public ImmutableArray<RequestOptionsRecord> Records => [.. _records];

        public void Clear() => _records.Clear();

        public void Record(RequestOptionsRecord record) => _records.Enqueue(record);
    }

    public sealed record RequestOptionsRecord(
        Type DeclaringInterface,
        string MethodName,
        int GenericArity,
        ImmutableArray<Type> ParameterTypes,
        Type ReturnType,
        InvokeMethodOptions Options);
}
