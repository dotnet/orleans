using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Serialization;
using Xunit;
using static Orleans.Journaling.Tests.ObservationTestHelpers;

namespace Orleans.Journaling.Tests;

/// <summary>
/// Tests the fence which stops code within a scope from changing journaled state once the scope's token is canceled.
/// </summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
public class JournaledStateMutationFenceTests : JournalingTestBase
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public Task ClosedFence_EveryStateChangeThrowsBeforeChangingMemory() => RunOnActivationSchedulerAsync(async () =>
    {
        var storage = new VolatileJournalStorage();
        var (sut, states) = await CreateStatesAsync(storage);
        states.Seed();
        await sut.Manager.WriteStateAsync(CancellationToken);

        var fence = CreateFence();
        using (JournaledStateMutationFence.Enter(fence))
        {
            fence.Close();
            var state = (IPersistentState<string>)states.State;
            AssertFenced(() => states.Value.Value = 2);
            AssertFenced(() => states.Dictionary["b"] = 2);
            AssertFenced(() => states.Dictionary.Add("c", 3));
            AssertFenced(() => states.Dictionary.Remove("a"));
            AssertFenced(states.Dictionary.Clear);
            AssertFenced(() => states.List.Add("b"));
            AssertFenced(() => states.List[0] = "c");
            AssertFenced(() => states.List.Insert(0, "c"));
            AssertFenced(() => states.List.RemoveAt(0));
            AssertFenced(states.List.Clear);
            AssertFenced(() => states.Queue.Enqueue("b"));
            AssertFenced(() => states.Queue.Dequeue());
            AssertFenced(() => states.Queue.TryDequeue(out _));
            AssertFenced(states.Queue.Clear);
            AssertFenced(() => states.Set.Add("b"));
            AssertFenced(() => states.Set.Remove("a"));
            AssertFenced(states.Set.Clear);
            AssertFenced(() => states.Log.Append("b"));
            AssertFenced(() => states.Log.TrimBefore(1));
            AssertFenced(states.Log.Clear);
            AssertFenced(states.Log.Seal);
            AssertFenced(() => states.Completion.TrySetResult(1));
            AssertFenced(() => states.Completion.TrySetException(new InvalidOperationException()));
            AssertFenced(() => states.Completion.TrySetCanceled());
            AssertFenced(() => state.State = "b");
            await AssertFencedAsync(() => state.WriteStateAsync());
            await AssertFencedAsync(() => state.ClearStateAsync());
            await AssertFencedAsync(() => sut.Manager.WriteStateAsync(CancellationToken).AsTask());

            // Reading and no-op operations don't change state.
            Assert.Equal(1, states.Value.Value);
            Assert.False(states.Dictionary.Remove("missing"));
            Assert.Equal("a", state.State);
        }

        states.AssertSeeded();
        Assert.Equal(DurableTaskCompletionSourceStatus.Pending, states.Completion.State.Status);

        // Code outside the fence commits; the fenced code's attempts left nothing to commit.
        states.Value.Value = 3;
        await sut.Manager.WriteStateAsync(CancellationToken);
        var (_, recovered) = await CreateStatesAsync(storage);
        Assert.Equal(3, recovered.Value.Value);
        recovered.AssertSeeded(value: 3);
    });

    [Fact]
    public Task AbsentPersistentStateIsNotCreatedByReadingWithinClosedFence() => RunOnActivationSchedulerAsync(async () =>
    {
        var (_, states) = await CreateStatesAsync(new VolatileJournalStorage());
        var fence = CreateFence();
        using (JournaledStateMutationFence.Enter(fence))
        {
            fence.Close();
            AssertFenced(() => _ = ((IPersistentState<string>)states.State).State);
        }

        Assert.False(((IPersistentState<string>)states.State).RecordExists);
    });

    [Fact]
    public Task OpenFence_StateChangesCommit() => RunOnActivationSchedulerAsync(async () =>
    {
        var storage = new VolatileJournalStorage();
        var (sut, states) = await CreateStatesAsync(storage);
        var fence = CreateFence();
        using (JournaledStateMutationFence.Enter(fence))
        {
            states.Seed();
            await sut.Manager.WriteStateAsync(CancellationToken);
        }

        var (_, recovered) = await CreateStatesAsync(storage);
        recovered.AssertSeeded();
    });

    [Fact]
    public Task Fence_FollowsAsynchronousFlowAndAppliesOnceClosed() => RunOnActivationSchedulerAsync(async () =>
    {
        var (_, states) = await CreateStatesAsync(new VolatileJournalStorage());
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task fenced;
        var fence = CreateFence();
        using (JournaledStateMutationFence.Enter(fence))
        {
            fenced = ChangeAfterAsync(resume.Task, states.Value);
        }

        // The fence applies to the flow which entered it, and not to this one after its scope ended.
        fence.Close();
        states.Value.Value = 5;
        resume.SetResult();
        Assert.Same(FenceException, await Assert.ThrowsAsync<OperationCanceledException>(() => fenced));
        Assert.Equal(5, states.Value.Value);

        static async Task ChangeAfterAsync(Task resume, IDurableValue<int> value)
        {
            await resume;
            value.Value = 4;
        }
    });

    [Fact]
    public Task Suspend_LiftsTheFenceForItsScope() => RunOnActivationSchedulerAsync(async () =>
    {
        var (_, states) = await CreateStatesAsync(new VolatileJournalStorage());
        var fence = CreateFence();
        using (JournaledStateMutationFence.Enter(fence))
        {
            fence.Close();
            using (JournaledStateMutationFence.Suspend())
            {
                states.Value.Value = 6;
            }

            AssertFenced(() => states.Value.Value = 7);
        }

        Assert.Equal(6, states.Value.Value);
    });

    private static readonly OperationCanceledException FenceException = new("The fenced scope ended.");

    private static JournaledStateMutationFence CreateFence() => new(static state => (Exception)state!, FenceException);

    private static void AssertFenced(Action change) => Assert.Same(FenceException, Assert.Throws<OperationCanceledException>(change));

    private static async Task AssertFencedAsync(Func<Task> change) =>
        Assert.Same(FenceException, await Assert.ThrowsAsync<OperationCanceledException>(change));

    private async Task<((IJournaledStateManager Manager, IJournalStorage Storage, ILifecycleSubject Lifecycle) System, States States)> CreateStatesAsync(
        IJournalStorage storage)
    {
        var sut = CreateTestSystem(storage);
        var manager = sut.Manager;
        var states = new States(
            new DurableValue<int>("value", manager, new OrleansBinaryDurableValueCommandCodec<int>(Codec<int>(), SessionPool)),
            new DurableDictionary<string, int>("dictionary", manager, new OrleansBinaryDurableDictionaryCommandCodec<string, int>(Codec<string>(), Codec<int>(), SessionPool)),
            new DurableList<string>("list", manager, new OrleansBinaryDurableListCommandCodec<string>(Codec<string>(), SessionPool)),
            new DurableQueue<string>("queue", manager, new OrleansBinaryDurableQueueCommandCodec<string>(Codec<string>(), SessionPool)),
            new DurableSet<string>("set", manager, new OrleansBinaryDurableSetCommandCodec<string>(Codec<string>(), SessionPool)),
            new DurableLog<string>("log", manager, new OrleansBinaryDurableLogCommandCodec<string>(Codec<string>(), SessionPool)),
            new DurableTaskCompletionSource<int>(
                "completion",
                manager,
                new OrleansBinaryDurableTaskCompletionSourceCommandCodec<int>(Codec<int>(), Codec<Exception>(), SessionPool),
                ServiceProvider.GetRequiredService<DeepCopier<int>>(),
                ServiceProvider.GetRequiredService<DeepCopier<Exception>>()),
            new JournaledPersistentState<string>("state", manager, new OrleansBinaryPersistentStateCommandCodec<string>(Codec<string>(), SessionPool), CodecProvider.GetActivator<string>()));
        await sut.Lifecycle.OnStart(CancellationToken);
        return (sut, states);
    }

    private Orleans.Serialization.Codecs.IFieldCodec<T> Codec<T>() => CodecProvider.GetCodec<T>();

    private sealed record States(
        DurableValue<int> Value,
        DurableDictionary<string, int> Dictionary,
        DurableList<string> List,
        DurableQueue<string> Queue,
        DurableSet<string> Set,
        DurableLog<string> Log,
        DurableTaskCompletionSource<int> Completion,
        JournaledPersistentState<string> State)
    {
        public void Seed()
        {
            Value.Value = 1;
            Dictionary["a"] = 1;
            List.Add("a");
            Queue.Enqueue("a");
            Set.Add("a");
            Log.Append("a");
            ((IPersistentState<string>)State).State = "a";
        }

        public void AssertSeeded(int value = 1)
        {
            Assert.Equal(value, Value.Value);
            Assert.Equal(1, Assert.Single(Dictionary, item => item.Key == "a").Value);
            Assert.Equal(["a"], List);
            Assert.Equal(["a"], Queue);
            Assert.Equal(["a"], Set);
            Assert.Equal(["a"], Log);
            Assert.Equal("a", ((IPersistentState<string>)State).State);
        }
    }
}
