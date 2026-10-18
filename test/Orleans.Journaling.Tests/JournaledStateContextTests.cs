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
    public async Task BeginningADraftReadsWhetherTheLogIsSealedThroughTheContext()
    {
        var sut = CreateTestSystem();
        var provider = new TestProvider();
        ((JournaledStateManager)sut.Manager).ContextProvider = provider;
        var log = new DurableLog<string>("log", sut.Manager, new OrleansBinaryDurableLogCommandCodec<string>(CodecProvider.GetCodec<string>(), SessionPool));
        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        log.Seal();

        // A replay of a run which began the draft before the seal returns the recorded state, so the draft begins.
        var context = provider.Context = new RecordingContext();
        context.Recorded.Enqueue(false);
        _ = log.BeginDraft();

        Assert.Throws<InvalidOperationException>(() => log.BeginDraft());
        var access = Assert.Single(context.Accesses);
        Assert.Equal((JournaledStateAccessKind.Read, JournaledStateOperation.LogIsSealed, true), (access.Kind, access.Operation, access.Outcome));
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

        public Queue<object?> Recorded { get; } = new();

        protected override TResult Access<TState, TResult>(in JournaledStateAccess access, TState state, Func<TState, TResult> operation)
        {
            if (Recorded.TryDequeue(out var outcome))
            {
                return outcome is Exception exception ? throw exception : (TResult)outcome!;
            }

            Depth++;
            try
            {
                var result = operation(state);
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
