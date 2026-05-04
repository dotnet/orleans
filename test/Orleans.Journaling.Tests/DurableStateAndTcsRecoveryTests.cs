using System.Buffers;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Core;
using Orleans.Serialization;
using Xunit;

namespace Orleans.Journaling.Tests;

[TestCategory("BVT")]
public sealed class DurableStateAndTcsRecoveryTests : JournalingTestBase
{
    [Fact]
    public async Task OrleansBinaryCodec_StateAndTcs_WriteAndRecover()
    {
        var sut = CreateTestSystem();
        var state = new DurableState<string>("state", sut.Manager, new OrleansBinaryStateOperationCodec<string>(ValueCodec<string>()));
        var tcs = new DurableTaskCompletionSource<int>(
            "tcs",
            sut.Manager,
            new OrleansBinaryTcsOperationCodec<int>(ValueCodec<int>(), ValueCodec<Exception>()),
            Copier<int>(),
            Copier<Exception>());
        await sut.Lifecycle.OnStart();

        ((IStorage<string>)state).State = "state-value";
        Assert.True(tcs.TrySetResult(17));
        await sut.Manager.WriteStateAsync(CancellationToken.None);

        var sut2 = CreateTestSystem(storage: sut.Storage);
        var state2 = new DurableState<string>("state", sut2.Manager, new OrleansBinaryStateOperationCodec<string>(ValueCodec<string>()));
        var tcs2 = new DurableTaskCompletionSource<int>(
            "tcs",
            sut2.Manager,
            new OrleansBinaryTcsOperationCodec<int>(ValueCodec<int>(), ValueCodec<Exception>()),
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
        var storage = new VolatileLogStorage();
        var codec = new TrackingStateOperationCodec<string>(ValueCodec<string>());
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
        var recoveredState = new DurableState<string>("state", recovered.Manager, new OrleansBinaryStateOperationCodec<string>(ValueCodec<string>()));
        await recovered.Lifecycle.OnStart();

        var recoveredStorage = (IStorage<string>)recoveredState;
        Assert.False(recoveredStorage.RecordExists);
        Assert.Equal("0", recoveredStorage.Etag);
    }

    private ILogValueCodec<T> ValueCodec<T>() => new OrleansLogValueCodec<T>(CodecProvider.GetCodec<T>(), SessionPool);

    private DeepCopier<T> Copier<T>() => ServiceProvider.GetRequiredService<DeepCopier<T>>();

    private sealed class TrackingStateOperationCodec<T>(ILogValueCodec<T> valueCodec) : IDurableStateOperationCodec<T>
    {
        private readonly OrleansBinaryStateOperationCodec<T> _inner = new(valueCodec);

        public int WriteClearCount { get; private set; }

        public void WriteSet(T state, ulong version, LogStreamWriter writer) => _inner.WriteSet(state, version, writer);

        public void WriteClear(LogStreamWriter writer)
        {
            WriteClearCount++;
            _inner.WriteClear(writer);
        }

        public void Apply(ReadOnlySequence<byte> input, IDurableStateOperationHandler<T> consumer) => _inner.Apply(input, consumer);
    }
}
