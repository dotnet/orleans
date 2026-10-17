using System.Collections.Immutable;

namespace Orleans.Journaling.Garnet.Tests;

/// <summary>
/// Records clients' operations on journals and checks that each journal's history is linearizable.
/// </summary>
/// <remarks>
/// <para>
/// The model of a journal is its content, a sequence of unique values, or its absence after a particular deletion.
/// Clients perform conditional operations, as journal storage does: an append or a delete succeeds only if the journal
/// is in the state the client last observed, and otherwise reports a conflict.
/// </para>
/// <para>
/// The checker searches for a total order of each journal's operations which respects real time (an operation which
/// completed before another began precedes it) and in which every operation's result matches the model (Wing and
/// Gong's algorithm, with memoization of visited configurations, as in Lowe's and Porcupine's checkers). An operation
/// whose outcome is unknown, because it failed with a timeout, may take effect at any point after it began, or never.
/// </para>
/// <para>
/// Every mutation is conditioned on one exact state and creates a state no other mutation creates, so states never
/// recur and the current state determines every mutation applied so far. The search therefore tracks only the
/// operations with known outcomes it has linearized, as a real-time frontier plus the few operations linearized ahead
/// of it, and the current state's identity. A write with an unknown outcome applies only in the state it names, so it
/// applies at most once without being tracked, and a read with an unknown outcome constrains nothing and is dropped.
/// The search is bounded: a journal whose history exceeds the budget is reported as inconclusive.
/// </para>
/// </remarks>
internal sealed class LinearizabilityHistory
{
    /// <summary>The maximum number of configurations the search visits per journal history.</summary>
    internal const int MaxVisited = 4_000_000;

    /// <summary>The default bound on the time to check every journal.</summary>
    internal static readonly TimeSpan DefaultCheckTimeout = TimeSpan.FromMinutes(3);

    private readonly Lock _lock = new();
    private readonly Dictionary<string, List<HistoryOp>> _ops = [];
    private long _clock;

    public long Now() => Interlocked.Increment(ref _clock);

    /// <summary>Records an operation which began at <see cref="HistoryOp.Invoked"/> and has now completed.</summary>
    public void Record(string journal, HistoryOp op)
    {
        lock (_lock)
        {
            if (!_ops.TryGetValue(journal, out var list))
            {
                _ops[journal] = list = [];
            }

            list.Add(op with { Returned = op.Outcome == HistoryOutcome.Unknown ? long.MaxValue : Now() });
        }
    }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _ops.Values.Sum(list => list.Count);
            }
        }
    }

    /// <summary>Gets the number of recorded operations whose outcome is unknown.</summary>
    public int UnknownCount
    {
        get
        {
            lock (_lock)
            {
                return _ops.Values.Sum(list => list.Count(op => op.Outcome == HistoryOutcome.Unknown));
            }
        }
    }

    /// <summary>Checks every journal's history, within <paramref name="timeout"/> in total.</summary>
    /// <returns>A description of each journal whose history is not linearizable, or could not be checked within the budget.</returns>
    public List<string> Check(TimeSpan? timeout = null)
    {
        List<KeyValuePair<string, HistoryOp[]>> histories;
        lock (_lock)
        {
            histories = _ops.Select(entry => KeyValuePair.Create(entry.Key, entry.Value.ToArray())).ToList();
        }

        var budget = timeout ?? DefaultCheckTimeout;
        var deadline = DateTime.UtcNow + budget;
        var failures = new List<string>();

        // The search recurses once per operation, so it runs on a thread with a large stack. It is bounded by the
        // deadline and MaxVisited; the join's own bound reports a checker which overruns them.
        var thread = new Thread(
            () =>
            {
                foreach (var (journal, ops) in histories)
                {
                    var result = IsLinearizable(ops, deadline);
                    if (result != CheckResult.Linearizable)
                    {
                        var failure = $"{journal}: {Describe(ops, result, deadline)}";
                        lock (failures)
                        {
                            failures.Add(failure);
                        }
                    }
                }
            },
            maxStackSize: 1 << 30)
        {
            IsBackground = true,
        };
        thread.Start();
        if (!thread.Join(budget + TimeSpan.FromSeconds(30)))
        {
            lock (failures)
            {
                failures.Add($"the linearizability checker did not finish within {budget}; histories: {Summarize(histories)}");
                return [.. failures];
            }
        }

        return failures;
    }

    private static string Summarize(List<KeyValuePair<string, HistoryOp[]>> histories)
        => string.Join(", ", histories.Select(entry => $"{entry.Key} ({entry.Value.Length} operations, {entry.Value.Count(op => op.Outcome == HistoryOutcome.Unknown)} unknown)"));

    /// <summary>
    /// Describes a failed check: for a history which is not linearizable, the earliest time by which it is no longer
    /// linearizable (treating operations still in progress then as having unknown outcomes), and the operations
    /// around it.
    /// </summary>
    private static string Describe(HistoryOp[] ops, CheckResult result, DateTime deadline)
    {
        var size = $"{ops.Length} operations, {ops.Count(op => op.Outcome == HistoryOutcome.Unknown)} with unknown outcomes";
        if (result == CheckResult.Inconclusive)
        {
            return $"inconclusive: the search exceeded its budget ({MaxVisited} configurations or the deadline) on {size}";
        }

        var times = ops.Where(op => op.Returned != long.MaxValue).Select(op => op.Returned).Order().ToArray();
        int low = 0, high = times.Length - 1;
        while (low < high)
        {
            var middle = (low + high) / 2;
            var prefixResult = IsLinearizable(Prefix(ops, times[middle]), deadline);
            if (prefixResult == CheckResult.Inconclusive)
            {
                return $"not linearizable ({size}); locating the first violation exceeded the search budget";
            }

            if (prefixResult == CheckResult.Linearizable)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        var prefix = Prefix(ops, times[low]).OrderBy(op => op.Invoked).ToList();
        return $"not linearizable by time {times[low]} ({size}); the last operations: "
            + string.Join("; ", prefix.TakeLast(16).Select(op => op.ToString()));

        static HistoryOp[] Prefix(HistoryOp[] ops, long time)
            => ops.Where(op => op.Invoked <= time)
                .Select(op => op.Returned <= time ? op : op with { Outcome = HistoryOutcome.Unknown, Returned = long.MaxValue, Observed = null })
                .ToArray();
    }

    internal enum CheckResult
    {
        Linearizable,
        NotLinearizable,
        Inconclusive,
    }

    internal static CheckResult IsLinearizable(IReadOnlyList<HistoryOp> ops, DateTime deadline)
    {
        try
        {
            return new Search(ops, deadline).Run() ? CheckResult.Linearizable : CheckResult.NotLinearizable;
        }
        catch (BudgetExceededException)
        {
            return CheckResult.Inconclusive;
        }
    }

    private sealed class BudgetExceededException : Exception;

    /// <summary>One search over one journal's history.</summary>
    private sealed class Search
    {
        private readonly HistoryOp[] _known;
        private readonly Dictionary<long, List<HistoryOp>> _unknownWritesByExpected = [];
        private readonly Dictionary<string, long> _absenceIds = new(StringComparer.Ordinal);
        private readonly HashSet<Configuration> _visited = [];
        private readonly HashSet<int> _ahead = [];
        private readonly DateTime _deadline;

        public Search(IReadOnlyList<HistoryOp> ops, DateTime deadline)
        {
            _deadline = deadline;
            _known = ops.Where(op => op.Outcome != HistoryOutcome.Unknown).OrderBy(op => op.Invoked).ToArray();
            foreach (var op in ops)
            {
                // A read with an unknown outcome observed nothing, so it constrains nothing.
                if (op.Outcome == HistoryOutcome.Unknown && op.Kind != HistoryKind.Read)
                {
                    var expected = Identity(op.Expected!);
                    if (!_unknownWritesByExpected.TryGetValue(expected, out var list))
                    {
                        _unknownWritesByExpected[expected] = list = [];
                    }

                    list.Add(op);
                }
            }
        }

        public bool Run() => Visit(frontier: 0, JournalState.Absent);

        /// <summary>
        /// Searches from the configuration in which the known operations before <paramref name="frontier"/>, and those
        /// in <see cref="_ahead"/>, are linearized, and the journal is in <paramref name="state"/>.
        /// </summary>
        private bool Visit(int frontier, JournalState state)
        {
            if (frontier == _known.Length)
            {
                return true;
            }

            var stateId = Identity(state);
            if (!_visited.Add(new Configuration(frontier, [.. _ahead.Order()], stateId)))
            {
                return false;
            }

            if (_visited.Count > MaxVisited || (_visited.Count & 0xFFF) == 0 && DateTime.UtcNow > _deadline)
            {
                throw new BudgetExceededException();
            }

            // An operation can come next only if it began before every pending known operation returned. Known
            // operations are ordered by invocation, and each returns after it began, so the scan stops once an
            // operation began after the horizon found so far.
            var horizon = long.MaxValue;
            for (var i = frontier; i < _known.Length && _known[i].Invoked <= horizon; i++)
            {
                if (!_ahead.Contains(i) && _known[i].Returned < horizon)
                {
                    horizon = _known[i].Returned;
                }
            }

            for (var i = frontier; i < _known.Length && _known[i].Invoked <= horizon; i++)
            {
                if (_ahead.Contains(i))
                {
                    continue;
                }

                foreach (var next in Apply(_known[i], state))
                {
                    if (VisitAfter(frontier, i, next))
                    {
                        return true;
                    }
                }
            }

            if (_unknownWritesByExpected.TryGetValue(stateId, out var unknown))
            {
                foreach (var op in unknown)
                {
                    if (op.Invoked <= horizon)
                    {
                        foreach (var next in Apply(op, state))
                        {
                            if (Visit(frontier, next))
                            {
                                return true;
                            }
                        }
                    }
                }
            }

            return false;
        }

        /// <summary>Visits the configuration after linearizing known operation <paramref name="index"/>.</summary>
        private bool VisitAfter(int frontier, int index, JournalState next)
        {
            if (index != frontier)
            {
                _ahead.Add(index);
                var found = Visit(frontier, next);
                _ahead.Remove(index);
                return found;
            }

            // Advance the frontier past every operation already linearized ahead of it.
            var advanced = frontier + 1;
            var absorbed = new List<int>();
            while (_ahead.Remove(advanced))
            {
                absorbed.Add(advanced++);
            }

            var result = Visit(advanced, next);
            foreach (var i in absorbed)
            {
                _ahead.Add(i);
            }

            return result;
        }

        /// <summary>Gets a number identifying a state: the mutation which created it.</summary>
        private long Identity(JournalState state)
        {
            if (state.Values is { Count: > 0 } values)
            {
                return values[^1] + 1L;
            }

            if (state.Values is not null)
            {
                return long.MinValue;
            }

            if (state.AbsenceETag.Length == 0)
            {
                return 0;
            }

            if (!_absenceIds.TryGetValue(state.AbsenceETag, out var id))
            {
                _absenceIds[state.AbsenceETag] = id = -1 - _absenceIds.Count;
            }

            return id;
        }

        private readonly struct Configuration(int frontier, int[] ahead, long state) : IEquatable<Configuration>
        {
            private readonly int _frontier = frontier;
            private readonly int[] _ahead = ahead;
            private readonly long _state = state;

            public bool Equals(Configuration other)
                => _frontier == other._frontier && _state == other._state && _ahead.AsSpan().SequenceEqual(other._ahead);

            public override bool Equals(object? obj) => obj is Configuration other && Equals(other);

            public override int GetHashCode()
            {
                var hash = new HashCode();
                hash.Add(_frontier);
                hash.Add(_state);
                foreach (var index in _ahead)
                {
                    hash.Add(index);
                }

                return hash.ToHashCode();
            }
        }
    }

    /// <summary>Gets the states which can follow <paramref name="state"/> if <paramref name="op"/> takes effect at this point.</summary>
    private static IEnumerable<JournalState> Apply(HistoryOp op, JournalState state)
    {
        switch (op.Kind)
        {
            case HistoryKind.Read:
                if (op.Outcome == HistoryOutcome.Unknown || state.Equals(op.Observed))
                {
                    yield return state;
                }

                break;
            case HistoryKind.Append:
            case HistoryKind.Delete:
                var matches = state.Equals(op.Expected);
                var next = op.Kind == HistoryKind.Append ? state.Append(op.Value) : JournalState.AbsentAfter(op.DeletionETag!);
                if (op.Outcome == HistoryOutcome.Success && matches)
                {
                    yield return next;
                }
                else if (op.Outcome == HistoryOutcome.Conflict && !matches)
                {
                    yield return state;
                }
                else if (op.Outcome == HistoryOutcome.Unknown && matches)
                {
                    yield return next;
                }

                break;
        }
    }
}

/// <summary>
/// A journal's content, or its absence, identified by the deletion which caused it (empty for a journal never written).
/// </summary>
/// <remarks>
/// Writes conditioned on absence name the absence they observed, so absences after different deletions are different states.
/// </remarks>
internal sealed record JournalState(ImmutableList<int>? Values, string AbsenceETag = "")
{
    public static JournalState Absent { get; } = new((ImmutableList<int>?)null);

    public static JournalState AbsentAfter(string deletionETag) => new(null, deletionETag);

    public JournalState Append(int value) => new((Values ?? []).Add(value));

    public bool Equals(JournalState? other)
        => other is not null
            && (Values is null
                ? other.Values is null && string.Equals(AbsenceETag, other.AbsenceETag, StringComparison.Ordinal)
                : other.Values is not null && Values.Count == other.Values.Count && Values.SequenceEqual(other.Values));

    public override int GetHashCode() => Values is null ? AbsenceETag.GetHashCode(StringComparison.Ordinal) : HashCode.Combine(Values.Count, Values.Count > 0 ? Values[^1] : 0);

    public override string ToString() => Values is null ? $"absent({AbsenceETag[..Math.Min(6, AbsenceETag.Length)]})" : $"[{string.Join(",", Values)}]";
}
internal enum HistoryKind
{
    Read,
    Append,
    Delete,
}

internal enum HistoryOutcome
{
    Success,
    Conflict,
    Unknown,
}

/// <summary>An operation in a journal's history.</summary>
/// <param name="Client">The client which performed it.</param>
/// <param name="Kind">The operation.</param>
/// <param name="Invoked">The logical time at which it began.</param>
/// <param name="Expected">For a conditional write, the state the client last observed.</param>
/// <param name="Value">For an append, the value appended.</param>
/// <param name="Observed">For a read, the state it returned.</param>
/// <param name="Outcome">The outcome.</param>
/// <param name="Returned">The logical time at which it completed.</param>
/// <param name="DeletionETag">For a delete, the ETag its tombstone carries.</param>
internal sealed record HistoryOp(
    int Client,
    HistoryKind Kind,
    long Invoked,
    JournalState? Expected,
    int Value,
    JournalState? Observed,
    HistoryOutcome Outcome,
    long Returned = 0,
    string? DeletionETag = null)
{
    public override string ToString() => $"c{Client} {Kind}({(Kind == HistoryKind.Append ? $"{Value} if {Expected}" : Kind == HistoryKind.Delete ? $"if {Expected}" : "")}) [{Invoked}, {(Returned == long.MaxValue ? "?" : Returned)}] -> {Outcome}{(Observed is null ? "" : $" {Observed}")}";
}
