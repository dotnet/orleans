using Orleans.Transactions.TestKit.Consistency;
using TestExtensions;
using Xunit;

namespace Orleans.Transactions.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Transactions")]
[TestCategory("BVT"), TestCategory("Transactions")]
public class ConsistencyTestHarnessTests
{
    [Fact]
    public void ValidHistory_PreservesVersionOrderingAndSerializableDependencies()
    {
        var harness = CreateHarness();

        harness.RecordSucceeded(
            Observe(grain: 0, version: 0, writer: ConsistencyTestHarness.InitialTx, transaction: "tx1"),
            Observe(grain: 0, version: 1, writer: "tx1", transaction: "tx1"),
            Observe(grain: 1, version: 0, writer: ConsistencyTestHarness.InitialTx, transaction: "tx1"));
        harness.RecordSucceeded(
            Observe(grain: 0, version: 1, writer: "tx1", transaction: "tx2"),
            Observe(grain: 0, version: 2, writer: "tx2", transaction: "tx2"),
            Observe(grain: 1, version: 0, writer: ConsistencyTestHarness.InitialTx, transaction: "tx2"),
            Observe(grain: 1, version: 1, writer: "tx2", transaction: "tx2"));

        Assert.Equal(0, harness.NumAborted);
        harness.CheckConsistency();
    }

    [Fact]
    public void RecordSucceeded_WithMixedTransactionIds_DoesNotMutateHistory()
    {
        var harness = CreateHarness();

        Assert.ThrowsAny<Exception>(() => harness.RecordSucceeded(
            Observe(0, 1, "tx1", "tx1"),
            Observe(0, 2, "tx1", "tx2")));

        harness.CheckConsistency();
    }

    [Fact]
    public void CheckConsistency_AfterRecordingMoreHistory_RebuildsDependencyGraph()
    {
        var harness = CreateHarness();
        harness.RecordSucceeded(Observe(0, 0, ConsistencyTestHarness.InitialTx, "tx1"));
        harness.RecordSucceeded(Observe(0, 1, "tx2", "tx2"));

        harness.CheckConsistency();

        harness.RecordSucceeded(Observe(1, 0, ConsistencyTestHarness.InitialTx, "tx2"));
        harness.RecordSucceeded(Observe(1, 1, "tx1", "tx1"));

        AssertInconsistent(harness, "found serializability violation");
    }

    [Fact]
    public void MissingVersion_IsRejected()
    {
        var harness = CreateHarness();
        harness.RecordSucceeded(
            Observe(0, 0, ConsistencyTestHarness.InitialTx, "tx1"),
            Observe(0, 2, "tx1", "tx1"));

        AssertInconsistent(harness, "is missing version v1, found v2 instead");
    }

    [Fact]
    public void MultipleWritersForVersion_AreRejected()
    {
        var harness = CreateHarness();
        harness.RecordSucceeded(Observe(0, 0, ConsistencyTestHarness.InitialTx, "reader"));
        harness.RecordObservation(Observe(0, 1, "tx1", "tx1"));
        harness.RecordObservation(Observe(0, 1, "tx2", "tx2"));

        AssertInconsistent(harness, "v1 has multiple writers");
    }

    [Fact]
    public void InitialVersionFromTransaction_IsRejected()
    {
        var harness = CreateHarness();
        harness.RecordSucceeded(Observe(0, 0, "tx1", "tx1"));

        AssertInconsistent(harness, $"v0 not written by {ConsistencyTestHarness.InitialTx}");
    }

    [Fact]
    public void AbortedWriter_IsRejected()
    {
        var harness = CreateHarness();
        harness.RecordSucceeded(Observe(0, 0, ConsistencyTestHarness.InitialTx, "reader"));
        harness.RecordObservation(Observe(0, 1, "tx1", "tx1"));
        harness.RecordAborted("tx1");

        Assert.Equal(1, harness.NumAborted);
        AssertInconsistent(harness, "v1 written by aborted transaction tx1");
    }

    [Fact]
    public void UnknownWriter_IsRejected()
    {
        var harness = CreateHarness();
        harness.RecordSucceeded(Observe(0, 0, ConsistencyTestHarness.InitialTx, "reader"));
        harness.RecordObservation(Observe(0, 1, "unknown", "unknown"));

        AssertInconsistent(harness, "v1 written by unknown transaction unknown");
    }

    [Fact]
    public void WriterMissingFromVersionReaders_IsRejected()
    {
        var harness = CreateHarness();
        harness.RecordSucceeded(Observe(0, 0, ConsistencyTestHarness.InitialTx, "writer"));
        harness.RecordSucceeded(
            Observe(1, 0, ConsistencyTestHarness.InitialTx, "reader"),
            Observe(1, 1, "writer", "reader"));

        AssertInconsistent(harness, "v1 writer writer missing");
    }

    [Fact]
    public void ReaderWithoutSuccessfulOutcome_IsRejected()
    {
        var harness = CreateHarness();
        harness.RecordObservation(Observe(0, 0, ConsistencyTestHarness.InitialTx, "aborted-reader"));
        harness.RecordAborted("aborted-reader");

        AssertInconsistent(harness, "v0 read by aborted transaction aborted-reader");
    }

    [Fact]
    public void CyclicDependency_IsRejected()
    {
        var harness = CreateHarness();
        harness.RecordSucceeded(
            Observe(0, 0, ConsistencyTestHarness.InitialTx, "tx1"),
            Observe(1, 1, "tx1", "tx1"));
        harness.RecordSucceeded(
            Observe(1, 0, ConsistencyTestHarness.InitialTx, "tx2"),
            Observe(0, 1, "tx2", "tx2"));

        AssertInconsistent(harness, "found serializability violation");
    }

    [Fact]
    public void InDoubtCommitFailure_IsRejectedByDefault()
    {
        var harness = CreateHarness();
        harness.RecordInDoubt("tx1", "failure during transaction commit");

        AssertInconsistent(harness, "exception during commit tx1");
    }

    [Fact]
    public void InDoubtCommitFailure_CanBeTolerated()
    {
        var harness = CreateHarness();
        harness.RecordInDoubt("tx1", "failure during transaction commit");

        harness.CheckConsistency(tolerateUnknownExceptions: true);
    }

    [Fact]
    public void InDoubtOutcome_AllowsRecoverableHistoryGaps()
    {
        var harness = CreateHarness();
        harness.RecordInDoubt("tx1", "transaction outcome is not yet known");
        harness.RecordSucceeded(Observe(0, 2, "tx1", "reader"));

        harness.CheckConsistency();
    }

    [Fact]
    public void GenericTimeout_IsRejectedByDefault()
    {
        var harness = CreateHarness();
        harness.RecordTimeout();

        AssertInconsistent(harness, "generic timeout exception caught");
    }

    [Fact]
    public void GenericTimeout_CanBeTolerated()
    {
        var harness = CreateHarness();
        harness.RecordTimeout();

        harness.CheckConsistency(tolerateGenericTimeouts: true);
    }

    [Fact]
    public void GenericTimeout_AllowsIncompleteHistory()
    {
        var harness = CreateHarness();
        harness.RecordTimeout();
        harness.RecordSucceeded(Observe(0, 2, "unknown", "reader"));

        harness.CheckConsistency(tolerateGenericTimeouts: true);
    }

    private static ConsistencyTestHarness CreateHarness() =>
        new(
            grainFactory: null!,
            numGrains: 2,
            seed: 0,
            avoidDeadlocks: true,
            avoidTimeouts: true,
            readWrite: ReadWriteDetermination.PerGrain,
            tolerateUnknownExceptions: false);

    private static Observation Observe(int grain, int version, string writer, string transaction) =>
        new()
        {
            Grain = grain,
            SeqNo = version,
            WriterTx = writer,
            ExecutingTx = transaction,
        };

    private static void AssertInconsistent(ConsistencyTestHarness harness, string expectedMessage)
    {
        var exception = Assert.ThrowsAny<Exception>(() => harness.CheckConsistency());
        Assert.Contains(expectedMessage, exception.Message);
    }
}
