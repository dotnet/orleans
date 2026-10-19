using CsCheck;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Diagnostics;
using Orleans.Journaling;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.Tests;

/// <summary>
/// Verifies, over random sequences of accesses to every journaled state type in both journal formats, that a replay of a
/// durable task body observes what the recording attempt observed and changes nothing which it already changed.
/// </summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class DurableTaskAccessReplayPropertyTests
{
    private static readonly TaskId WorkflowId = TaskId.CreateRoot("workflow");

    private static readonly Gen<Op> Operation = Gen.Select(Gen.Int[0, Op.Kinds - 1], Gen.Int[0, 4], Gen.Int[0, 4]).Select(static (kind, a, b) => new Op(kind, a, b));

    private static readonly Gen<Scenario> Scenarios = Gen.Select(Operation.Array[1, 30], Operation.Array[0, 6]).Select(static (body, outside) => new Scenario(body, outside));

    private static TimeSpan Timeout => TimeSpan.FromSeconds(10);

    [Theory]
    [MemberData(nameof(DurableTaskJournalHarness.Formats), MemberType = typeof(DurableTaskJournalHarness))]
    public Task RecordedAccessesReplayEquivalently(string journalFormatKey)
    {
        Func<Scenario, Task> run = scenario => ActivationScheduler.RunAsync(() => RunAsync(journalFormatKey, scenario.Body, scenario.Outside));
        return Scenarios.SampleAsync(run, iter: 25, threads: 1, print: static scenario => scenario.ToString());
    }

    // Oracle: the replay returns the recording attempt's outcomes in order, never diverges, and leaves the state as the
    // recording attempt and the other grain work left it.
    private static async Task RunAsync(string journalFormatKey, Op[] body, Op[] outside)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var outcomes = new List<string>();
        var stalled = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = DurableTaskEvents.AllEvents.Subscribe(new EventObserver(evt =>
        {
            if (evt is DurableTaskEvents.ExecutionStalled stall && evt.TaskId == WorkflowId)
            {
                stalled.TrySetResult(stall.Exception);
            }
        }));

        await using var harness = new DurableTaskReplayHarness(grain => Workflow(grain, body, outcomes), journalFormatKey);
        string[] recorded;
        string expectedState;
        await using (var activation = await harness.ActivateAsync(cancellationToken))
        {
            await activation.ScheduleAsync(WorkflowId, cancellationToken);
            await WaitAtGateAsync(activation, stalled.Task);
            recorded = [.. outcomes];

            // Other grain work changes the same states after the body's accesses, and one write commits both.
            foreach (var op in outside)
            {
                _ = op.Apply(activation.Grain);
            }

            activation.Grain.RequestUnrelatedWrite();
            await activation.Grain.UnrelatedWrites;
            expectedState = Snapshot(activation.Grain);
        }

        outcomes.Clear();
        await using var replay = await harness.ActivateAsync(cancellationToken);
        await WaitAtGateAsync(replay, stalled.Task);
        Assert.Equal(recorded, outcomes);
        Assert.Equal(expectedState, Snapshot(replay.Grain));
        replay.Grain.OpenGate();
        Assert.True((await replay.GetResponseAsync(WorkflowId, cancellationToken)).IsCompleted);
        Assert.Equal(expectedState, Snapshot(replay.Grain));
        Assert.False(stalled.Task.IsCompleted);

        static async DurableTask Workflow(ReplayGrain grain, Op[] body, List<string> outcomes)
        {
            foreach (var op in body)
            {
                outcomes.Add(op.Apply(grain));
            }

            await grain.WaitAtGateAsync();
        }
    }

    private static async Task WaitAtGateAsync(ReplayActivation activation, Task<Exception> stalled)
    {
        var reached = await Task.WhenAny(activation.Grain.AtGate, stalled).WaitAsync(Timeout, TestContext.Current.CancellationToken);
        if (reached == stalled)
        {
            Assert.Fail($"The body stalled: {stalled.Result}");
        }
    }

    private static string Snapshot(ReplayGrain grain) =>
        $"value={grain.Counter.Value}; "
        + $"dictionary={string.Join(",", grain.Stock.OrderBy(static entry => entry.Key, StringComparer.Ordinal).Select(static entry => $"{entry.Key}:{entry.Value}"))}; "
        + $"list={string.Join(",", grain.List)}; queue={string.Join(",", grain.Queue)}; set={string.Join(",", grain.Set.Order())}; "
        + $"log={grain.Log.FirstSequence}:{string.Join(",", grain.Log)}:{grain.Log.IsSealed}; completion={grain.Completion.State.Status}";

    /// <summary>Accesses which a body makes, and accesses which other grain work makes after them.</summary>
    private sealed record Scenario(Op[] Body, Op[] Outside)
    {
        public override string ToString() => $"body: [{string.Join(", ", (IEnumerable<Op>)Body)}], outside: [{string.Join(", ", (IEnumerable<Op>)Outside)}]";
    }

    /// <summary>One access to the grain's journaled state.</summary>
    private sealed record Op(int Kind, int A, int B)
    {
        public const int Kinds = 60;

        private string Key => $"k{A}";

        public override string ToString() => $"{Kind}({A},{B})";

        /// <summary>Runs the access and describes its outcome: its result, or the type of the exception which it threw.</summary>
        public string Apply(ReplayGrain grain)
        {
            try
            {
                return Run(grain);
            }
            catch (Exception exception) when (exception is not DurableTaskAttemptCanceledException)
            {
                return $"!{exception.GetType().Name}";
            }
        }

        private string Run(ReplayGrain grain)
        {
            var other = new[] { A, B };
            return Kind switch
            {
                0 => $"{grain.Counter.Value}",
                1 => Do(() => grain.Counter.Value = A),
                2 => $"{grain.Stock[Key]}",
                3 => Do(() => grain.Stock[Key] = B),
                4 => grain.Stock.TryGetValue(Key, out var stock) ? $"{stock}" : "none",
                5 => $"{grain.Stock.ContainsKey(Key)}",
                6 => $"{grain.Stock.Count}",
                7 => string.Join(",", grain.Stock.Keys.Order(StringComparer.Ordinal)),
                8 => string.Join(",", grain.Stock.Values.Order()),
                9 => $"{grain.Stock.Remove(Key)}",
                10 => Do(() => grain.Stock.Add(Key, B)),
                11 => $"{grain.Stock.Contains(new KeyValuePair<string, int>(Key, B))}",
                12 => $"{grain.Stock.Remove(new KeyValuePair<string, int>(Key, B))}",
                13 => Do(grain.Stock.Clear),
                14 => string.Join(",", grain.Stock.OrderBy(static entry => entry.Key, StringComparer.Ordinal).Select(static entry => $"{entry.Key}:{entry.Value}")),
                15 => $"{grain.List[A]}",
                16 => Do(() => grain.List[A] = B),
                17 => Do(() => grain.List.Add(A)),
                18 => Do(() => grain.List.Insert(A, B)),
                19 => Do(() => grain.List.RemoveAt(A)),
                20 => $"{grain.List.Remove(A)}",
                21 => $"{grain.List.Contains(A)}",
                22 => $"{grain.List.IndexOf(A)}",
                23 => $"{grain.List.Count}",
                24 => string.Join(",", grain.List),
                25 => Do(() => grain.List.AddRange(other)),
                26 => Do(grain.List.Clear),
                27 => Do(() => grain.Queue.Enqueue(A)),
                28 => $"{grain.Queue.Dequeue()}",
                29 => grain.Queue.TryDequeue(out var dequeued) ? $"{dequeued}" : "none",
                30 => $"{grain.Queue.Peek()}",
                31 => grain.Queue.TryPeek(out var peeked) ? $"{peeked}" : "none",
                32 => $"{grain.Queue.Count}",
                33 => $"{grain.Queue.Contains(A)}",
                34 => string.Join(",", grain.Queue),
                35 => Do(grain.Queue.Clear),
                36 => $"{grain.Set.Add(A)}",
                37 => $"{grain.Set.Remove(A)}",
                38 => $"{grain.Set.Contains(A)}",
                39 => $"{grain.Set.Count}",
                40 => string.Join(",", grain.Set.Order()),
                41 => Do(() => grain.Set.UnionWith(other)),
                42 => Do(() => grain.Set.IntersectWith(other)),
                43 => Do(() => grain.Set.ExceptWith(other)),
                44 => Do(() => grain.Set.SymmetricExceptWith(other)),
                45 => $"{grain.Set.IsSubsetOf(other)}{grain.Set.IsProperSubsetOf(other)}{grain.Set.IsSupersetOf(other)}{grain.Set.IsProperSupersetOf(other)}{grain.Set.Overlaps(other)}{grain.Set.SetEquals(other)}",
                46 => Do(grain.Set.Clear),
                47 => $"{grain.Log.Append(A)}",
                48 => $"{grain.Log[A]}",
                49 => grain.Log.TryGet(A, out var entry) ? $"{entry}" : "none",
                50 => Do(() => grain.Log.TrimBefore(A)),
                51 => $"{grain.Log.Count}:{grain.Log.FirstSequence}:{grain.Log.NextSequence}:{grain.Log.IsSealed}",
                52 => string.Join(",", grain.Log),
                53 => A == 0 ? Do(grain.Log.Seal) : $"{grain.Log.Append(B)}",
                54 => Do(grain.Log.Clear),
                55 => $"{grain.Completion.TrySetResult(A)}",
                56 => $"{grain.Completion.TrySetCanceled()}",
                57 => $"{grain.Completion.State.Status}",
                58 => $"{grain.Counter.Value += A}",
                _ => $"{grain.Stock.TryGetValue(Key, out var current) && grain.Stock.Remove(Key)}:{current}",
            };
        }

        private static string Do(Action action)
        {
            action();
            return "done";
        }
    }
}
