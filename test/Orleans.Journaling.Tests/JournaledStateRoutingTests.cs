using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;
using Xunit;

namespace Orleans.Journaling.Tests;

/// <summary>
/// Verifies that every public member of every journaled state type either runs through the current
/// <see cref="JournaledStateContext"/> exactly once or observes the state live, and that the members together exercise
/// every <see cref="JournaledStateOperation"/> which Journaling defines.
/// </summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
public sealed class JournaledStateRoutingTests : JournalingTestBase
{
    // Members which observe the state as it changes, or which describe the type rather than the state.
    private static readonly HashSet<string> LiveMembers =
    [
        "ICollection`1.get_IsReadOnly",
        "IDurableLog`1.ReadAsync(Int64,Int64,DurableWatchMode,Nullable`1,CancellationToken)",
        "IDurableTaskCompletionSource`1.get_Task",
    ];

    [Fact]
    public async Task EveryPublicMemberRoutesExactlyOnceOrIsLive()
    {
        var sut = CreateTestSystem();
        var provider = new Provider();
        ((JournaledStateManager)sut.Manager).ContextProvider = provider;
        var value = new DurableValue<int>("value", sut.Manager, new OrleansBinaryDurableValueCommandCodec<int>(CodecProvider.GetCodec<int>(), SessionPool));
        var dictionary = new DurableDictionary<int, int>(
            "dictionary",
            sut.Manager,
            new OrleansBinaryDurableDictionaryCommandCodec<int, int>(CodecProvider.GetCodec<int>(), CodecProvider.GetCodec<int>(), SessionPool));
        var list = new DurableList<int>("list", sut.Manager, new OrleansBinaryDurableListCommandCodec<int>(CodecProvider.GetCodec<int>(), SessionPool));
        var queue = new DurableQueue<int>("queue", sut.Manager, new OrleansBinaryDurableQueueCommandCodec<int>(CodecProvider.GetCodec<int>(), SessionPool));
        var set = new DurableSet<int>("set", sut.Manager, new OrleansBinaryDurableSetCommandCodec<int>(CodecProvider.GetCodec<int>(), SessionPool));
        var log = new DurableLog<int>("log", sut.Manager, new OrleansBinaryDurableLogCommandCodec<int>(CodecProvider.GetCodec<int>(), SessionPool));
        var completion = new DurableTaskCompletionSource<int>(
            "completion",
            sut.Manager,
            new OrleansBinaryDurableTaskCompletionSourceCommandCodec<int>(CodecProvider.GetCodec<int>(), CodecProvider.GetCodec<Exception>(), SessionPool),
            ServiceProvider.GetRequiredService<DeepCopier<int>>(),
            ServiceProvider.GetRequiredService<DeepCopier<Exception>>());
        var drafts = new DurableLog<int>("drafts", sut.Manager, new OrleansBinaryDurableLogCommandCodec<int>(CodecProvider.GetCodec<int>(), SessionPool));
        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        var context = provider.Context = new RecordingContext();
        int[] other = [1, 2];

        AssertRoutes<IDurableValue<int>>(value, context, new()
        {
            ["IDurableValue`1.get_Value"] = static state => _ = state.Value,
            ["IDurableValue`1.set_Value(Int32)"] = static state => state.Value = 1,
        });
        AssertRoutes<IDurableDictionary<int, int>>(dictionary, context, new()
        {
            ["IDictionary`2.get_Item(Int32)"] = static state => _ = state[1],
            ["IDictionary`2.set_Item(Int32,Int32)"] = static state => state[1] = 1,
            ["IDictionary`2.get_Keys"] = static state => _ = state.Keys,
            ["IDictionary`2.get_Values"] = static state => _ = state.Values,
            ["IDictionary`2.ContainsKey(Int32)"] = static state => state.ContainsKey(1),
            ["IDictionary`2.Add(Int32,Int32)"] = static state => state.Add(2, 2),
            ["IDictionary`2.Remove(Int32)"] = static state => state.Remove(2),
            ["IDictionary`2.TryGetValue(Int32,Int32&)"] = static state => state.TryGetValue(1, out _),
            ["ICollection`1.get_Count"] = static state => _ = state.Count,
            ["ICollection`1.get_IsReadOnly"] = static state => _ = state.IsReadOnly,
            ["ICollection`1.Add(KeyValuePair`2)"] = static state => state.Add(new KeyValuePair<int, int>(3, 3)),
            ["ICollection`1.Clear"] = static state => state.Clear(),
            ["ICollection`1.Contains(KeyValuePair`2)"] = static state => state.Contains(new KeyValuePair<int, int>(1, 1)),
            ["ICollection`1.CopyTo(KeyValuePair`2[],Int32)"] = static state => state.CopyTo(new KeyValuePair<int, int>[10], 0),
            ["ICollection`1.Remove(KeyValuePair`2)"] = static state => state.Remove(new KeyValuePair<int, int>(1, 1)),
            ["IEnumerable`1.GetEnumerator"] = static state => state.GetEnumerator().Dispose(),
            ["IEnumerable.GetEnumerator"] = static state => ((System.Collections.IEnumerable)state).GetEnumerator(),
        });
        AssertRoutes<IDurableList<int>>(list, context, new()
        {
            ["IList`1.get_Item(Int32)"] = static state => _ = state[0],
            ["IList`1.set_Item(Int32,Int32)"] = static state => state[0] = 1,
            ["IList`1.IndexOf(Int32)"] = static state => state.IndexOf(1),
            ["IList`1.Insert(Int32,Int32)"] = static state => state.Insert(0, 1),
            ["IList`1.RemoveAt(Int32)"] = static state => state.RemoveAt(0),
            ["ICollection`1.get_Count"] = static state => _ = state.Count,
            ["ICollection`1.get_IsReadOnly"] = static state => _ = state.IsReadOnly,
            ["ICollection`1.Add(Int32)"] = static state => state.Add(1),
            ["ICollection`1.Clear"] = static state => state.Clear(),
            ["ICollection`1.Contains(Int32)"] = static state => state.Contains(1),
            ["ICollection`1.CopyTo(Int32[],Int32)"] = static state => state.CopyTo(new int[10], 0),
            ["ICollection`1.Remove(Int32)"] = static state => state.Remove(1),
            ["IDurableList`1.AddRange(IEnumerable`1)"] = state => state.AddRange(other),
            ["IDurableList`1.AsReadOnly"] = static state => state.AsReadOnly(),
            ["IEnumerable`1.GetEnumerator"] = static state => state.GetEnumerator().Dispose(),
            ["IEnumerable.GetEnumerator"] = static state => ((System.Collections.IEnumerable)state).GetEnumerator(),
        });
        AssertRoutes<IDurableQueue<int>>(queue, context, new()
        {
            ["IDurableQueue`1.Clear"] = static state => state.Clear(),
            ["IDurableQueue`1.Contains(Int32)"] = static state => state.Contains(1),
            ["IDurableQueue`1.CopyTo(Int32[],Int32)"] = static state => state.CopyTo(new int[10], 0),
            ["IDurableQueue`1.Dequeue"] = static state => state.Dequeue(),
            ["IDurableQueue`1.Enqueue(Int32)"] = static state => state.Enqueue(1),
            ["IDurableQueue`1.Peek"] = static state => state.Peek(),
            ["IDurableQueue`1.TryDequeue(Int32&)"] = static state => state.TryDequeue(out _),
            ["IDurableQueue`1.TryPeek(Int32&)"] = static state => state.TryPeek(out _),
            ["IReadOnlyCollection`1.get_Count"] = static state => _ = state.Count,
            ["IEnumerable`1.GetEnumerator"] = static state => state.GetEnumerator().Dispose(),
            ["IEnumerable.GetEnumerator"] = static state => ((System.Collections.IEnumerable)state).GetEnumerator(),
        });
        AssertRoutes<IDurableSet<int>>(set, context, new()
        {
            ["IDurableSet`1.get_Count"] = static state => _ = state.Count,
            ["IDurableSet`1.Add(Int32)"] = static state => state.Add(1),
            ["IDurableSet`1.Contains(Int32)"] = static state => state.Contains(1),
            ["IDurableSet`1.IsProperSubsetOf(IEnumerable`1)"] = state => state.IsProperSubsetOf(other),
            ["IDurableSet`1.IsProperSupersetOf(IEnumerable`1)"] = state => state.IsProperSupersetOf(other),
            ["IDurableSet`1.IsSubsetOf(IEnumerable`1)"] = state => state.IsSubsetOf(other),
            ["IDurableSet`1.IsSupersetOf(IEnumerable`1)"] = state => state.IsSupersetOf(other),
            ["IDurableSet`1.Overlaps(IEnumerable`1)"] = state => state.Overlaps(other),
            ["IDurableSet`1.SetEquals(IEnumerable`1)"] = state => state.SetEquals(other),
            ["ISet`1.Add(Int32)"] = static state => ((ISet<int>)state).Add(1),
            ["ISet`1.ExceptWith(IEnumerable`1)"] = state => state.ExceptWith(other),
            ["ISet`1.IntersectWith(IEnumerable`1)"] = state => state.IntersectWith(other),
            ["ISet`1.IsProperSubsetOf(IEnumerable`1)"] = state => ((ISet<int>)state).IsProperSubsetOf(other),
            ["ISet`1.IsProperSupersetOf(IEnumerable`1)"] = state => ((ISet<int>)state).IsProperSupersetOf(other),
            ["ISet`1.IsSubsetOf(IEnumerable`1)"] = state => ((ISet<int>)state).IsSubsetOf(other),
            ["ISet`1.IsSupersetOf(IEnumerable`1)"] = state => ((ISet<int>)state).IsSupersetOf(other),
            ["ISet`1.Overlaps(IEnumerable`1)"] = state => ((ISet<int>)state).Overlaps(other),
            ["ISet`1.SetEquals(IEnumerable`1)"] = state => ((ISet<int>)state).SetEquals(other),
            ["ISet`1.SymmetricExceptWith(IEnumerable`1)"] = state => state.SymmetricExceptWith(other),
            ["ISet`1.UnionWith(IEnumerable`1)"] = state => state.UnionWith(other),
            ["IReadOnlySet`1.Contains(Int32)"] = static state => ((IReadOnlySet<int>)state).Contains(1),
            ["IReadOnlySet`1.IsProperSubsetOf(IEnumerable`1)"] = state => ((IReadOnlySet<int>)state).IsProperSubsetOf(other),
            ["IReadOnlySet`1.IsProperSupersetOf(IEnumerable`1)"] = state => ((IReadOnlySet<int>)state).IsProperSupersetOf(other),
            ["IReadOnlySet`1.IsSubsetOf(IEnumerable`1)"] = state => ((IReadOnlySet<int>)state).IsSubsetOf(other),
            ["IReadOnlySet`1.IsSupersetOf(IEnumerable`1)"] = state => ((IReadOnlySet<int>)state).IsSupersetOf(other),
            ["IReadOnlySet`1.Overlaps(IEnumerable`1)"] = state => ((IReadOnlySet<int>)state).Overlaps(other),
            ["IReadOnlySet`1.SetEquals(IEnumerable`1)"] = state => ((IReadOnlySet<int>)state).SetEquals(other),
            ["IReadOnlyCollection`1.get_Count"] = static state => _ = ((IReadOnlyCollection<int>)state).Count,
            ["ICollection`1.get_Count"] = static state => _ = ((ICollection<int>)state).Count,
            ["ICollection`1.get_IsReadOnly"] = static state => _ = state.IsReadOnly,
            ["ICollection`1.Add(Int32)"] = static state => ((ICollection<int>)state).Add(1),
            ["ICollection`1.Clear"] = static state => state.Clear(),
            ["ICollection`1.Contains(Int32)"] = static state => ((ICollection<int>)state).Contains(1),
            ["ICollection`1.CopyTo(Int32[],Int32)"] = static state => state.CopyTo(new int[10], 0),
            ["ICollection`1.Remove(Int32)"] = static state => state.Remove(1),
            ["IEnumerable`1.GetEnumerator"] = static state => state.GetEnumerator().Dispose(),
            ["IEnumerable.GetEnumerator"] = static state => ((System.Collections.IEnumerable)state).GetEnumerator(),
        });
        AssertRoutes<IDurableLog<int>>(log, context, new()
        {
            ["IReadOnlyCollection`1.get_Count"] = static state => _ = state.Count,
            ["IDurableLog`1.get_Epoch"] = static state => _ = state.Epoch,
            ["IDurableLog`1.get_FirstSequence"] = static state => _ = state.FirstSequence,
            ["IDurableLog`1.get_NextSequence"] = static state => _ = state.NextSequence,
            ["IDurableLog`1.get_CommittedSequence"] = static state => _ = state.CommittedSequence,
            ["IDurableLog`1.get_IsSealed"] = static state => _ = state.IsSealed,
            ["IDurableLog`1.get_Item(Int64)"] = static state => _ = state[0],
            ["IDurableLog`1.TryGet(Int64,Int32&)"] = static state => state.TryGet(0, out _),
            ["IDurableLog`1.Append(Int32)"] = static state => state.Append(1),
            ["IDurableLog`1.TrimBefore(Int64)"] = static state => state.TrimBefore(0),
            ["IDurableLog`1.Clear"] = static state => state.Clear(),
            ["IDurableLog`1.BeginDraft"] = static state => state.BeginDraft().Dispose(),
            ["IDurableLog`1.ReadAsync(Int64,Int64,DurableWatchMode,Nullable`1,CancellationToken)"] = static state => _ = state.ReadAsync(),
            ["IDurableLog`1.Seal"] = static state => state.Seal(),
            ["IEnumerable`1.GetEnumerator"] = static state => state.GetEnumerator().Dispose(),
            ["IEnumerable.GetEnumerator"] = static state => ((System.Collections.IEnumerable)state).GetEnumerator(),
        });
        AssertRoutes<IDurableTaskCompletionSource<int>>(completion, context, new()
        {
            ["IDurableTaskCompletionSource`1.get_Task"] = static state => _ = state.Task,
            ["IDurableTaskCompletionSource`1.get_State"] = static state => _ = state.State,
            ["IDurableTaskCompletionSource`1.TrySetResult(Int32)"] = static state => state.TrySetResult(1),
            ["IDurableTaskCompletionSource`1.TrySetException(Exception)"] = static state => state.TrySetException(new InvalidOperationException()),
            ["IDurableTaskCompletionSource`1.TrySetCanceled"] = static state => state.TrySetCanceled(),
        });

        // A draft's commit is the log's recorded change; its items stream live.
        provider.Context = null;
        using var draft = drafts.BeginDraft();
        provider.Context = context;
        var before = context.Accesses.Count;
        draft.Write(1);
        Assert.Equal(before, context.Accesses.Count);
        draft.Commit(1);
        Assert.Equal(JournaledStateOperation.LogCommitDraft, Assert.Single(context.Accesses.Skip(before)).Operation);

        // Messaging's outbox sends are recorded by the Messaging tests.
        Assert.Equal(
            Enum.GetValues<JournaledStateOperation>().Where(static operation => operation is not (JournaledStateOperation.OutboxSend or JournaledStateOperation.OutboxWithdraw)).Order(),
            context.Accesses.Select(static access => access.Operation).Distinct().Order());
    }

    private static void AssertRoutes<T>(T state, RecordingContext context, Dictionary<string, Action<T>> members)
    {
        var reflected = typeof(T).GetInterfaces().Append(typeof(T)).SelectMany(static type => type.GetMethods()).Select(Signature).Order(StringComparer.Ordinal);
        var missing = reflected.Except(members.Keys).ToList();
        var extra = members.Keys.Except(reflected).ToList();
        Assert.True(missing.Count == 0 && extra.Count == 0, $"{typeof(T).Name}: members without an invocation: {string.Join("; ", missing)}. Invocations of no member: {string.Join("; ", extra)}.");
        foreach (var (name, invoke) in members)
        {
            var before = context.Accesses.Count;
            try
            {
                invoke(state);
            }
            catch (Exception exception) when (exception is not Xunit.Sdk.XunitException)
            {
                // Outcomes don't matter here: an access which throws runs through the context as one which returns does.
            }

            Assert.True(
                context.Accesses.Count - before == (LiveMembers.Contains(name) ? 0 : 1),
                $"{typeof(T).Name}: '{name}' made {context.Accesses.Count - before} accesses through the context.");
        }
    }

    private static string Signature(MethodInfo method)
    {
        var parameters = method.GetParameters();
        var name = $"{method.DeclaringType!.Name}.{method.Name}";
        return parameters.Length == 0 ? name : $"{name}({string.Join(",", parameters.Select(static parameter => parameter.ParameterType.Name))})";
    }

    private sealed class Provider : JournaledStateContextProvider
    {
        public RecordingContext? Context { get; set; }

        public override JournaledStateContext? Current => Context is { Depth: 0 } context ? context : null;
    }

    /// <summary>A context which runs each access and records its operation.</summary>
    private sealed class RecordingContext : JournaledStateContext
    {
        public int Depth { get; private set; }

        public List<JournaledStateAccess> Accesses { get; } = [];

        protected override TResult Access<TArgument, TState, TResult>(
            in JournaledStateAccess access,
            TArgument argument,
            TState state,
            Func<TState, TResult> operation,
            Action<TState, TResult>? apply)
        {
            Accesses.Add(access);
            Depth++;
            try
            {
                var result = operation(state);
                apply?.Invoke(state, result);
                return result;
            }
            finally
            {
                Depth--;
            }
        }
    }
}
