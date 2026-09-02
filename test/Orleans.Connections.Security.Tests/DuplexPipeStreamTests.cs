using System.Buffers;
using System.IO.Pipelines;
using Orleans.Connections.Security;
using Xunit;

namespace Orleans.Connections.Security.Tests;

public class DuplexPipeStreamTests
{
    [Fact]
    public void Properties_ReportReadableWritableNonSeekable()
    {
        var stream = CreateStream();

        Assert.True(stream.CanRead);
        Assert.True(stream.CanWrite);
        Assert.False(stream.CanSeek);
    }

    [Fact]
    public async Task Read_ImmediatePartialData_ReturnsExactPrefixAndPreservesRemainder()
    {
        var inbound = new Pipe();
        await inbound.Writer.WriteAsync(new byte[] { 10, 20, 30, 40, 50 }, TestContext.Current.CancellationToken);
        var stream = CreateStream(input: inbound.Reader);
        var first = new byte[3];
        var second = new byte[4];

        var firstCount = stream.Read(first, 0, first.Length);
        var secondCount = stream.Read(second, 1, 2);

        Assert.Equal(3, firstCount);
        Assert.Equal(new byte[] { 10, 20, 30 }, first);
        Assert.Equal(2, secondCount);
        Assert.Equal(new byte[] { 0, 40, 50, 0 }, second);
    }

    [Fact]
    public async Task Read_DelayedData_ReturnsExactBytes()
    {
        var reader = new ControlledPipeReader();
        var stream = CreateStream(input: reader);
        var buffer = new byte[5];

        var readTask = Task.Run(() => stream.Read(buffer, 1, 3));
        await reader.ReadStarted.WaitAsync(TestContext.Current.CancellationToken);
        var sequence = new ReadOnlySequence<byte>(new byte[] { 7, 8, 9 });
        reader.SetResult(new(sequence, isCanceled: false, isCompleted: false));

        Assert.Equal(3, await readTask);
        Assert.Equal(new byte[] { 0, 7, 8, 9, 0 }, buffer);
        Assert.Equal(sequence.End, reader.LastConsumed);
        Assert.Equal(1, reader.AdvanceCount);
    }

    [Fact]
    public async Task ReadAsync_ImmediateMultiSegmentData_ConsumesExactlyReturnedBytes()
    {
        var sequence = SequenceFactory.Create([1, 2], [3, 4, 5], [6, 7]);
        var reader = new ScriptedPipeReader(
            _ => new(new ReadResult(sequence, isCanceled: false, isCompleted: false)));
        var stream = CreateStream(input: reader);
        var buffer = new byte[6];

        var count = await stream.ReadAsync(buffer.AsMemory(1, 4), TestContext.Current.CancellationToken);

        Assert.Equal(4, count);
        Assert.Equal(new byte[] { 0, 1, 2, 3, 4, 0 }, buffer);
        Assert.Equal(sequence.GetPosition(4), reader.LastConsumed);
        Assert.Equal(sequence.GetPosition(4), reader.LastExamined);
        Assert.Equal(1, reader.AdvanceCount);
    }

    [Fact]
    public async Task ReadAsync_DelayedData_ReturnsExactBytes()
    {
        var reader = new ControlledPipeReader();
        var stream = CreateStream(input: reader);
        var buffer = new byte[4];

        var read = stream.ReadAsync(buffer.AsMemory(1, 2), TestContext.Current.CancellationToken);
        await reader.ReadStarted.WaitAsync(TestContext.Current.CancellationToken);
        var sequence = new ReadOnlySequence<byte>(new byte[] { 91, 92 });
        reader.SetResult(new(sequence, isCanceled: false, isCompleted: false));

        Assert.Equal(2, await read);
        Assert.Equal(new byte[] { 0, 91, 92, 0 }, buffer);
        Assert.Equal(sequence.End, reader.LastConsumed);
        Assert.Equal(TestContext.Current.CancellationToken, reader.LastReadCancellationToken);
    }

    [Fact]
    public async Task ReadAsync_CompletedEmptyReader_ReturnsEof()
    {
        var sequence = ReadOnlySequence<byte>.Empty;
        var reader = new ScriptedPipeReader(
            _ => new(new ReadResult(sequence, isCanceled: false, isCompleted: true)));
        var stream = CreateStream(input: reader);

        var count = await stream.ReadAsync(new byte[4], TestContext.Current.CancellationToken);

        Assert.Equal(0, count);
        Assert.Equal(sequence.Start, reader.LastConsumed);
        Assert.Equal(1, reader.AdvanceCount);
    }

    [Fact]
    public async Task ReadAsync_CanceledResult_ThrowsOperationCanceledException()
    {
        var sequence = new ReadOnlySequence<byte>(new byte[] { 1, 2 });
        var reader = new ScriptedPipeReader(
            _ => new(new ReadResult(sequence, isCanceled: true, isCompleted: false)));
        var stream = CreateStream(input: reader);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => stream.ReadAsync(new byte[4], TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(0, reader.AdvanceCount);
    }

    [Fact]
    public async Task ReadAsync_EmptyNonCompletedResult_ThrowsInvalidOperationException()
    {
        var sequence = ReadOnlySequence<byte>.Empty;
        var reader = new ScriptedPipeReader(
            _ => new(new ReadResult(sequence, isCanceled: false, isCompleted: false)));
        var stream = CreateStream(input: reader);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => stream.ReadAsync(new byte[4], TestContext.Current.CancellationToken).AsTask());

        Assert.Equal("Read zero bytes unexpectedly", exception.Message);
        Assert.Equal(sequence.Start, reader.LastConsumed);
        Assert.Equal(1, reader.AdvanceCount);
    }

    [Fact]
    public void Read_InvalidByteArrayArguments_Throw()
    {
        var stream = CreateStream();
        var buffer = new byte[4];

        Assert.Equal("buffer", Assert.Throws<ArgumentNullException>(() => stream.Read(null!, 0, 0)).ParamName);
        Assert.Equal("offset", Assert.Throws<ArgumentOutOfRangeException>(() => stream.Read(buffer, -1, 1)).ParamName);
        Assert.Equal("count", Assert.Throws<ArgumentOutOfRangeException>(() => stream.Read(buffer, 5, 0)).ParamName);
        Assert.Equal("count", Assert.Throws<ArgumentOutOfRangeException>(() => stream.Read(buffer, 0, -1)).ParamName);
        Assert.Equal("count", Assert.Throws<ArgumentOutOfRangeException>(() => stream.Read(buffer, 3, 2)).ParamName);
    }

    [Fact]
    public async Task ReadAsync_InvalidByteArrayArguments_Throw()
    {
        var stream = CreateStream();
        var buffer = new byte[4];

        Assert.Equal("buffer", (await Assert.ThrowsAsync<ArgumentNullException>(
            () => stream.ReadAsync(null!, 0, 0, TestContext.Current.CancellationToken))).ParamName);
        Assert.Equal("offset", (await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => stream.ReadAsync(buffer, -1, 1, TestContext.Current.CancellationToken))).ParamName);
        Assert.Equal("count", (await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => stream.ReadAsync(buffer, 5, 0, TestContext.Current.CancellationToken))).ParamName);
        Assert.Equal("count", (await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => stream.ReadAsync(buffer, 0, -1, TestContext.Current.CancellationToken))).ParamName);
        Assert.Equal("count", (await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => stream.ReadAsync(buffer, 3, 2, TestContext.Current.CancellationToken))).ParamName);
    }

    [Fact]
    public void Write_InvalidByteArrayArguments_Throw()
    {
        var stream = CreateStream();
        var buffer = new byte[4];

        Assert.Equal("buffer", Assert.Throws<ArgumentNullException>(() => stream.Write(null!, 0, 0)).ParamName);
        Assert.Equal("offset", Assert.Throws<ArgumentOutOfRangeException>(() => stream.Write(buffer, -1, 1)).ParamName);
        Assert.Equal("count", Assert.Throws<ArgumentOutOfRangeException>(() => stream.Write(buffer, 5, 0)).ParamName);
        Assert.Equal("count", Assert.Throws<ArgumentOutOfRangeException>(() => stream.Write(buffer, 0, -1)).ParamName);
        Assert.Equal("count", Assert.Throws<ArgumentOutOfRangeException>(() => stream.Write(buffer, 3, 2)).ParamName);
    }

    [Fact]
    public async Task WriteAsync_InvalidByteArrayArguments_Throw()
    {
        var stream = CreateStream();
        var buffer = new byte[4];

        Assert.Equal("buffer", (await Assert.ThrowsAsync<ArgumentNullException>(
            () => stream.WriteAsync(null!, 0, 0, TestContext.Current.CancellationToken))).ParamName);
        Assert.Equal("offset", (await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => stream.WriteAsync(buffer, -1, 1, TestContext.Current.CancellationToken))).ParamName);
        Assert.Equal("count", (await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => stream.WriteAsync(buffer, 5, 0, TestContext.Current.CancellationToken))).ParamName);
        Assert.Equal("count", (await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => stream.WriteAsync(buffer, 0, -1, TestContext.Current.CancellationToken))).ParamName);
        Assert.Equal("count", (await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => stream.WriteAsync(buffer, 3, 2, TestContext.Current.CancellationToken))).ParamName);
    }

    [Fact]
    public void Write_WritesExactSelectedBytes()
    {
        var writer = new RecordingPipeWriter(new FlushResult(isCanceled: false, isCompleted: false));
        var stream = CreateStream(output: writer);

        stream.Write(new byte[] { 1, 2, 3, 4, 5 }, 1, 3);

        Assert.Equal(new byte[] { 2, 3, 4 }, writer.Written.ToArray());
        Assert.Equal(1, writer.FlushCount);
    }

    [Fact]
    public async Task WriteAsync_WritesExactSelectedBytes()
    {
        var writer = new RecordingPipeWriter(new FlushResult(isCanceled: false, isCompleted: false));
        var stream = CreateStream(output: writer);

        await stream.WriteAsync(new byte[] { 10, 20, 30, 40 }, 1, 2, TestContext.Current.CancellationToken);

        Assert.Equal(new byte[] { 20, 30 }, writer.Written.ToArray());
        Assert.Equal(1, writer.FlushCount);
        Assert.Equal(TestContext.Current.CancellationToken, writer.LastFlushCancellationToken);
    }

    [Fact]
    public async Task WriteAsync_CanceledFlushResult_ThrowsOperationCanceledException()
    {
        var writer = new RecordingPipeWriter(new FlushResult(isCanceled: true, isCompleted: false));
        var stream = CreateStream(output: writer);
        using var cancellation = new CancellationTokenSource();

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(
            () => stream.WriteAsync(new byte[] { 4, 5 }, cancellation.Token).AsTask());

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(new byte[] { 4, 5 }, writer.Written.ToArray());
        Assert.Equal(1, writer.FlushCount);
    }

    [Fact]
    public async Task FlushAndFlushAsync_CanceledResult_ThrowOperationCanceledException()
    {
        var writer = new RecordingPipeWriter(new FlushResult(isCanceled: true, isCompleted: false));
        var stream = CreateStream(output: writer);
        using var cancellation = new CancellationTokenSource();

        var synchronous = Assert.Throws<OperationCanceledException>(stream.Flush);
        var asynchronous = await Assert.ThrowsAsync<OperationCanceledException>(
            () => stream.FlushAsync(cancellation.Token));

        Assert.Equal(CancellationToken.None, synchronous.CancellationToken);
        Assert.Equal(cancellation.Token, asynchronous.CancellationToken);
        Assert.Equal(2, writer.FlushCount);
        Assert.Equal(cancellation.Token, writer.LastFlushCancellationToken);
    }

    [Fact]
    public async Task CopyToAsync_CopiesExactBytes()
    {
        var inbound = new Pipe();
        await inbound.Writer.WriteAsync(new byte[] { 1, 3, 5, 7, 9 }, TestContext.Current.CancellationToken);
        await inbound.Writer.CompleteAsync();
        var stream = CreateStream(input: inbound.Reader);
        await using var destination = new MemoryStream();

        await stream.CopyToAsync(destination, 2, CancellationToken.None);

        Assert.Equal(new byte[] { 1, 3, 5, 7, 9 }, destination.ToArray());
        Assert.Equal(5, destination.Length);
    }

    [Fact]
    public async Task CopyToAsync_ForwardsCancellationTokenToEveryRead()
    {
        using var cancellation = new CancellationTokenSource();
        var invocation = 0;
        var reader = new ScriptedPipeReader(
            _ =>
            {
                invocation++;
                return invocation == 1
                    ? new(new ReadResult(new ReadOnlySequence<byte>(new byte[] { 2, 4, 6 }), false, false))
                    : new(new ReadResult(ReadOnlySequence<byte>.Empty, false, true));
            });
        var stream = CreateStream(input: reader);
        await using var destination = new MemoryStream();

        await stream.CopyToAsync(destination, 2, cancellation.Token);

        Assert.Equal(new byte[] { 2, 4, 6 }, destination.ToArray());
        Assert.Equal(2, reader.ReadCancellationTokens.Count);
        Assert.All(reader.ReadCancellationTokens, token => Assert.Equal(cancellation.Token, token));
    }

    [Fact]
    public async Task BeginReadAndEndRead_PreserveStateCallbackAndExactBytes()
    {
        var reader = new ControlledPipeReader();
        var stream = CreateStream(input: reader);
        var buffer = new byte[4];
        var state = new object();
        var callback = new TaskCompletionSource<IAsyncResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        var result = stream.BeginRead(buffer, 1, 2, callback.SetResult, state);
        await reader.ReadStarted.WaitAsync(TestContext.Current.CancellationToken);
        var sequence = new ReadOnlySequence<byte>(new byte[] { 8, 9 });
        reader.SetResult(new(sequence, false, false));
        var callbackResult = await callback.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Same(result, callbackResult);
        Assert.Same(state, result.AsyncState);
        Assert.Equal(2, stream.EndRead(result));
        Assert.Equal(new byte[] { 0, 8, 9, 0 }, buffer);
    }

    [Fact]
    public async Task BeginWriteAndEndWrite_PreserveStateCallbackAndExactBytes()
    {
        var writer = new RecordingPipeWriter(new FlushResult(false, false));
        var stream = CreateStream(output: writer);
        var state = new object();
        var callback = new TaskCompletionSource<IAsyncResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        var result = stream.BeginWrite(new byte[] { 1, 3, 5, 7 }, 1, 2, callback.SetResult, state);
        var callbackResult = await callback.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Same(result, callbackResult);
        Assert.Same(state, result.AsyncState);
        stream.EndWrite(result);
        Assert.Equal(new byte[] { 3, 5 }, writer.Written.ToArray());
        Assert.Equal(1, writer.FlushCount);
    }

    [Fact]
    public void UnsupportedSeekLengthPositionAndSetLength_Throw()
    {
        var stream = CreateStream();

        Assert.Throws<NotSupportedException>(() => stream.Seek(1, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => stream.SetLength(8));
        Assert.Throws<NotSupportedException>(() => stream.Length);
        Assert.Throws<NotSupportedException>(() => stream.Position);
        Assert.Throws<NotSupportedException>(() => stream.Position = 3);
        Assert.False(stream.CanSeek);
    }

    [Fact]
    public void Dispose_CompletesReaderAndWriterSynchronously()
    {
        var reader = new ScriptedPipeReader(
            _ => new(new ReadResult(ReadOnlySequence<byte>.Empty, false, true)));
        var writer = new RecordingPipeWriter(new FlushResult(false, false));
        var stream = CreateStream(reader, writer);

        stream.Dispose();

        Assert.Equal(1, reader.CompleteCount);
        Assert.Equal(0, reader.CompleteAsyncCount);
        Assert.Equal(1, writer.CompleteCount);
        Assert.Equal(0, writer.CompleteAsyncCount);
    }

    [Fact]
    public async Task DisposeAsync_CompletesReaderAndWriterAsynchronously()
    {
        var reader = new ScriptedPipeReader(
            _ => new(new ReadResult(ReadOnlySequence<byte>.Empty, false, true)));
        var writer = new RecordingPipeWriter(new FlushResult(false, false));
        var stream = CreateStream(reader, writer);

        await stream.DisposeAsync();

        Assert.Equal(0, reader.CompleteCount);
        Assert.Equal(1, reader.CompleteAsyncCount);
        Assert.Equal(0, writer.CompleteCount);
        Assert.Equal(1, writer.CompleteAsyncCount);
    }

    private static DuplexPipeStream CreateStream(PipeReader? input = null, PipeWriter? output = null)
    {
        input ??= new Pipe().Reader;
        output ??= new Pipe().Writer;
        return new(new TestDuplexPipe(input, output));
    }
}
