using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;
using Xunit;

namespace Orleans.Journaling.Tests;

/// <summary>
/// Verifies that journaled state runs its public operations through the <see cref="JournaledStateContext"/> which the
/// manager's provider supplies, which can observe each operation or supply its outcome instead of running it.
/// </summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
public sealed class JournaledStateContextTests : JournalingTestBase
{
    [Fact]
    public async Task OperationsWithoutAContextRunDirectly()
    {
        var (list, _, _, provider) = await CreateStatesAsync();
        provider.Context = null;

        list.Add("a");

        Assert.Equal(["a"], list);
    }

    [Fact]
    public async Task ContextObservesEachPublicOperationOnceInOrder()
    {
        var (list, dictionary, value, provider) = await CreateStatesAsync();
        list.Add("a");
        list.Add("b");
        var context = provider.Context = new RecordingContext();

        Assert.Equal(2, list.Count);

        // Remove runs RemoveAt within the operation, which belongs to it.
        Assert.True(list.Remove("a"));
        dictionary["x"] = 1;
        Assert.True(dictionary.TryGetValue("x", out var x));
        Assert.Equal(1, x);
        value.Value = 7;
        var items = new List<string>();
        foreach (var item in list)
        {
            items.Add(item);
        }

        Assert.Equal(["b"], items);
        Assert.Equal(
            [
                (JournaledStateAccessKind.Read, JournaledStateOperation.ListCount, "list"),
                (JournaledStateAccessKind.Write, JournaledStateOperation.ListRemove, "list"),
                (JournaledStateAccessKind.Write, JournaledStateOperation.DictionarySet, "dictionary"),
                (JournaledStateAccessKind.Read, JournaledStateOperation.DictionaryTryGetValue, "dictionary"),
                (JournaledStateAccessKind.Write, JournaledStateOperation.ValueSet, "value"),
                (JournaledStateAccessKind.Read, JournaledStateOperation.ListSnapshot, "list"),
            ],
            context.Accesses.Select(static access => (access.Kind, access.Operation, access.Target)));
        Assert.True(Assert.IsType<bool>(context.Accesses[1].Outcome));
        Assert.Equal((true, 1), Assert.IsType<(bool, int)>(context.Accesses[3].Outcome));
    }

    [Fact]
    public async Task CollectionQueriesReturnDetachedSnapshots()
    {
        var (_, dictionary, _, provider) = await CreateStatesAsync();
        dictionary["a"] = 1;
        provider.Context = new RecordingContext();

        var keys = dictionary.Keys;
        dictionary["b"] = 2;

        // A replay returns the recorded snapshot, so the first run observes the same keys.
        Assert.Equal(["a"], keys);
    }

    [Fact]
    public async Task ContextSuppliesRecordedOutcomesWithoutRunningOperations()
    {
        var (list, dictionary, value, provider) = await CreateStatesAsync();
        value.Value = 5;
        var context = provider.Context = new RecordingContext();
        context.Recorded.Enqueue(1);
        context.Recorded.Enqueue(true);
        context.Recorded.Enqueue(default(JournaledStateAccessNoResult));

        // The recorded read, removal, and append return their outcomes and leave the state unchanged.
        Assert.Equal(1, value.Value);
        Assert.True(dictionary.Remove("missing"));
        list.Add("skipped");

        provider.Context = null;
        Assert.Equal(5, value.Value);
        Assert.Empty(list);
        Assert.Empty(context.Accesses);
    }

    [Fact]
    public async Task ContextObservesAndReplaysTheExceptionOfAnOperation()
    {
        var (_, dictionary, _, provider) = await CreateStatesAsync();
        var context = provider.Context = new RecordingContext();

        Assert.Throws<KeyNotFoundException>(() => dictionary["missing"]);
        Assert.IsType<KeyNotFoundException>(Assert.Single(context.Accesses).Outcome);

        provider.Context = null;
        dictionary["missing"] = 1;
        var replay = provider.Context = new RecordingContext();
        replay.Recorded.Enqueue(new KeyNotFoundException("recorded"));
        Assert.Equal("recorded", Assert.Throws<KeyNotFoundException>(() => dictionary["missing"]).Message);
    }

    [Fact]
    public async Task OperationWhichChangesStateAndThenThrowsReportsItsException()
    {
        var (list, _, _, provider) = await CreateStatesAsync();
        var context = provider.Context = new RecordingContext();

        Assert.Throws<InvalidOperationException>(() => list.AddRange(ItemsThenFailure()));

        // The change and the exception belong to the one operation, so a host records them together.
        var access = Assert.Single(context.Accesses);
        Assert.Equal(JournaledStateOperation.ListAddRange, access.Operation);
        Assert.IsType<InvalidOperationException>(access.Outcome);
        provider.Context = null;
        Assert.Equal(["a"], list);

        static IEnumerable<string> ItemsThenFailure()
        {
            yield return "a";
            throw new InvalidOperationException("The source failed.");
        }
    }

    [Fact]
    public async Task LiveObservationsRunDirectly()
    {
        var (list, _, value, provider) = await CreateStatesAsync();
        var context = provider.Context = new RecordingContext();

        _ = value.WatchAsync(DurableWatchMode.Committed, TestContext.Current.CancellationToken);
        _ = list.WatchAsync(DurableWatchMode.Committed, 1, TestContext.Current.CancellationToken);

        Assert.Empty(context.Accesses);
    }

    [Fact]
    public async Task BeginningADraftChecksTheSealInOneRecordedRead()
    {
        var sut = CreateTestSystem();
        var provider = new TestProvider();
        ((JournaledStateManager)sut.Manager).ContextProvider = provider;
        var log = new DurableLog<string>("log", sut.Manager, new OrleansBinaryDurableLogCommandCodec<string>(CodecProvider.GetCodec<string>(), SessionPool));
        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        log.Seal();

        // A replay of a run which began the draft before the seal returns the recorded outcome, so the draft begins.
        var context = provider.Context = new RecordingContext();
        context.Recorded.Enqueue(default(JournaledStateAccessNoResult));
        _ = log.BeginDraft();

        // A run which records the check records its failure, which a replay throws without checking the seal again.
        Assert.Throws<InvalidOperationException>(() => log.BeginDraft());
        var access = Assert.Single(context.Accesses);
        Assert.Equal((JournaledStateAccessKind.Read, JournaledStateOperation.LogBeginDraft), (access.Kind, access.Operation));
        Assert.IsType<InvalidOperationException>(access.Outcome);
    }

    [Fact]
    public async Task ReplayedSealClosesEveryRecreatedDraft()
    {
        var sut = CreateTestSystem();
        var provider = new TestProvider();
        ((JournaledStateManager)sut.Manager).ContextProvider = provider;
        var log = new DurableLog<string>("log", sut.Manager, new OrleansBinaryDurableLogCommandCodec<string>(CodecProvider.GetCodec<string>(), SessionPool));
        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        log.Seal();

        var context = provider.Context = new RecordingContext();
        context.Recorded.Enqueue(default(JournaledStateAccessNoResult));
        using var first = log.BeginDraft();
        context.Recorded.Enqueue(default(JournaledStateAccessNoResult));
        using var second = log.BeginDraft();
        first.Write("first");
        second.Write("second");

        // A failed recorded seal leaves drafts open; a successful one closes the attempt's recreated drafts.
        context.Recorded.Enqueue(new InvalidOperationException("recorded seal failure"));
        Assert.Equal("recorded seal failure", Assert.Throws<InvalidOperationException>(log.Seal).Message);
        Assert.True(first.IsOpen);
        Assert.True(second.IsOpen);

        context.Recorded.Enqueue(default(JournaledStateAccessNoResult));
        log.Seal();

        Assert.False(first.IsOpen);
        Assert.False(second.IsOpen);
        Assert.Equal(0, first.Count);
        Assert.Equal(0, second.Count);
        Assert.Empty(context.Accesses);
        Assert.Empty(context.Recorded);
    }

    [Fact]
    public async Task ReplayedCommitsCloseOnlyTheDraftsWhichAreStillOpen()
    {
        var sut = CreateTestSystem();
        var provider = new TestProvider();
        ((JournaledStateManager)sut.Manager).ContextProvider = provider;
        var log = new DurableLog<string>("log", sut.Manager, new OrleansBinaryDurableLogCommandCodec<string>(CodecProvider.GetCodec<string>(), SessionPool));
        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        log.Seal();

        // Another replaying execution's recorded seal closed the first draft before its recorded commit replays.
        var context = provider.Context = new RecordingContext();
        context.Recorded.Enqueue(default(JournaledStateAccessNoResult));
        using var closedBySeal = log.BeginDraft();
        context.Recorded.Enqueue(default(JournaledStateAccessNoResult));
        log.Seal();
        context.Recorded.Enqueue(0L);
        Assert.Equal(0, closedBySeal.Commit("a"));
        Assert.False(closedBySeal.IsOpen);

        // The recording run's commit failed because the live seal closed its draft, so the replayed failure closes the
        // recreated draft too.
        context.Recorded.Enqueue(default(JournaledStateAccessNoResult));
        using var failed = log.BeginDraft();
        context.Recorded.Enqueue(new InvalidOperationException("recorded commit failure"));
        Assert.Equal("recorded commit failure", Assert.Throws<InvalidOperationException>(() => failed.Commit("b")).Message);
        Assert.False(failed.IsOpen);
        Assert.Contains("was closed by the log", Assert.Throws<InvalidOperationException>(() => failed.Write("c")).Message, StringComparison.Ordinal);
        Assert.Empty(context.Accesses);
    }

    [Fact]
    public async Task DequeueWhoseResultTheContextRejectsLeavesTheItemQueued()
    {
        var sut = CreateTestSystem();
        var provider = new TestProvider();
        ((JournaledStateManager)sut.Manager).ContextProvider = provider;
        var queue = new DurableQueue<string>("queue", sut.Manager, new OrleansBinaryDurableQueueCommandCodec<string>(CodecProvider.GetCodec<string>(), SessionPool));
        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        queue.Enqueue("a");

        // A context which can't record the item, such as one without a codec for it, fails the dequeue before it applies.
        var context = provider.Context = new RecordingContext { Rejects = static _ => new NotSupportedException("no codec") };
        Assert.Throws<NotSupportedException>(() => queue.Dequeue());
        Assert.Throws<NotSupportedException>(() => queue.TryDequeue(out _));

        provider.Context = null;
        Assert.Equal(["a"], queue);
        Assert.Equal(
            [JournaledStateOperation.QueueDequeue, JournaledStateOperation.QueueTryDequeue],
            context.Accesses.Select(static access => access.Operation));

        // A context which records the item applies the dequeue after recording it.
        provider.Context = new RecordingContext();
        Assert.Equal("a", queue.Dequeue());
        provider.Context = null;
        Assert.Empty(queue);
    }

    [Fact]
    public async Task ReadsPassTheArgumentsWhichTheirResultsDependOn()
    {
        var sut = CreateTestSystem();
        var provider = new TestProvider();
        ((JournaledStateManager)sut.Manager).ContextProvider = provider;
        var (list, dictionary, value, queue, set, log, _) = CreateAllStates(sut.Manager);
        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        list.Add("a");
        dictionary["x"] = 1;
        log.Append("e");
        queue.Enqueue("q");
        set.Add("s");
        string[] other = ["a"];
        var context = provider.Context = new RecordingContext();

        _ = dictionary["x"];
        _ = dictionary.TryGetValue("x", out _);
        _ = dictionary.ContainsKey("y");
        _ = dictionary.Contains(new KeyValuePair<string, int>("x", 1));
        _ = list[0];
        _ = list.Contains("b");
        _ = list.IndexOf("a");
        _ = log[0];
        _ = log.TryGet(0, out _);
        _ = queue.Contains("q");
        _ = set.Contains("s");
        _ = set.IsSubsetOf(other);
        _ = list.Count;
        _ = value.Value;
        _ = dictionary.Keys;

        var none = default(JournaledStateAccessNoArgument);
        Assert.Equal(
            ["x", "x", "y", ("x", 1), 0, "b", "a", 0L, 0L, "q", "s", other, none, none, none],
            context.Arguments);
    }

    [Fact]
    public void TargetIdentifiersAreStableHashesOfTheirNames()
    {
        var manager = CreateTestSystem().Manager;
        Assert.Equal(new JournaledStateAccessTarget("stock", manager).Id, new JournaledStateAccessTarget("stock", manager).Id);
        Assert.NotEqual(new JournaledStateAccessTarget("stock", manager).Id, new JournaledStateAccessTarget("orders", manager).Id);

        // FNV-1a of "a", which every process computes alike.
        Assert.Equal(unchecked((int)0xE40C292Cu), new JournaledStateAccessTarget("a", manager).Id);
    }

    [Fact]
    public void ManagerRejectsAStateWhoseIdentifierAnotherStateHas()
    {
        var manager = CreateTestSystem().Manager;

        // Distinct names whose FNV-1a hashes are both 0xE256D750.
        var first = new JournaledStateAccessTarget("vmXuqzvn", manager);
        Assert.Equal(unchecked((int)0xE256D750u), first.Id);

        var exception = Assert.Throws<InvalidOperationException>(() => new DurableValue<int>(
            "prcxZS4u",
            manager,
            new OrleansBinaryDurableValueCommandCodec<int>(CodecProvider.GetCodec<int>(), SessionPool)));
        Assert.Contains("'vmXuqzvn' and 'prcxZS4u'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Rename", exception.Message, StringComparison.Ordinal);
        Assert.False(manager.TryGetStateMachine("prcxZS4u", out _));
    }

    private (DurableList<string> List, DurableDictionary<string, int> Dictionary, DurableValue<int> Value, DurableQueue<string> Queue, DurableSet<string> Set, DurableLog<string> Log, DurableTaskCompletionSource<int> Completion)
        CreateAllStates(IJournaledStateManager manager) =>
        (
            new DurableList<string>("list", manager, new OrleansBinaryDurableListCommandCodec<string>(CodecProvider.GetCodec<string>(), SessionPool)),
            new DurableDictionary<string, int>(
                "dictionary",
                manager,
                new OrleansBinaryDurableDictionaryCommandCodec<string, int>(CodecProvider.GetCodec<string>(), CodecProvider.GetCodec<int>(), SessionPool)),
            new DurableValue<int>("value", manager, new OrleansBinaryDurableValueCommandCodec<int>(CodecProvider.GetCodec<int>(), SessionPool)),
            new DurableQueue<string>("queue", manager, new OrleansBinaryDurableQueueCommandCodec<string>(CodecProvider.GetCodec<string>(), SessionPool)),
            new DurableSet<string>("set", manager, new OrleansBinaryDurableSetCommandCodec<string>(CodecProvider.GetCodec<string>(), SessionPool)),
            new DurableLog<string>("log", manager, new OrleansBinaryDurableLogCommandCodec<string>(CodecProvider.GetCodec<string>(), SessionPool)),
            new DurableTaskCompletionSource<int>(
                "completion",
                manager,
                new OrleansBinaryDurableTaskCompletionSourceCommandCodec<int>(CodecProvider.GetCodec<int>(), CodecProvider.GetCodec<Exception>(), SessionPool),
                ServiceProvider.GetRequiredService<DeepCopier<int>>(),
                ServiceProvider.GetRequiredService<DeepCopier<Exception>>()));

    private async Task<(DurableList<string> List, DurableDictionary<string, int> Dictionary, DurableValue<int> Value, TestProvider Provider)> CreateStatesAsync()
    {
        var sut = CreateTestSystem();
        var provider = new TestProvider();
        ((JournaledStateManager)sut.Manager).ContextProvider = provider;
        var list = new DurableList<string>("list", sut.Manager, new OrleansBinaryDurableListCommandCodec<string>(CodecProvider.GetCodec<string>(), SessionPool));
        var dictionary = new DurableDictionary<string, int>(
            "dictionary",
            sut.Manager,
            new OrleansBinaryDurableDictionaryCommandCodec<string, int>(CodecProvider.GetCodec<string>(), CodecProvider.GetCodec<int>(), SessionPool));
        var value = new DurableValue<int>("value", sut.Manager, new OrleansBinaryDurableValueCommandCodec<int>(CodecProvider.GetCodec<int>(), SessionPool));
        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        return (list, dictionary, value, provider);
    }

    /// <summary>Supplies the test's context, except to operations which run within one of its operations.</summary>
    private sealed class TestProvider : JournaledStateContextProvider
    {
        public RecordingContext? Context { get; set; }

        public override JournaledStateContext? Current => Context is { Depth: 0 } context ? context : null;
    }

    /// <summary>A context which records each operation's outcome and supplies queued outcomes, as a replay does.</summary>
    private sealed class RecordingContext : JournaledStateContext
    {
        public int Depth { get; private set; }

        public List<(JournaledStateAccessKind Kind, JournaledStateOperation Operation, string Target, object? Outcome)> Accesses { get; } = [];

        /// <summary>Gets the argument of each access which ran.</summary>
        public List<object?> Arguments { get; } = [];

        public Queue<object?> Recorded { get; } = new();

        /// <summary>Gets or sets a check which rejects a result before its change applies, as a context which can't record it does.</summary>
        public Func<object?, Exception?>? Rejects { get; set; }

        protected override TResult Access<TArgument, TState, TResult>(
            in JournaledStateAccess access,
            TArgument argument,
            TState state,
            Func<TState, TResult> operation,
            Action<TState, TResult>? apply)
        {
            if (Recorded.TryDequeue(out var outcome))
            {
                return outcome is Exception exception ? throw exception : (TResult)outcome!;
            }

            Depth++;
            try
            {
                Arguments.Add(argument);
                var result = operation(state);
                if (Rejects?.Invoke(result) is { } rejection)
                {
                    throw rejection;
                }

                apply?.Invoke(state, result);
                Accesses.Add((access.Kind, access.Operation, access.Target.Name, result));
                return result;
            }
            catch (Exception exception)
            {
                Accesses.Add((access.Kind, access.Operation, access.Target.Name, exception));
                throw;
            }
            finally
            {
                Depth--;
            }
        }
    }
}
