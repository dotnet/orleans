using System.Buffers;
using System.IO.Pipelines;
using System.Net.Security;
using Orleans.Connections.Security;
using Xunit;

namespace Orleans.Connections.Security.Tests;

public class DuplexPipeStreamAdapterTests
{
    [Fact]
    public async Task Adapter_DefaultConstructor_PassesItselfToFactoryAndWiresProperties()
    {
        var inbound = new Pipe();
        var outbound = new Pipe();
        var transport = new TestDuplexPipe(inbound.Reader, outbound.Writer);
        Stream? factoryInput = null;
        TrackingStream? decorated = null;

        var adapter = new DuplexPipeStreamAdapter<TrackingStream>(
            transport,
            stream =>
            {
                factoryInput = stream;
                return decorated = new TrackingStream(stream);
            });

        Assert.Same(adapter, factoryInput);
        var tracked = Assert.IsType<TrackingStream>(decorated);
        Assert.Same(tracked, adapter.Stream);
        Assert.NotNull(adapter.Input);
        Assert.NotNull(adapter.Output);

        await inbound.Writer.WriteAsync(new byte[] { 1, 2, 3 }, TestContext.Current.CancellationToken);
        var read = await adapter.Input.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new byte[] { 1, 2, 3 }, read.Buffer.ToArray());
        adapter.Input.AdvanceTo(read.Buffer.End);

        await adapter.Output.WriteAsync(new byte[] { 4, 5, 6, 7 }, TestContext.Current.CancellationToken);
        var written = await outbound.Reader.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new byte[] { 4, 5, 6, 7 }, written.Buffer.ToArray());
        outbound.Reader.AdvanceTo(written.Buffer.End);

        adapter.Dispose();
        Assert.Equal(0, tracked.DisposeCount);
        Assert.Equal(0, tracked.DisposeAsyncCount);
    }

    [Fact]
    public async Task Adapter_ExplicitOptions_ApplyReaderWriterBehaviorAndWireProperties()
    {
        var inbound = new Pipe();
        var outbound = new Pipe();
        var transport = new TestDuplexPipe(inbound.Reader, outbound.Writer);
        var readerOptions = new StreamPipeReaderOptions(
            bufferSize: 64,
            minimumReadSize: 16,
            leaveOpen: false);
        var writerOptions = new StreamPipeWriterOptions(
            minimumBufferSize: 32,
            leaveOpen: false);
        TrackingStream? decorated = null;

        var adapter = new DuplexPipeStreamAdapter<TrackingStream>(
            transport,
            readerOptions,
            writerOptions,
            stream => decorated = new TrackingStream(stream));

        var tracked = Assert.IsType<TrackingStream>(decorated);
        Assert.Same(tracked, adapter.Stream);
        Assert.NotNull(adapter.Input);
        Assert.NotNull(adapter.Output);

        await inbound.Writer.WriteAsync(new byte[] { 11, 12 }, TestContext.Current.CancellationToken);
        var read = await adapter.Input.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new byte[] { 11, 12 }, read.Buffer.ToArray());
        adapter.Input.AdvanceTo(read.Buffer.End);

        await adapter.DisposeAsync();

        Assert.NotEqual(0, tracked.DisposeCount + tracked.DisposeAsyncCount);
    }

    [Fact]
    public async Task Adapter_MixedSyncAsyncDisposal_IsIdempotent()
    {
        var transport = new TestDuplexPipe(new Pipe().Reader, new Pipe().Writer);
        TrackingStream? decorated = null;
        var adapter = new DuplexPipeStreamAdapter<TrackingStream>(
            transport,
            new StreamPipeReaderOptions(leaveOpen: false),
            new StreamPipeWriterOptions(leaveOpen: false),
            stream => decorated = new TrackingStream(stream));

        adapter.Dispose();
        var tracked = Assert.IsType<TrackingStream>(decorated);
        var disposeCount = tracked.DisposeCount;
        var disposeAsyncCount = tracked.DisposeAsyncCount;
        await adapter.DisposeAsync();
        adapter.Dispose();

        Assert.NotEqual(0, disposeCount + disposeAsyncCount);
        Assert.Equal(disposeCount, tracked.DisposeCount);
        Assert.Equal(disposeAsyncCount, tracked.DisposeAsyncCount);
    }

    [Fact]
    public void TlsDuplexPipe_DefaultFactory_CreatesSslStreamAndWiresPipe()
    {
        var transport = new TestDuplexPipe(new Pipe().Reader, new Pipe().Writer);

        using var adapter = new TlsDuplexPipe(
            transport,
            new StreamPipeReaderOptions(leaveOpen: true),
            new StreamPipeWriterOptions(leaveOpen: true));

        Assert.IsType<SslStream>(adapter.Stream);
        Assert.NotNull(adapter.Input);
        Assert.NotNull(adapter.Output);
        Assert.NotSame(transport.Input, adapter.Input);
        Assert.NotSame(transport.Output, adapter.Output);
        Assert.False(adapter.Stream.IsAuthenticated);
        Assert.False(adapter.Stream.IsEncrypted);
        adapter.Stream.Dispose();
    }

    [Fact]
    public void TlsDuplexPipe_CustomFactory_ReceivesAdapterAndPreservesExactStream()
    {
        var transport = new TestDuplexPipe(new Pipe().Reader, new Pipe().Writer);
        Stream? factoryInput = null;
        SslStream? sslStream = null;

        using var adapter = new TlsDuplexPipe(
            transport,
            new StreamPipeReaderOptions(leaveOpen: true),
            new StreamPipeWriterOptions(leaveOpen: true),
            stream =>
            {
                factoryInput = stream;
                return sslStream = new SslStream(stream, leaveInnerStreamOpen: true);
            });

        Assert.Same(adapter, factoryInput);
        Assert.Same(sslStream, adapter.Stream);
        Assert.NotNull(adapter.Input);
        Assert.NotNull(adapter.Output);
        adapter.Stream.Dispose();
    }

    [Fact]
    public async Task TlsDuplexPipe_MixedSyncAsyncDisposal_IsIdempotent()
    {
        var transport = new TestDuplexPipe(new Pipe().Reader, new Pipe().Writer);
        TrackingStream? tracked = null;
        var adapter = new TlsDuplexPipe(
            transport,
            new StreamPipeReaderOptions(leaveOpen: false),
            new StreamPipeWriterOptions(leaveOpen: false),
            stream =>
            {
                tracked = new TrackingStream(stream);
                return new SslStream(tracked, leaveInnerStreamOpen: false);
            });

        await adapter.DisposeAsync();
        var trackingStream = Assert.IsType<TrackingStream>(tracked);
        var disposeCount = trackingStream.DisposeCount;
        var disposeAsyncCount = trackingStream.DisposeAsyncCount;
        adapter.Dispose();
        await adapter.DisposeAsync();

        Assert.NotEqual(0, disposeCount + disposeAsyncCount);
        Assert.Equal(disposeCount, trackingStream.DisposeCount);
        Assert.Equal(disposeAsyncCount, trackingStream.DisposeAsyncCount);
    }

    [Fact]
    public async Task Adapter_ReaderLeaveOpenFalse_DisposesDecoratedStream()
    {
        var transport = new TestDuplexPipe(new Pipe().Reader, new Pipe().Writer);
        TrackingStream? decorated = null;
        var adapter = new DuplexPipeStreamAdapter<TrackingStream>(
            transport,
            new StreamPipeReaderOptions(leaveOpen: false),
            new StreamPipeWriterOptions(leaveOpen: true),
            stream => decorated = new TrackingStream(stream));

        await adapter.DisposeAsync();

        var tracked = Assert.IsType<TrackingStream>(decorated);
        Assert.Equal(1, tracked.DisposeCount + tracked.DisposeAsyncCount);
    }

    [Fact]
    public async Task Adapter_WriterLeaveOpenFalse_DisposesDecoratedStream()
    {
        var transport = new TestDuplexPipe(new Pipe().Reader, new Pipe().Writer);
        TrackingStream? decorated = null;
        var adapter = new DuplexPipeStreamAdapter<TrackingStream>(
            transport,
            new StreamPipeReaderOptions(leaveOpen: true),
            new StreamPipeWriterOptions(leaveOpen: false),
            stream => decorated = new TrackingStream(stream));

        await adapter.DisposeAsync();

        var tracked = Assert.IsType<TrackingStream>(decorated);
        Assert.Equal(1, tracked.DisposeCount + tracked.DisposeAsyncCount);
    }
}
