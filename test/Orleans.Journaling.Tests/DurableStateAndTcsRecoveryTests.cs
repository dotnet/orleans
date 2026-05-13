using System.Buffers;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Core;
using Orleans.Serialization;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Session;
using Xunit;

namespace Orleans.Journaling.Tests;

[TestCategory("BVT")]
public sealed class DurableStateAndTcsRecoveryTests : JournalingTestBase
{
    [Fact]
    public async Task OrleansBinaryCodec_StateAndTcs_WriteAndRecover()
    {
        var sut = CreateTestSystem();
        var state = new DurableState<string>("state", sut.Manager, new OrleansBinaryPersistentStateCommandCodec<string>(ValueCodec<string>(), SessionPool));
        var tcs = new DurableTaskCompletionSource<int>(
            "tcs",
            sut.Manager,
            new OrleansBinaryDurableTaskCompletionSourceCommandCodec<int>(ValueCodec<int>(), ValueCodec<Exception>(), SessionPool),
            Copier<int>(),
            Copier<Exception>());
        await sut.Lifecycle.OnStart();

        ((IStorage<string>)state).State = "state-value";
        Assert.True(tcs.TrySetResult(17));
        await sut.Manager.WriteStateAsync(CancellationToken.None);

        var sut2 = CreateTestSystem(storage: sut.Storage);
        var state2 = new DurableState<string>("state", sut2.Manager, new OrleansBinaryPersistentStateCommandCodec<string>(ValueCodec<string>(), SessionPool));
        var tcs2 = new DurableTaskCompletionSource<int>(
            "tcs",
            sut2.Manager,
            new OrleansBinaryDurableTaskCompletionSourceCommandCodec<int>(ValueCodec<int>(), ValueCodec<Exception>(), SessionPool),
            Copier<int>(),
            Copier<Exception>());
        await sut2.Lifecycle.OnStart();

        var recoveredState = (IStorage<string>)state2;
        Assert.True(recoveredState.RecordExists);
        Assert.Equal("1", recoveredState.Etag);
        Assert.Equal("state-value", recoveredState.State);
        Assert.Equal(DurableTaskCompletionSourceStatus.Completed, tcs2.State.Status);
        Assert.Equal(17, tcs2.State.Value);
        Assert.Equal(17, await tcs2.Task);
    }

    [Fact]
    public async Task OrleansBinaryCodec_StateClear_WritesClearAndRecoversNoRecord()
    {
        var storage = new VolatileJournalStorage();
        var codec = new TrackingPersistentStateCommandCodec<string>(ValueCodec<string>(), SessionPool);
        var sut = CreateTestSystem(storage: storage);
        var state = new DurableState<string>("state", sut.Manager, codec);
        var grainState = (IStorage<string>)state;
        await sut.Lifecycle.OnStart();

        grainState.State = "state-value";
        await grainState.WriteStateAsync(CancellationToken.None);
        await grainState.ClearStateAsync(CancellationToken.None);

        Assert.Equal(1, codec.WriteClearCount);
        Assert.False(grainState.RecordExists);
        Assert.Equal("0", grainState.Etag);

        var recovered = CreateTestSystem(storage: storage);
        var recoveredState = new DurableState<string>("state", recovered.Manager, new OrleansBinaryPersistentStateCommandCodec<string>(ValueCodec<string>(), SessionPool));
        await recovered.Lifecycle.OnStart();

        var recoveredStorage = (IStorage<string>)recoveredState;
        Assert.False(recoveredStorage.RecordExists);
        Assert.Equal("0", recoveredStorage.Etag);
    }

    [Fact]
    public async Task DurableTaskCompletionSource_DeleteState_ResetsToPending()
    {
        var sut = CreateTestSystem();
        var tcs = new DurableTaskCompletionSource<int>(
            "tcs",
            sut.Manager,
            new OrleansBinaryDurableTaskCompletionSourceCommandCodec<int>(ValueCodec<int>(), ValueCodec<Exception>(), SessionPool),
            Copier<int>(),
            Copier<Exception>());
        await sut.Lifecycle.OnStart();
        Assert.True(tcs.TrySetResult(17));
        await sut.Manager.WriteStateAsync(CancellationToken.None);
        Assert.Equal(17, await tcs.Task);

        await sut.Manager.DeleteStateAsync(CancellationToken.None);

        Assert.Equal(DurableTaskCompletionSourceStatus.Pending, tcs.State.Status);
        Assert.False(tcs.Task.IsCompleted);
        Assert.True(tcs.TrySetResult(18));
    }

    private IFieldCodec<T> ValueCodec<T>() => CodecProvider.GetCodec<T>();

    private DeepCopier<T> Copier<T>() => ServiceProvider.GetRequiredService<DeepCopier<T>>();

    private sealed class TrackingPersistentStateCommandCodec<T>(IFieldCodec<T> valueCodec, SerializerSessionPool sessionPool) : IPersistentStateCommandCodec<T>
    {
        private readonly OrleansBinaryPersistentStateCommandCodec<T> _inner = new(valueCodec, sessionPool);

        public int WriteClearCount { get; private set; }

        public void WriteSet(T state, ulong version, JournalStreamWriter writer) => _inner.WriteSet(state, version, writer);

        public void WriteClear(JournalStreamWriter writer)
        {
            WriteClearCount++;
            _inner.WriteClear(writer);
        }

        public void Apply(JournalBufferReader input, IPersistentStateCommandHandler<T> consumer) => _inner.Apply(input, consumer);
    }
}
