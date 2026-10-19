using CsCheck;
using Orleans.DurableTasks;
using Xunit;

namespace Orleans.DurableTasks.Abstractions.Tests;

/// <summary>
/// Verifies over generated bodies that a replay, which returns recorded effects without running them, assigns every
/// operation the identifier which the first run assigned.
/// </summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
[TestCategory("BVT")]
public sealed class ReplayIdentityPropertyTests
{
    private static readonly string[] Names = ["a", "b", "c"];

    private enum Kind
    {
        NamedChild,
        UnnamedChild,
        NamedEffect,
        UnnamedEffect,
        WhenAll,
        UnnamedDetachedPost,
        NamedDetachedPost,
        RootedDetachedPost,
        Version,
    }

    /// <summary>One operation of a generated body; an effect's flags add a nested effect and a rejected durable operation.</summary>
    private sealed record Operation(Kind Kind, int Name, int Width, bool NestedEffect, bool NestedOperation)
    {
        public override string ToString() => $"{Kind}({Names[Name]}, {Width}, {NestedEffect}, {NestedOperation})";
    }

    [Fact]
    public void ReplayAssignsTheIdentifiersOfTheFirstRun()
    {
        var operation = Gen.Select(
            Gen.Enum<Kind>(),
            Gen.Int[0, Names.Length - 1],
            Gen.Int[1, 3],
            Gen.Bool,
            Gen.Bool,
            static (kind, name, width, nestedEffect, nestedOperation) => new Operation(kind, name, width, nestedEffect, nestedOperation));

        operation.Array[0, 12].Sample(
            static operations =>
            {
                var records = new Dictionary<TaskId, DurableTaskResponse>();
                var first = Run(operations, records);
                var replay = Run(operations, records);

                Assert.Equal(first.Identifiers, replay.Identifiers);
                Assert.Equal(0, replay.EffectRuns);

                // Each operation of a run has its own identifier; a supplied root identifier names one task globally.
                var owned = first.Identifiers.Where(static entry => entry.Kind is not ("rooted" or "version")).Select(static entry => entry.Id).ToArray();
                Assert.Equal(owned.Length, owned.Distinct().Count());
            },
            iter: 200,
            threads: 1,
            print: static operations => string.Join(", ", operations.Select(static operation => operation.ToString())));
    }

    private static RecordingContext Run(Operation[] operations, Dictionary<TaskId, DurableTaskResponse> records)
    {
        var context = new RecordingContext(records);
        var response = DurableTaskRuntimeHelper.RunAsync(DurableTask.Run(_ => BodyAsync(operations)), context).AsTask().GetAwaiter().GetResult();
        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, response.Status);
        return context;
    }

    private static async Task BodyAsync(Operation[] operations)
    {
        foreach (var operation in operations)
        {
            var name = Names[operation.Name];
            switch (operation.Kind)
            {
                case Kind.NamedChild:
                    await DurableTask.Run(static _ => 0).WithId(name);
                    break;
                case Kind.UnnamedChild:
                    await DurableTask.Run(static _ => 0);
                    break;
                case Kind.NamedEffect:
                    DurableTask.RunInline(name, () => Effect(operation));
                    break;
                case Kind.UnnamedEffect:
                    DurableTask.RunInline(() => Effect(operation));
                    break;
                case Kind.WhenAll:
                    await DurableTask.WhenAll(Enumerable.Range(0, operation.Width).Select(static _ => DurableTask.Run(static _ => 0)));
                    break;
                case Kind.UnnamedDetachedPost:
                    _ = DurableTask.Run(static _ => 0).Detached().Post();
                    break;
                case Kind.NamedDetachedPost:
                    _ = DurableTask.Run(static _ => 0).WithId(name).Detached().Post();
                    break;
                case Kind.RootedDetachedPost:
                    _ = DurableTask.Run(static _ => 0).Detached($"root-{name}").Post();
                    break;
                case Kind.Version:
                    _ = DurableTask.GetVersion(name, 0, 1);
                    break;
            }
        }
    }

    /// <summary>An effect whose body a replay doesn't run, so nothing it does may take an identifier.</summary>
    private static int Effect(Operation operation)
    {
        var result = 1;
        if (operation.NestedEffect)
        {
            result += DurableTask.RunInline(static () => 1) + DurableTask.RunInline(Names[operation.Name], static () => 1);
        }

        if (operation.NestedOperation)
        {
            Assert.Throws<InvalidOperationException>(static () => DurableTask.Run(static _ => 0).Post());
            Assert.Throws<InvalidOperationException>(() => DurableTask.GetVersion(Names[operation.Name], 0, 1));
        }

        return result;
    }

    /// <summary>A host which completes every operation at once, records effects, and lists the identifiers which a run uses.</summary>
    private sealed class RecordingContext(Dictionary<TaskId, DurableTaskResponse> records) : DurableTaskContext(TaskId.CreateRoot("root"))
    {
        public List<(string Kind, string Id)> Identifiers { get; } = [];

        public int EffectRuns { get; private set; }

        public override DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;

        public override Guid Incarnation => Guid.Empty;

        protected override ValueTask<IScheduledTaskHandle> StartTaskAsync(
            TaskId taskId,
            DurableTask taskDefinition,
            DurableTaskStartOptions options,
            CancellationToken cancellationToken)
        {
            Identifiers.Add(("start", taskId.ToString()));
            return new(new CompletedHandle(taskId));
        }

        protected override IScheduledTaskHandle PostTask(TaskId taskId, DurableTask taskDefinition, DurableTaskStartOptions options)
        {
            Identifiers.Add((taskId.ToString().StartsWith("root-", StringComparison.Ordinal) ? "rooted" : "post", taskId.ToString()));
            return new CompletedHandle(taskId);
        }

        protected override DurableTaskResponse RunEffect<TState>(TaskId effectId, TState state, Func<TState, DurableTaskResponse> effect)
        {
            Identifiers.Add(("effect", effectId.ToString()));
            if (!records.TryGetValue(effectId, out var outcome))
            {
                EffectRuns++;
                records[effectId] = outcome = effect(state);
            }

            return outcome;
        }

        protected override int GetVersion(string name, int minSupported, int maxSupported)
        {
            Identifiers.Add(("version", name));
            return maxSupported;
        }

        protected override ValueTask<DurableTaskResponse> ScheduleDelayAsync(TaskId taskId, DateTimeOffset dueTime, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        protected override ValueTask<TaskId> SelectCompletionAsync(TaskId decisionId, IReadOnlyList<TaskId> candidates, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class CompletedHandle(TaskId taskId) : IScheduledTaskHandle
    {
        public TaskId TaskId => taskId;

        public ValueTask<DurableTaskResponse> WaitAsync(CancellationToken cancellationToken) => new(DurableTaskResponse.FromResult(0));

        public ValueTask<DurableTaskResponse> PollAsync(PollingOptions options, CancellationToken cancellationToken)
            => new(DurableTaskResponse.FromResult(0));

        public ValueTask CancelAsync(string? reason, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
