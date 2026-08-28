using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Storage;
using UnitTests.GrainInterfaces;
using Xunit;
using Assert = Xunit.Assert;

namespace Tester.EventSourcingTests;

/// <summary>
/// Integration tests for clear-log behavior on non-Azure log test grain configurations.
/// </summary>
[TestSuite("Functional")]
[TestProvider("None")]
[TestArea("EventSourcing")]
public class LogTestGrainClearTests : IClassFixture<EventSourcingClusterFixture>
{
    private readonly EventSourcingClusterFixture fixture;

    public LogTestGrainClearTests(EventSourcingClusterFixture fixture)
    {
        this.fixture = fixture;
    }

    [Theory, TestCategory("EventSourcing"), TestCategory("Functional")]
    [InlineData("TestGrains.LogTestGrainDefaultStorage", 721001L)]
    [InlineData("TestGrains.LogTestGrainSharedLogStorage", 721002L)]
    [InlineData("TestGrains.LogTestGrainCustomStoragePrimaryCluster", 721003L)]
    [InlineData("TestGrains.LogTestGrainJournaledStateStorage", 721004L)]
    public async Task ClearLog_ResetDropsTentativeAndAllowsFurtherWrites(string grainClass, long grainId)
    {
        var grain = this.fixture.GrainFactory.GetGrain<ILogTestGrain>(grainId, grainClass);

        await grain.Clear();
        await grain.SetAGlobal(10);
        Assert.Equal(10, await grain.GetAGlobal());
        Assert.Equal(1, await grain.GetConfirmedVersion());

        await grain.SetALocal(99);
        await grain.SetBLocal(77);
        var tentativeBeforeClear = await grain.GetBothLocal();
        Assert.Equal(99, tentativeBeforeClear.A);
        Assert.Equal(77, tentativeBeforeClear.B);

        await grain.Clear();
        Assert.Equal(0, await grain.GetConfirmedVersion());

        var confirmedAfterClear = await grain.GetBothGlobal();
        Assert.Equal(0, confirmedAfterClear.A);
        Assert.Equal(0, confirmedAfterClear.B);

        var tentativeAfterClear = await grain.GetBothLocal();
        Assert.Equal(0, tentativeAfterClear.A);
        Assert.Equal(0, tentativeAfterClear.B);

        await grain.SetAGlobal(41);
        await grain.IncrementAGlobal();
        Assert.Equal(42, await grain.GetAGlobal());
        Assert.Equal(2, await grain.GetConfirmedVersion());

        await grain.Clear();
        var exceptions = await RunConcurrentOperationsAroundClear(grain);
        Assert.DoesNotContain(exceptions, static ex => ex is InconsistentStateException);
        Assert.Empty(exceptions);

        await grain.Clear();
        await grain.SetAGlobal(7);
        Assert.Equal(7, await grain.GetAGlobal());
        Assert.Equal(1, await grain.GetConfirmedVersion());
    }

    [Fact, TestCategory("EventSourcing"), TestCategory("Functional")]
    public async Task JournaledStateLogStorage_PersistsEventsAcrossActivation()
    {
        var grain = this.fixture.GrainFactory.GetGrain<ILogTestGrain>(721005L, "TestGrains.LogTestGrainJournaledStateStorage");

        await grain.Clear();
        await grain.SetAGlobal(10);
        await grain.IncrementAGlobal();

        var eventLog = await grain.GetEventLog();
        Assert.Equal(2, eventLog.Count);
        Assert.Equal(11, await grain.GetAGlobal());

        await this.fixture.HostedCluster.DeactivateAsync(grain);

        grain = this.fixture.GrainFactory.GetGrain<ILogTestGrain>(721005L, "TestGrains.LogTestGrainJournaledStateStorage");
        Assert.Equal(11, await grain.GetAGlobal());
        Assert.Equal(2, await grain.GetConfirmedVersion());

        eventLog = await grain.GetEventLog();
        Assert.Equal(2, eventLog.Count);
    }

    [Fact, TestCategory("EventSourcing"), TestCategory("Functional")]
    public async Task JournaledStateLogStorage_RetrievesIndexedLogSegments()
    {
        var grain = this.fixture.GrainFactory.GetGrain<ILogTestGrain>(721017L, "TestGrains.LogTestGrainJournaledStateStorage");

        await grain.Clear();
        await grain.SetAGlobal(10);
        await grain.SetBGlobal(20);
        await grain.IncrementAGlobal();

        var segment = await grain.GetEventLogSegment(1, 3);
        Assert.Collection(
            segment,
            entry => Assert.Equal(20, Assert.IsType<TestGrains.UpdateB>(entry).Val),
            entry => Assert.IsType<TestGrains.IncrementA>(entry));
        Assert.Empty(await grain.GetEventLogSegment(2, 2));
        await Assert.ThrowsAsync<ArgumentException>(() => grain.GetEventLogSegment(-1, 1));
        await Assert.ThrowsAsync<ArgumentException>(() => grain.GetEventLogSegment(2, 4));
    }

    [Fact, TestCategory("EventSourcing"), TestCategory("Functional")]
    public async Task JournaledStateLogStorage_DoesNotRetrieveTentativeLogEntries()
    {
        var grain = this.fixture.GrainFactory.GetGrain<ILogTestGrain>(721018L, "TestGrains.LogTestGrainJournaledStateStorage");

        await grain.Clear();
        var appendStarted = this.fixture.BlockNextJournalAppend(grain);
        try
        {
            await grain.SetALocal(41);
            await appendStarted.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            Assert.Equal(0, await grain.GetConfirmedVersion());
            await Assert.ThrowsAsync<ArgumentException>(() => grain.GetEventLogSegment(0, 1));
        }
        finally
        {
            this.fixture.ReleaseBlockedJournalAppend(grain);
        }

        await grain.SynchronizeGlobalState();
        var segment = await grain.GetEventLogSegment(0, 1);
        Assert.Equal(41, Assert.IsType<TestGrains.UpdateA>(Assert.Single(segment)).Val);
    }

    [Fact, TestCategory("EventSourcing"), TestCategory("Functional")]
    public async Task JournaledStateLogStorage_ClearPreservesOtherJournaledState()
    {
        var grain = this.fixture.GrainFactory.GetGrain<ILogTestGrainWithAuxiliaryState>(721006L, "TestGrains.LogTestGrainJournaledStateStorageWithAuxiliaryState");

        await grain.Clear();
        await grain.SetAuxiliaryValue(17);
        await grain.SetAGlobal(10);

        Assert.Equal(17, await grain.GetAuxiliaryValue());
        Assert.Equal(10, await grain.GetAGlobal());
        Assert.Equal(1, await grain.GetConfirmedVersion());

        await grain.Clear();

        Assert.Equal(17, await grain.GetAuxiliaryValue());
        Assert.Equal(0, await grain.GetConfirmedVersion());
        Assert.Equal(0, await grain.GetAGlobal());

        await this.fixture.HostedCluster.DeactivateAsync(grain);

        grain = this.fixture.GrainFactory.GetGrain<ILogTestGrainWithAuxiliaryState>(721006L, "TestGrains.LogTestGrainJournaledStateStorageWithAuxiliaryState");

        Assert.Equal(17, await grain.GetAuxiliaryValue());
        Assert.Equal(0, await grain.GetConfirmedVersion());
        Assert.Equal(0, await grain.GetAGlobal());

        await grain.SetAGlobal(42);
        Assert.Equal(17, await grain.GetAuxiliaryValue());
        Assert.Equal(1, await grain.GetConfirmedVersion());
    }

    [Fact, TestCategory("EventSourcing"), TestCategory("Functional")]
    public async Task JournaledStateLogStorage_UncommittedFailureFailsCompleteJournalBatch()
    {
        var grain = this.fixture.GrainFactory.GetGrain<ILogTestGrainWithAuxiliaryState>(721007L, "TestGrains.LogTestGrainJournaledStateStorageWithAuxiliaryState");

        await grain.Clear();
        await grain.SetAuxiliaryValueAndAGlobal(17, 10);
        var deactivated = this.fixture.HostedCluster.WaitForDeactivationAsync(grain);
        this.fixture.FailNextJournalAppend(grain, new IOException("Expected transient append failure."));

        await Assert.ThrowsAsync<OrleansException>(() => grain.SetAuxiliaryValueAndAGlobal(23, 41));
        await deactivated.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        grain = this.fixture.GrainFactory.GetGrain<ILogTestGrainWithAuxiliaryState>(721007L, "TestGrains.LogTestGrainJournaledStateStorageWithAuxiliaryState");
        Assert.Equal(17, await grain.GetAuxiliaryValue());
        Assert.Equal(10, await grain.GetAGlobal());
        Assert.Equal(1, await grain.GetConfirmedVersion());
    }

    [Fact, TestCategory("EventSourcing"), TestCategory("Functional")]
    public async Task JournaledStateLogStorage_ConflictFailsCompleteJournalBatch()
    {
        var grain = this.fixture.GrainFactory.GetGrain<ILogTestGrainWithAuxiliaryState>(721008L, "TestGrains.LogTestGrainJournaledStateStorageWithAuxiliaryState");
        await grain.Clear();
        await grain.SetAuxiliaryValueAndAGlobal(17, 10);

        var deactivated = this.fixture.HostedCluster.WaitForDeactivationAsync(grain);
        this.fixture.FailNextJournalAppend(grain, new InconsistentStateException("Expected append conflict."));

        await Assert.ThrowsAsync<OrleansException>(() => grain.SetAuxiliaryValueAndAGlobal(23, 41));
        await deactivated.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        grain = this.fixture.GrainFactory.GetGrain<ILogTestGrainWithAuxiliaryState>(721008L, "TestGrains.LogTestGrainJournaledStateStorageWithAuxiliaryState");
        Assert.Equal(17, await grain.GetAuxiliaryValue());
        Assert.Equal(10, await grain.GetAGlobal());
        Assert.Equal(1, await grain.GetConfirmedVersion());
    }

    [Fact, TestCategory("EventSourcing"), TestCategory("Functional")]
    public async Task JournaledStateLogStorage_AmbiguousConflictRecognizesCommittedJournalBatch()
    {
        var grain = this.fixture.GrainFactory.GetGrain<ILogTestGrainWithAuxiliaryState>(721009L, "TestGrains.LogTestGrainJournaledStateStorageWithAuxiliaryState");
        await grain.Clear();
        this.fixture.FailNextJournalAppend(grain, new InconsistentStateException("Expected post-commit conflict."), afterWrite: true);

        await grain.SetAuxiliaryValueAndAGlobal(23, 41);

        Assert.Equal(23, await grain.GetAuxiliaryValue());
        Assert.Equal(41, await grain.GetAGlobal());
        Assert.Equal(1, await grain.GetConfirmedVersion());

        await this.fixture.HostedCluster.DeactivateAsync(grain);
        grain = this.fixture.GrainFactory.GetGrain<ILogTestGrainWithAuxiliaryState>(721009L, "TestGrains.LogTestGrainJournaledStateStorageWithAuxiliaryState");
        Assert.Equal(23, await grain.GetAuxiliaryValue());
        Assert.Equal(41, await grain.GetAGlobal());
        Assert.Equal(1, await grain.GetConfirmedVersion());
    }

    [Fact, TestCategory("EventSourcing"), TestCategory("Functional")]
    public async Task JournaledStateLogStorage_AmbiguousTransientFailureRecognizesCommittedJournalBatch()
    {
        var grain = this.fixture.GrainFactory.GetGrain<ILogTestGrainWithAuxiliaryState>(721012L, "TestGrains.LogTestGrainJournaledStateStorageWithAuxiliaryState");
        await grain.Clear();
        this.fixture.FailNextJournalAppend(grain, new IOException("Expected post-commit transient failure."), afterWrite: true);

        await grain.SetAuxiliaryValueAndAGlobal(23, 41);

        Assert.Equal(23, await grain.GetAuxiliaryValue());
        Assert.Equal(41, await grain.GetAGlobal());
        Assert.Equal(1, await grain.GetConfirmedVersion());

        await this.fixture.HostedCluster.DeactivateAsync(grain);
        grain = this.fixture.GrainFactory.GetGrain<ILogTestGrainWithAuxiliaryState>(721012L, "TestGrains.LogTestGrainJournaledStateStorageWithAuxiliaryState");
        Assert.Equal(23, await grain.GetAuxiliaryValue());
        Assert.Equal(41, await grain.GetAGlobal());
        Assert.Equal(1, await grain.GetConfirmedVersion());
    }

    [Fact, TestCategory("EventSourcing"), TestCategory("Functional")]
    public async Task JournaledStateLogStorage_ClearFailureFailsAndPreservesDurableState()
    {
        var grain = this.fixture.GrainFactory.GetGrain<ILogTestGrainWithAuxiliaryState>(721010L, "TestGrains.LogTestGrainJournaledStateStorageWithAuxiliaryState");
        await grain.Clear();
        await grain.SetAuxiliaryValueAndAGlobal(17, 10);
        var deactivated = this.fixture.HostedCluster.WaitForDeactivationAsync(grain);
        this.fixture.FailNextJournalAppend(grain, new IOException("Expected transient clear failure."));

        await Assert.ThrowsAsync<IOException>(() => grain.Clear());
        await deactivated.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        grain = this.fixture.GrainFactory.GetGrain<ILogTestGrainWithAuxiliaryState>(721010L, "TestGrains.LogTestGrainJournaledStateStorageWithAuxiliaryState");
        Assert.Equal(17, await grain.GetAuxiliaryValue());
        Assert.Equal(10, await grain.GetAGlobal());
        Assert.Equal(1, await grain.GetConfirmedVersion());
    }

    [Fact, TestCategory("EventSourcing"), TestCategory("Functional")]
    public async Task JournaledStateLogStorage_ClearConflictFailsAndPreservesDurableState()
    {
        var grain = this.fixture.GrainFactory.GetGrain<ILogTestGrainWithAuxiliaryState>(721011L, "TestGrains.LogTestGrainJournaledStateStorageWithAuxiliaryState");
        await grain.Clear();
        await grain.SetAuxiliaryValueAndAGlobal(17, 10);

        var deactivated = this.fixture.HostedCluster.WaitForDeactivationAsync(grain);
        this.fixture.FailNextJournalAppend(grain, new InconsistentStateException("Expected clear conflict."));

        await Assert.ThrowsAsync<InconsistentStateException>(() => grain.Clear());
        await deactivated.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        grain = this.fixture.GrainFactory.GetGrain<ILogTestGrainWithAuxiliaryState>(721011L, "TestGrains.LogTestGrainJournaledStateStorageWithAuxiliaryState");
        Assert.Equal(17, await grain.GetAuxiliaryValue());
        Assert.Equal(10, await grain.GetAGlobal());
        Assert.Equal(1, await grain.GetConfirmedVersion());
    }

    [Fact, TestCategory("EventSourcing"), TestCategory("Functional")]
    public async Task JournaledStateLogStorage_RefreshPreservesUnflushedAuxiliaryState()
    {
        var grain = this.fixture.GrainFactory.GetGrain<ILogTestGrainWithAuxiliaryState>(721013L, "TestGrains.LogTestGrainJournaledStateStorageWithAuxiliaryState");
        await grain.Clear();

        Assert.Equal(23, await grain.SetAuxiliaryValueAndSynchronize(23));
        await grain.SetAGlobal(41);

        await this.fixture.HostedCluster.DeactivateAsync(grain);
        grain = this.fixture.GrainFactory.GetGrain<ILogTestGrainWithAuxiliaryState>(721013L, "TestGrains.LogTestGrainJournaledStateStorageWithAuxiliaryState");
        Assert.Equal(23, await grain.GetAuxiliaryValue());
        Assert.Equal(41, await grain.GetAGlobal());
        Assert.Equal(1, await grain.GetConfirmedVersion());
    }

    [Fact, TestCategory("EventSourcing"), TestCategory("Functional")]
    public async Task JournaledStateLogStorage_StagingFailureDoesNotPersistPartialBatch()
    {
        var grain = this.fixture.GrainFactory.GetGrain<ILogTestGrain>(721014L, "TestGrains.LogTestGrainJournaledStateStorage");
        await grain.Clear();
        var deactivated = this.fixture.HostedCluster.WaitForDeactivationAsync(grain);

        await Assert.ThrowsAsync<OrleansException>(() => grain.RaiseEventsWithUnsupportedSecond());
        await deactivated.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        grain = this.fixture.GrainFactory.GetGrain<ILogTestGrain>(721014L, "TestGrains.LogTestGrainJournaledStateStorage");
        Assert.Equal(0, await grain.GetAGlobal());
        Assert.Equal(0, await grain.GetConfirmedVersion());
        Assert.Empty(await grain.GetEventLog());
    }

    [Fact, TestCategory("EventSourcing"), TestCategory("Functional")]
    public async Task JournaledStateLogStorage_RejectsReentrantGrain()
    {
        var grain = this.fixture.GrainFactory.GetGrain<ILogTestGrain>(721015L, "TestGrains.LogTestGrainJournaledStateReentrantStorage");

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => grain.GetAGlobal());

        Assert.Contains("requires a single, turn-serialized grain activation", exception.ToString(), StringComparison.Ordinal);
    }

    private static async Task<List<Exception>> RunConcurrentOperationsAroundClear(ILogTestGrain grain)
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exceptions = new List<Exception>();
        var syncLock = new object();

        Task Run(Func<Task> operation)
        {
            return Task.Run(async () =>
            {
                await gate.Task;
                try
                {
                    await operation();
                }
                catch (Exception exception)
                {
                    lock (syncLock)
                    {
                        exceptions.Add(exception);
                    }
                }
            });
        }

        var operations = new[]
        {
            Run(() => grain.SetALocal(1)),
            Run(() => grain.SetAGlobal(2)),
            Run(() => grain.IncrementAGlobal()),
            Run(() => grain.Clear()),
            Run(() => grain.SetAGlobal(3)),
            Run(async () => _ = await grain.GetAGlobal()),
        };

        gate.SetResult(true);
        await Task.WhenAll(operations);
        return exceptions;
    }
}

[TestSuite("Functional")]
[TestProvider("None")]
[TestArea("EventSourcing")]
public class LogTestGrainClearCommaClusterIdTests : IClassFixture<CommaClusterIdEventSourcingClusterFixture>
{
    private const long GrainId = 721016L;
    private const string GrainClass = "TestGrains.LogTestGrainJournaledStateStorageWithAuxiliaryState";
    private readonly CommaClusterIdEventSourcingClusterFixture fixture;

    public LogTestGrainClearCommaClusterIdTests(CommaClusterIdEventSourcingClusterFixture fixture)
    {
        this.fixture = fixture;
    }

    [Fact, TestCategory("EventSourcing"), TestCategory("Functional")]
    public async Task JournaledStateLogStorage_AmbiguousConflictWithCommaClusterId_RecognizesCommittedBatchBeforeAndAfterReactivation()
    {
        Assert.Equal("west,prod-v2.canary", this.fixture.HostedCluster.Options.ClusterId);

        var grain = this.fixture.GrainFactory.GetGrain<ILogTestGrainWithAuxiliaryState>(GrainId, GrainClass);

        await grain.Clear();
        await grain.SetAuxiliaryValueAndAGlobal(17, 10);

        Assert.Equal(17, await grain.GetAuxiliaryValue());
        Assert.Equal(10, await grain.GetAGlobal());
        Assert.Equal(1, await grain.GetConfirmedVersion());
        AssertEventLog(await grain.GetEventLog(), 10);

        this.fixture.FailNextJournalAppend(
            grain,
            new InconsistentStateException(
                $"Expected post-commit conflict for ClusterId '{CommaClusterIdEventSourcingClusterFixture.ClusterId}'."),
            afterWrite: true);

        await grain.SetAuxiliaryValueAndAGlobal(23, 41);

        Assert.Equal(23, await grain.GetAuxiliaryValue());
        Assert.Equal(41, await grain.GetAGlobal());
        Assert.Equal(2, await grain.GetConfirmedVersion());
        AssertEventLog(await grain.GetEventLog(), 10, 41);

        await this.fixture.HostedCluster.DeactivateAsync(grain);
        grain = this.fixture.GrainFactory.GetGrain<ILogTestGrainWithAuxiliaryState>(GrainId, GrainClass);

        Assert.Equal(23, await grain.GetAuxiliaryValue());
        Assert.Equal(41, await grain.GetAGlobal());
        Assert.Equal(2, await grain.GetConfirmedVersion());
        AssertEventLog(await grain.GetEventLog(), 10, 41);
    }

    private static void AssertEventLog(IReadOnlyList<object> eventLog, params int[] expectedValues)
    {
        Assert.Equal(expectedValues.Length, eventLog.Count);
        for (var index = 0; index < expectedValues.Length; index++)
        {
            var update = Assert.IsType<TestGrains.UpdateA>(eventLog[index]);
            Assert.Equal(expectedValues[index], update.Val);
        }
    }
}
