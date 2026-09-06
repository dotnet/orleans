using System.Reflection;
using Orleans.Transactions.TestKit.Consistency;
using TestExtensions;
using Xunit;

namespace Orleans.Transactions.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Transactions")]
[TestCategory("BVT"), TestCategory("Transactions")]
public class ConsistencyTestHarnessInputValidationTests
{
    [Fact]
    public async Task RunRandomTransactionSequence_NullGrainFactory_ThrowsBeforeOutputOrHistoryMutation()
    {
        var constructorFactory = CreateRecordingGrainFactory(out var constructorFactoryProxy);
        var harness = CreateHarness(constructorFactory);
        SeedValidHistory(harness);
        var initialAbortedCount = harness.NumAborted;
        var outputCallCount = 0;

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(
            () => harness.RunRandomTransactionSequence(
                partition: 7,
                count: 3,
                grainFactory: null!,
                output: _ => outputCallCount++));

        Assert.Equal("grainFactory", exception.ParamName);
        Assert.Equal(0, outputCallCount);
        Assert.Equal(0, constructorFactoryProxy.CallCount);
        AssertHistoryPreserved(harness, initialAbortedCount);

        var secondConstructorFactory = CreateRecordingGrainFactory(out var secondConstructorFactoryProxy);
        var secondHarness = CreateHarness(secondConstructorFactory);
        SeedValidHistory(secondHarness);
        initialAbortedCount = secondHarness.NumAborted;
        exception = await Assert.ThrowsAsync<ArgumentNullException>(
            () => secondHarness.RunRandomTransactionSequence(
                partition: 7,
                count: 3,
                grainFactory: null!,
                output: null!));

        Assert.Equal("grainFactory", exception.ParamName);
        Assert.Equal(0, secondConstructorFactoryProxy.CallCount);
        AssertHistoryPreserved(secondHarness, initialAbortedCount);
    }

    [Fact]
    public async Task RunRandomTransactionSequence_NullOutput_ThrowsBeforeGetGrainOrHistoryMutation()
    {
        var grainFactory = CreateRecordingGrainFactory(out var grainFactoryProxy);
        var harness = CreateHarness(grainFactory);
        SeedValidHistory(harness);
        var initialAbortedCount = harness.NumAborted;

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(
            () => harness.RunRandomTransactionSequence(
                partition: 11,
                count: 4,
                grainFactory,
                output: null!));

        Assert.Equal("output", exception.ParamName);
        Assert.Equal(0, grainFactoryProxy.CallCount);
        AssertHistoryPreserved(harness, initialAbortedCount);
    }

    private static ConsistencyTestHarness CreateHarness(IGrainFactory grainFactory) =>
        new(
            grainFactory,
            numGrains: 5,
            seed: 29,
            avoidDeadlocks: true,
            avoidTimeouts: true,
            readWrite: ReadWriteDetermination.PerGrain,
            tolerateUnknownExceptions: false);

    private static void SeedValidHistory(ConsistencyTestHarness harness)
    {
        harness.RecordSucceeded(
            new Observation
            {
                Grain = 2,
                SeqNo = 0,
                WriterTx = ConsistencyTestHarness.InitialTx,
                ExecutingTx = "existing-transaction",
            });
        harness.CheckConsistency();
    }

    private static void AssertHistoryPreserved(
        ConsistencyTestHarness harness,
        int expectedAbortedCount)
    {
        Assert.Equal(expectedAbortedCount, harness.NumAborted);
        harness.RecordSucceeded(
            new Observation
            {
                Grain = 2,
                SeqNo = 1,
                WriterTx = "continuing-transaction",
                ExecutingTx = "continuing-transaction",
            });
        harness.CheckConsistency();
    }

    private static IGrainFactory CreateRecordingGrainFactory(out RecordingGrainFactory proxy)
    {
        var grainFactory = DispatchProxy.Create<IGrainFactory, RecordingGrainFactory>();
        proxy = (RecordingGrainFactory)(object)grainFactory;
        return grainFactory;
    }

    private class RecordingGrainFactory : DispatchProxy
    {
        public int CallCount { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            CallCount++;
            throw new InvalidOperationException(
                $"Grain factory method {targetMethod?.Name} must not be called.");
        }
    }
}
