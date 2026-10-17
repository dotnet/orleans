using System.Collections.Immutable;

namespace Orleans.Journaling.Garnet.Tests;

/// <summary>
/// Tests the linearizability checker which the fault tests rely on: it accepts linearizable histories, including
/// operations with unknown outcomes, rejects violations, and stays fast on histories with many unknown outcomes.
/// </summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
public sealed class LinearizabilityHistoryTests
{
    private static readonly DateTime Deadline = DateTime.MaxValue;

    private static JournalState State(params int[] values) => new(values.ToImmutableList());

    private static HistoryOp Read(int client, long invoked, long returned, JournalState observed)
        => new(client, HistoryKind.Read, invoked, null, 0, observed, HistoryOutcome.Success, returned);

    private static HistoryOp Append(int client, long invoked, long returned, JournalState expected, int value, HistoryOutcome outcome = HistoryOutcome.Success)
        => new(client, HistoryKind.Append, invoked, expected, value, null, outcome, outcome == HistoryOutcome.Unknown ? long.MaxValue : returned);

    private static HistoryOp Delete(int client, long invoked, long returned, JournalState expected, string eTag, HistoryOutcome outcome = HistoryOutcome.Success)
        => new(client, HistoryKind.Delete, invoked, expected, 0, null, outcome, outcome == HistoryOutcome.Unknown ? long.MaxValue : returned, eTag);

    private static LinearizabilityHistory.CheckResult Check(params HistoryOp[] ops) => LinearizabilityHistory.IsLinearizable(ops, Deadline);

    [Fact]
    public void Accepts_ConcurrentConditionalWrites()
    {
        // Two clients both observe [1]; one append wins, the other conflicts, and a read sees the winner.
        Assert.Equal(LinearizabilityHistory.CheckResult.Linearizable, Check(
            Append(1, 1, 2, JournalState.Absent, 1),
            Append(1, 3, 6, State(1), 2),
            Append(2, 4, 7, State(1), 3, HistoryOutcome.Conflict),
            Read(2, 8, 9, State(1, 2))));
    }

    [Fact]
    public void Accepts_UnknownOutcomesWhichDidOrDidNotTakeEffect()
    {
        Assert.Equal(LinearizabilityHistory.CheckResult.Linearizable, Check(
            Append(1, 1, 0, JournalState.Absent, 1, HistoryOutcome.Unknown),
            Read(2, 5, 6, State(1)),
            Append(1, 7, 0, State(1), 2, HistoryOutcome.Unknown),
            Read(2, 8, 9, State(1)),
            new HistoryOp(3, HistoryKind.Read, 2, null, 0, null, HistoryOutcome.Unknown, long.MaxValue)));
    }

    [Fact]
    public void Accepts_DeletesAndRecreation()
    {
        Assert.Equal(LinearizabilityHistory.CheckResult.Linearizable, Check(
            Append(1, 1, 2, JournalState.Absent, 1),
            Delete(1, 3, 4, State(1), "d1"),
            Read(2, 5, 6, JournalState.AbsentAfter("d1")),
            Append(2, 7, 8, JournalState.AbsentAfter("d1"), 2),
            Read(1, 9, 10, State(2))));
    }

    public static TheoryData<string> Violations => [.. ViolationsByName.Keys];

    private static readonly Dictionary<string, HistoryOp[]> ViolationsByName = new()
    {
        ["acknowledged append lost"] =
        [
            Append(1, 1, 2, JournalState.Absent, 1),
            Append(1, 3, 4, State(1), 2),
            Read(2, 5, 6, State(1)),
        ],
        ["append reported as a conflict took effect"] =
        [
            Append(1, 1, 2, JournalState.Absent, 1),
            Append(2, 3, 4, State(1), 2, HistoryOutcome.Conflict),
            Read(1, 5, 6, State(1, 2)),
        ],
        ["stale read after a newer read"] =
        [
            Append(1, 1, 2, JournalState.Absent, 1),
            Append(1, 3, 6, State(1), 2),
            Read(2, 4, 5, State(1, 2)),
            Read(3, 7, 8, State(1)),
        ],
        ["deleted journal resurrected"] =
        [
            Append(1, 1, 2, JournalState.Absent, 1),
            Delete(1, 3, 4, State(1), "d1"),
            Read(2, 5, 6, State(1)),
        ],
        ["retry applied twice"] =
        [
            Append(1, 1, 0, JournalState.Absent, 1, HistoryOutcome.Unknown),
            Delete(2, 5, 6, State(1), "d1"),
            Read(2, 7, 8, State(1)),
        ],
    };

    [Theory]
    [MemberData(nameof(Violations))]
    public void Rejects_Violations(string name)
    {
        Assert.Equal(LinearizabilityHistory.CheckResult.NotLinearizable, Check(ViolationsByName[name]));

        var history = new LinearizabilityHistory();
        foreach (var op in ViolationsByName[name])
        {
            history.Record("journal", op);
        }

        Assert.Single(history.Check(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void ManyUnknownOutcomes_AreCheckedQuickly()
    {
        // Each read and write with an unknown outcome may be linearized anywhere after it began, or never. Without
        // pruning, every subset of them is a distinct configuration of the search.
        var ops = new List<HistoryOp>();
        var state = JournalState.Absent;
        var time = 0L;
        for (var i = 0; i < 400; i++)
        {
            ops.Add(new HistoryOp(2, HistoryKind.Read, ++time, null, 0, null, HistoryOutcome.Unknown, long.MaxValue));
            ops.Add(Append(3, ++time, 0, state, 100_000 + i, HistoryOutcome.Unknown));
            var invoked = ++time;
            ops.Add(Append(1, invoked, ++time, state, i));
            state = state.Append(i);
            ops.Add(Read(1, ++time, ++time, state));
        }

        var start = DateTime.UtcNow;
        Assert.Equal(LinearizabilityHistory.CheckResult.Linearizable, LinearizabilityHistory.IsLinearizable(ops, DateTime.UtcNow + TimeSpan.FromSeconds(30)));
        Assert.True(DateTime.UtcNow - start < TimeSpan.FromSeconds(10), $"The check took {DateTime.UtcNow - start}.");
    }

    [Fact]
    public void ExceededBudget_IsReportedAsInconclusive()
    {
        // Many concurrent reads and one impossible read: refuting the history explores every subset of the reads.
        var ops = Enumerable.Range(0, 200).Select(i => Read(i, 1, 2, JournalState.Absent)).Append(Read(200, 3, 4, State(1))).ToArray();
        Assert.Equal(LinearizabilityHistory.CheckResult.Inconclusive, LinearizabilityHistory.IsLinearizable(ops, DateTime.UtcNow));
    }
}
