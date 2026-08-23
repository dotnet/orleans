using System.Buffers;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Core;
using Orleans.Serialization;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Session;
using Xunit;

namespace Orleans.Journaling.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
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
        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);

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
        await sut2.Lifecycle.OnStart(TestContext.Current.CancellationToken);

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
        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);

        grainState.State = "state-value";
        await grainState.WriteStateAsync(CancellationToken.None);
        await grainState.ClearStateAsync(CancellationToken.None);

        Assert.Equal(1, codec.WriteClearCount);
        Assert.False(grainState.RecordExists);
        Assert.Equal("0", grainState.Etag);

        var recovered = CreateTestSystem(storage: storage);
        var recoveredState = new DurableState<string>("state", recovered.Manager, new OrleansBinaryPersistentStateCommandCodec<string>(ValueCodec<string>(), SessionPool));
        await recovered.Lifecycle.OnStart(TestContext.Current.CancellationToken);

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
        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        Assert.True(tcs.TrySetResult(17));
        await sut.Manager.WriteStateAsync(CancellationToken.None);
        Assert.Equal(17, await tcs.Task);

        await sut.Manager.DeleteStateAsync(CancellationToken.None);

        Assert.Equal(DurableTaskCompletionSourceStatus.Pending, tcs.State.Status);
        Assert.False(tcs.Task.IsCompleted);
        Assert.True(tcs.TrySetResult(18));
    }

    [Fact]
    public async Task DurableState_SetRetry_ReusesStagedCommand()
    {
        var storage = new RetryCapturingStorage();
        var codec = new TrackingPersistentStateCommandCodec<string>(ValueCodec<string>(), SessionPool);
        var sut = CreateTestSystem(storage: storage);
        var state = new DurableState<string>("state", sut.Manager, codec);
        var grainState = (IStorage<string>)state;
        await sut.Lifecycle.OnStart();

        grainState.State = "state-value";
        storage.FailNextAppend();
        await Assert.ThrowsAsync<IOException>(() => grainState.WriteStateAsync(CancellationToken.None));
        var firstAttempt = Assert.Single(storage.AppendAttempts);

        await sut.Manager.WriteStateAsync(CancellationToken.None);

        Assert.Equal(1, codec.WriteSetCount);
        Assert.Equal(2, storage.AppendAttempts.Count);
        Assert.Equal(firstAttempt, storage.AppendAttempts[1]);

        var recovered = CreateTestSystem(storage: storage);
        var recoveredState = new DurableState<string>(
            "state",
            recovered.Manager,
            new OrleansBinaryPersistentStateCommandCodec<string>(ValueCodec<string>(), SessionPool));
        await recovered.Lifecycle.OnStart();
        Assert.Equal("state-value", ((IStorage<string>)recoveredState).State);
    }

    [Fact]
    public async Task DurableState_ClearRetry_ReusesStagedCommand()
    {
        var storage = new RetryCapturingStorage();
        var codec = new TrackingPersistentStateCommandCodec<string>(ValueCodec<string>(), SessionPool);
        var sut = CreateTestSystem(storage: storage);
        var state = new DurableState<string>("state", sut.Manager, codec);
        var grainState = (IStorage<string>)state;
        await sut.Lifecycle.OnStart();
        grainState.State = "state-value";
        await grainState.WriteStateAsync(CancellationToken.None);
        storage.ClearAttempts();

        storage.FailNextAppend();
        await Assert.ThrowsAsync<IOException>(() => grainState.ClearStateAsync(CancellationToken.None));
        var firstAttempt = Assert.Single(storage.AppendAttempts);

        await sut.Manager.WriteStateAsync(CancellationToken.None);

        Assert.Equal(1, codec.WriteClearCount);
        Assert.Equal(2, storage.AppendAttempts.Count);
        Assert.Equal(firstAttempt, storage.AppendAttempts[1]);

        var recovered = CreateTestSystem(storage: storage);
        var recoveredState = new DurableState<string>(
            "state",
            recovered.Manager,
            new OrleansBinaryPersistentStateCommandCodec<string>(ValueCodec<string>(), SessionPool));
        await recovered.Lifecycle.OnStart();
        Assert.False(((IStorage)recoveredState).RecordExists);
    }

    [Fact]
    public async Task DurableTaskCompletionSource_Retry_ReusesStagedCommand()
    {
        var storage = new RetryCapturingStorage();
        var codec = new TrackingTaskCompletionSourceCommandCodec<int>(
            ValueCodec<int>(),
            ValueCodec<Exception>(),
            SessionPool);
        var sut = CreateTestSystem(storage: storage);
        var tcs = new DurableTaskCompletionSource<int>(
            "tcs",
            sut.Manager,
            codec,
            Copier<int>(),
            Copier<Exception>());
        await sut.Lifecycle.OnStart();

        Assert.True(tcs.TrySetResult(17));
        storage.FailNextAppend();
        await Assert.ThrowsAsync<IOException>(() => sut.Manager.WriteStateAsync(CancellationToken.None).AsTask());
        var firstAttempt = Assert.Single(storage.AppendAttempts);

        await sut.Manager.WriteStateAsync(CancellationToken.None);

        Assert.Equal(1, codec.WriteCompletedCount);
        Assert.Equal(2, storage.AppendAttempts.Count);
        Assert.Equal(firstAttempt, storage.AppendAttempts[1]);
        Assert.Equal(17, await tcs.Task);

        var recovered = CreateTestSystem(storage: storage);
        var recoveredTcs = new DurableTaskCompletionSource<int>(
            "tcs",
            recovered.Manager,
            new OrleansBinaryDurableTaskCompletionSourceCommandCodec<int>(
                ValueCodec<int>(),
                ValueCodec<Exception>(),
                SessionPool),
            Copier<int>(),
            Copier<Exception>());
        await recovered.Lifecycle.OnStart();
        Assert.Equal(17, await recoveredTcs.Task);
    }

    private IFieldCodec<T> ValueCodec<T>() => CodecProvider.GetCodec<T>();

    private DeepCopier<T> Copier<T>() => ServiceProvider.GetRequiredService<DeepCopier<T>>();

    private sealed class TrackingPersistentStateCommandCodec<T>(IFieldCodec<T> valueCodec, SerializerSessionPool sessionPool) : IPersistentStateCommandCodec<T>
    {
        private readonly OrleansBinaryPersistentStateCommandCodec<T> _inner = new(valueCodec, sessionPool);

        public int WriteClearCount { get; private set; }

        public int WriteSetCount { get; private set; }

        public void WriteSet(T state, ulong version, JournalStreamWriter writer)
        {
            WriteSetCount++;
            _inner.WriteSet(state, version, writer);
        }

        public void WriteClear(JournalStreamWriter writer)
        {
            WriteClearCount++;
            _inner.WriteClear(writer);
        }

        public void Apply(JournalBufferReader input, IPersistentStateCommandHandler<T> consumer) => _inner.Apply(input, consumer);
    }

    private sealed class TrackingTaskCompletionSourceCommandCodec<T>(
        IFieldCodec<T> valueCodec,
        IFieldCodec<Exception> exceptionCodec,
        SerializerSessionPool sessionPool) : IDurableTaskCompletionSourceCommandCodec<T>
    {
        private readonly OrleansBinaryDurableTaskCompletionSourceCommandCodec<T> _inner = new(valueCodec, exceptionCodec, sessionPool);

        public int WriteCompletedCount { get; private set; }

        public void Apply(JournalBufferReader input, IDurableTaskCompletionSourceCommandHandler<T> consumer) => _inner.Apply(input, consumer);

        public void WritePending(JournalStreamWriter writer) => _inner.WritePending(writer);

        public void WriteCompleted(T value, JournalStreamWriter writer)
        {
            WriteCompletedCount++;
            _inner.WriteCompleted(value, writer);
        }

        public void WriteFaulted(Exception exception, JournalStreamWriter writer) => _inner.WriteFaulted(exception, writer);

        public void WriteCanceled(JournalStreamWriter writer) => _inner.WriteCanceled(writer);
    }

    private sealed class RetryCapturingStorage : IJournalStorage
    {
        private readonly VolatileJournalStorage _inner = new();
        private bool _failNextAppend;

        public List<byte[]> AppendAttempts { get; } = [];

        public bool IsCompactionRequested => false;

        public void FailNextAppend() => _failNextAppend = true;

        public void ClearAttempts() => AppendAttempts.Clear();

        public ValueTask ReadAsync(IJournalStorageConsumer consumer, CancellationToken cancellationToken) =>
            _inner.ReadAsync(consumer, cancellationToken);

        public ValueTask ReplaceAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken) =>
            _inner.ReplaceAsync(value, cancellationToken);

        public async ValueTask AppendAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
        {
            var bytes = value.ToArray();
            AppendAttempts.Add(bytes);
            if (_failNextAppend)
            {
                _failNextAppend = false;
                throw new IOException("Expected append failure.");
            }

            await _inner.AppendAsync(new ReadOnlySequence<byte>(bytes), cancellationToken);
        }

        public ValueTask DeleteAsync(CancellationToken cancellationToken) => _inner.DeleteAsync(cancellationToken);
    }
}
