using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans;
using Orleans.Configuration;
using Orleans.CodeGeneration;
using Orleans.Messaging;
using Orleans.Connections;
using Orleans.Connections.Transport;
using Orleans.Placement.Repartitioning;
using Orleans.Runtime;
using Orleans.Runtime.Messaging;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Configuration;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.Invocation;
using Orleans.Serialization.Session;
using Orleans.Serialization.WireProtocol;
using TestExtensions;
using Xunit;

namespace UnitTests.Runtime;

[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
public class OwnedRpcArgumentLifetimeTests
{
    [Theory]
    [InlineData("success")]
    [InlineData("rejection")]
    [InlineData("cancellation")]
    [InlineData("timeout")]
    [InlineData("unavailable")]
    [InlineData("shutdown")]
    public void CallbackTerminalOutcomes_ReleaseCopiedPinsExactlyOnce(string outcome)
    {
        using var fixture = new TransportFixture();
        using var source = new ArcBufferWriter();
        source.Write(Bytes);
        using var argument = source.PeekSlice(source.Length);
        var pages = argument.Pages.ToArray();
        var baseline = pages.Select(page => page.ReferenceCount).ToArray();
        using var request = CreateRequest(fixture.Services, argument);
        var owner = Assert.IsAssignableFrom<IInvokableArgumentOwner>(request);
        AssertCounts(pages, baseline, 1);
        var message = fixture.Message(request);
        var callback = fixture.Callback(message);
        using var cancellation = new CancellationTokenSource();
        callback.SubscribeForCancellation(cancellation.Token);

        Complete(callback, cancellation, outcome);
        var firstResponse = fixture.Completion.Response;
        Assert.True(callback.IsCompleted);
        AssertCounts(pages, baseline, 0);
        Assert.False(owner.TryRetainArgumentResources());
        Assert.True(Assert.IsType<ArcBuffer>(request.GetArgument(0)).IsEmpty);

        callback.OnTimeout();
        callback.OnHostShutdown();
        message.Dispose();
        request.Dispose();
        AssertCounts(pages, baseline, 0);
        Assert.Same(firstResponse, fixture.Completion.Response);
        Assert.Equal(1, fixture.Completion.Count);
        Assert.Equal(Bytes, argument.ToArray());
    }

    [Theory]
    [InlineData("cancellation")]
    [InlineData("timeout")]
    [InlineData("shutdown")]
    [InlineData("dispose")]
    public async Task TerminalCompletion_DoesNotReleaseDuringActualFieldSerialization(string outcome)
    {
        using var fixture = new TransportFixture();
        using var source = new ArcBufferWriter();
        source.Write(Bytes);
        using var argument = new OwnedTransportArgument(source.PeekSlice(source.Length)) { BlockWriter = true };
        var pages = argument.Buffer.Pages.ToArray();
        var baseline = pages.Select(page => page.ReferenceCount).ToArray();
        using var request = CreateRequest(fixture.Services, argument);
        var copied = Assert.IsType<OwnedTransportArgument>(request.GetArgument(0));
        var owner = Assert.IsAssignableFrom<IInvokableArgumentOwner>(request);
        var message = fixture.Message(request);
        var callback = fixture.Callback(message);
        using var cancellation = new CancellationTokenSource();
        callback.SubscribeForCancellation(cancellation.Token);
        using var wire = new ArcBufferWriter();
        using var serializer = fixture.Serializer();
        var write = Task.Run(() => serializer.Write(wire, message), TestContext.Current.CancellationToken);
        try
        {
            await Phase(copied.WriterEntered.Task, "actual field serializer entered");
            if (outcome == "dispose") message.Dispose();
            else Complete(callback, cancellation, outcome);
            AssertCounts(pages, baseline, 1);
            Assert.Equal(0, copied.DisposeCount);
            Assert.Same(copied, request.GetArgument(0));
            Assert.False(owner.TryRetainArgumentResources());
        }
        finally
        {
            copied.WriterRelease.TrySetResult();
            await Phase(write, "actual field serializer exited");
        }

        var lengths = await write;
        AssertCounts(pages, baseline, 0);
        Assert.Equal(1, copied.DisposeCount);
        using var read = new MessageReadRequest(fixture.Shared);
        using (var frame = wire.PeekSlice(wire.Length))
        {
            read.Body = frame.Slice(lengths.HeaderLength, lengths.BodyLength);
        }
        using var received = new Message { Direction = Message.Directions.Request };
        serializer.ReadBodyObject(received, read);
        read.Body.Dispose();
        read.Body = default;
        wire.Dispose();
        var decodedRequest = Assert.IsAssignableFrom<IInvokable>(received.BodyObject);
        var decoded = Assert.IsType<OwnedTransportArgument>(decodedRequest.GetArgument(0));
        Assert.Equal(Bytes, decoded.Buffer.ToArray());
        var decodedPages = decoded.Buffer.Pages.ToArray();
        received.Dispose();
        Assert.All(decodedPages, page => Assert.Equal(0, page.ReferenceCount));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RetiredRequest_IsRejectedBeforeWritingFields(bool disposeMessage)
    {
        using var fixture = new TransportFixture();
        using var source = new ArcBufferWriter();
        source.Write(Bytes);
        using var argument = source.PeekSlice(source.Length);
        using var request = CreateRequest(fixture.Services, argument);
        var message = fixture.Message(request);
        if (disposeMessage) message.Dispose();
        else Assert.IsAssignableFrom<IInvokableArgumentOwner>(request).CompleteArgumentResources();
        using var wire = new ArcBufferWriter();
        using var serializer = fixture.Serializer();
        Assert.Throws<OperationCanceledException>(() => serializer.Write(wire, message));
        Assert.Equal(0, wire.Length);
        Assert.Equal(Bytes, argument.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActualWriteCompletion_ReleasesOnlyOneWayRequests(bool oneWay)
    {
        using var fixture = new TransportFixture();
        using var source = new ArcBufferWriter();
        source.Write(Bytes);
        using var argument = source.PeekSlice(source.Length);
        var pages = argument.Pages.ToArray();
        var baseline = pages.Select(page => page.ReferenceCount).ToArray();
        using var request = CreateRequest(fixture.Services, argument);
        var message = fixture.Message(request, oneWay);
        var write = fixture.Shared.GetSendMessageHandler(fixture.Connection);
        write.WriteMessage(message);
        AssertCounts(pages, baseline, 1);
        write.SetResult();
        AssertCounts(pages, baseline, oneWay ? 0 : 1);
        if (!oneWay) fixture.Callback(message).OnTimeout(); // actual no-response terminal outcome
        AssertCounts(pages, baseline, 0);
    }

    [Fact]
    public async Task FailedWrite_ReroutesWithoutCompletingArguments()
    {
        using var fixture = new TransportFixture();
        using var source = new ArcBufferWriter();
        source.Write(Bytes);
        using var argument = source.PeekSlice(source.Length);
        var pages = argument.Pages.ToArray();
        var baseline = pages.Select(page => page.ReferenceCount).ToArray();
        using var request = CreateRequest(fixture.Services, argument);
        var message = fixture.Message(request, oneWay: true);
        var first = fixture.Shared.GetSendMessageHandler(fixture.Connection);
        first.WriteMessage(message);
        var error = new InvalidOperationException("write failed before completion");
        first.SetException(error);
        await Phase(fixture.Connection.Retried.Task, "failed write rerouted");
        Assert.Same(message, await fixture.Connection.Retried.Task);
        AssertCounts(pages, baseline, 1);
        var retry = fixture.Shared.GetSendMessageHandler(fixture.Connection);
        retry.WriteMessage(message);
        retry.SetResult();
        AssertCounts(pages, baseline, 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualSerializationFailure_ReleasesArgumentsAndPreservesFirstCause(bool cleanupThrows)
    {
        using var fixture = new TransportFixture();
        using var source = new ArcBufferWriter();
        source.Write(Bytes);
        using var argument = new OwnedTransportArgument(source.PeekSlice(source.Length))
        {
            WriterError = new InvalidOperationException("first field serialization failure")
        };
        var pages = argument.Buffer.Pages.ToArray();
        var baseline = pages.Select(page => page.ReferenceCount).ToArray();
        using var request = CreateRequest(fixture.Services, argument);
        if (cleanupThrows) Assert.IsType<OwnedTransportArgument>(request.GetArgument(0)).DisposeError =
            new InvalidOperationException("cleanup must not mask serialization failure");
        var message = fixture.Message(request);
        var callback = fixture.Callback(message);
        fixture.Shared.MessageCenter.When(center => center.DispatchLocalMessage(Arg.Any<Message>()))
            .Do(call => callback.DoCallback(call.Arg<Message>()));
        fixture.Connection.Send(message);
        await Phase(fixture.Completion.Completed.Task, "serialization rejection callback");
        // Quiescence, not callback publication, is evidence that the worker released the request.
        fixture.Shared.Dispose();
        Assert.Same(argument.WriterError, fixture.Completion.Response.Exception);
        AssertCounts(pages, baseline, 0);
        Assert.Equal(1, fixture.Completion.Count);
    }

    [Fact]
    public void DisposedQueuedWrite_CompletesArguments()
    {
        using var fixture = new TransportFixture();
        using var source = new ArcBufferWriter();
        source.Write(Bytes);
        using var argument = source.PeekSlice(source.Length);
        var pages = argument.Pages.ToArray();
        var baseline = pages.Select(page => page.ReferenceCount).ToArray();
        using var request = CreateRequest(fixture.Services, argument);
        using var write = new MessageWriteRequest(fixture.Shared);
        write.WriteMessage(fixture.Message(request));
        AssertCounts(pages, baseline, 1);
        write.Dispose();
        AssertCounts(pages, baseline, 0);
    }

    [Theory]
    [InlineData("send", false)]
    [InlineData("filter", false)]
    [InlineData("short-circuit", false)]
    [InlineData("send", true)]
    [InlineData("filter", true)]
    [InlineData("short-circuit", true)]
    public async Task ProxySendAndFilterOutcomes_CompleteUntransferredArguments(string outcome, bool cleanupThrows)
    {
        using var fixture = new TransportFixture();
        using var source = new ArcBufferWriter();
        source.Write(Bytes);
        using var argument = new OwnedTransportArgument(source.PeekSlice(source.Length));
        var pages = argument.Buffer.Pages.ToArray();
        var baseline = pages.Select(page => page.ReferenceCount).ToArray();
        using var request = CreateRequest(fixture.Services, argument, out var reference);
        var runtimeClient = Substitute.For<IRuntimeClient>();
        var failure = new InvalidOperationException("original outgoing failure");
        var cleanupFailure = new InvalidOperationException("outgoing cleanup failure");
        if (cleanupThrows) Assert.IsType<OwnedTransportArgument>(request.GetArgument(0)).DisposeError = cleanupFailure;
        var logger = Substitute.For<ILogger<GrainReferenceRuntime>>();
        logger.IsEnabled(LogLevel.Warning).Returns(true);
        runtimeClient.When(client => client.SendRequest(Arg.Any<GrainReference>(), Arg.Any<IInvokable>(),
            Arg.Any<IResponseCompletionSource>(), Arg.Any<InvokeMethodOptions>())).Do(_ => throw failure);
        var filter = Substitute.For<IOutgoingGrainCallFilter>();
        filter.Invoke(Arg.Any<IOutgoingGrainCallContext>()).Returns(call =>
        {
            if (outcome == "filter") throw failure;
            call.Arg<IOutgoingGrainCallContext>().Response = Response.Completed;
            return Task.CompletedTask;
        });
        var runtime = new GrainReferenceRuntime(runtimeClient, Substitute.For<IGrainCancellationTokenRuntime>(),
            outcome == "send" ? [] : [filter], null!, null!, logger);
        if (outcome == "short-circuit")
        {
            await runtime.InvokeMethodAsync(reference, request, InvokeMethodOptions.None);
        }
        else
        {
            var observed = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await runtime.InvokeMethodAsync(reference, request, InvokeMethodOptions.None));
            Assert.Same(failure, observed);
        }

        AssertCounts(pages, baseline, 0);
        Assert.Equal(Bytes, argument.Buffer.ToArray());
        var logs = logger.ReceivedCalls().Where(call => call.GetMethodInfo().Name == nameof(ILogger.Log));
        if (cleanupThrows)
        {
            Assert.Same(cleanupFailure, Assert.Single(logs).GetArguments()[3]);
        }
        else
        {
            Assert.Empty(logs);
        }
    }

    [Fact]
    public void CleanupFailure_DoesNotReplaceCallbackFailureOrLeaveFieldsOwned()
    {
        using var fixture = new TransportFixture();
        using var source = new ArcBufferWriter();
        source.Write(Bytes);
        using var argument = new OwnedTransportArgument(source.PeekSlice(source.Length));
        var pages = argument.Buffer.Pages.ToArray();
        var baseline = pages.Select(page => page.ReferenceCount).ToArray();
        using var request = CreateRequest(fixture.Services, argument);
        var copied = Assert.IsType<OwnedTransportArgument>(request.GetArgument(0));
        copied.DisposeError = new InvalidOperationException("owned argument cleanup failure");
        var logger = Substitute.For<ILogger>();
        logger.IsEnabled(LogLevel.Warning).Returns(true);
        var message = fixture.Message(request);
        message.ArgumentResourceLogger = logger;
        fixture.Callback(message).OnHostShutdown();
        var log = Assert.Single(logger.ReceivedCalls(), call => call.GetMethodInfo().Name == nameof(ILogger.Log));
        Assert.Same(copied.DisposeError, log.GetArguments()[3]);
        Assert.IsType<SiloUnavailableException>(fixture.Completion.Response.Exception);
        AssertCounts(pages, baseline, 0);
        Assert.Equal(1, copied.DisposeCount);
        Assert.Null(request.GetArgument(0));
    }

    [Theory]
    [InlineData("complete", false)]
    [InlineData("release", false)]
    [InlineData("dispose", false)]
    [InlineData("complete", true)]
    [InlineData("release", true)]
    [InlineData("dispose", true)]
    public void CleanupFailure_IsLoggedOrRethrownWithoutLogger(string operation, bool hasLogger)
    {
        using var fixture = new TransportFixture();
        using var source = new ArcBufferWriter();
        source.Write(Bytes);
        using var argument = new OwnedTransportArgument(source.PeekSlice(source.Length));
        var pages = argument.Buffer.Pages.ToArray();
        var baseline = pages.Select(page => page.ReferenceCount).ToArray();
        using var request = CreateRequest(fixture.Services, argument);
        var owner = Assert.IsAssignableFrom<IInvokableArgumentOwner>(request);
        var copied = Assert.IsType<OwnedTransportArgument>(request.GetArgument(0));
        var cleanupFailure = new InvalidOperationException("cleanup failure must remain visible");
        copied.DisposeError = cleanupFailure;
        var logger = hasLogger ? Substitute.For<ILogger>() : null;
        if (logger is not null) logger.IsEnabled(LogLevel.Warning).Returns(true);
        if (operation == "release")
        {
            Assert.True(owner.TryRetainArgumentResources());
            owner.CompleteArgumentResources();
        }

        void Cleanup()
        {
            switch (operation)
            {
                case "complete": InvokableArgumentResources.Complete(owner, logger); break;
                case "release": InvokableArgumentResources.Release(owner, logger); break;
                case "dispose": InvokableArgumentResources.Dispose(request, logger); break;
                default: throw new ArgumentOutOfRangeException(nameof(operation));
            }
        }

        if (logger is null)
        {
            Assert.Same(cleanupFailure, Assert.Throws<InvalidOperationException>(Cleanup));
        }
        else
        {
            Cleanup();
            var log = Assert.Single(logger.ReceivedCalls(), call => call.GetMethodInfo().Name == nameof(ILogger.Log));
            Assert.Equal(LogLevel.Warning, log.GetArguments()[0]);
            Assert.Same(cleanupFailure, log.GetArguments()[3]);
        }

        AssertCounts(pages, baseline, 0);
        Assert.Equal(1, copied.DisposeCount);
        Assert.Null(request.GetArgument(0));
    }

    [Fact]
    public void GrainReferenceRuntime_RequiresLogger()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => new GrainReferenceRuntime(
            Substitute.For<IRuntimeClient>(), Substitute.For<IGrainCancellationTokenRuntime>(), [], null!, null!, null!));
        Assert.Equal("logger", exception.ParamName);
    }

    internal static readonly byte[] Bytes = Enumerable.Range(0, 50037).Select(i => (byte)(i * 19)).ToArray();

    internal static IInvokable CreateRequest(IServiceProvider services, object argument) => CreateRequest(services, argument, out _);

    private static IInvokable CreateRequest(IServiceProvider services, object argument, out GrainReference reference)
    {
        var runtime = new CapturingRuntime();
        var shared = new GrainReferenceShared(GrainType.Create("owned-rpc"), GrainInterfaceType.Create("owned-rpc"),
            0, runtime, InvokeMethodOptions.None, services.GetRequiredService<CodecProvider>(),
            services.GetRequiredService<CopyContextPool>(), services);
        var proxyType = Assert.Single(services.GetRequiredService<IOptions<TypeManifestOptions>>().Value.InterfaceProxies,
            type => typeof(IOwnedTransportCalls).IsAssignableFrom(type));
        var proxy = Assert.IsAssignableFrom<IOwnedTransportCalls>(Activator.CreateInstance(proxyType, shared, IdSpan.Create("owned")));
        reference = proxy.AsReference();
        var call = argument is ArcBuffer buffer ? proxy.Owned(buffer) : proxy.Blocked((OwnedTransportArgument)argument);
        Assert.True(call.IsCompletedSuccessfully);
        return Assert.Single(runtime.Requests);
    }

    internal static void AssertCounts(ArcBufferPage[] pages, int[] baseline, int extra) =>
        Assert.Equal(baseline.Select(count => count + extra).ToArray(), pages.Select(page => page.ReferenceCount).ToArray());

    private static Task Phase(Task task, string phase) => DrainTestHelpers.AwaitPhaseAsync(task, phase,
        () => $"owned RPC phase={phase}; task={task.Status}", TestContext.Current.CancellationToken);

    private static void Complete(CallbackData callback, CancellationTokenSource cancellation, string outcome)
    {
        switch (outcome)
        {
            case "success": callback.DoCallback(new Message { BodyObject = Response.Completed }); break;
            case "rejection": callback.DoCallback(new Message { BodyObject = new RejectionResponse
                { RejectionType = Message.RejectionTypes.Unrecoverable, Exception = new InvalidOperationException("rejected") } }); break;
            case "cancellation": cancellation.Cancel(); break;
            case "timeout": callback.OnTimeout(); break;
            case "unavailable": callback.OnTargetSiloFail(); break;
            case "shutdown": callback.OnHostShutdown(); break;
            default: throw new ArgumentOutOfRangeException(nameof(outcome));
        }
    }

    private sealed class CapturingRuntime : IGrainReferenceRuntime
    {
        internal List<IInvokable> Requests { get; } = [];
        public ValueTask<T?> InvokeMethodAsync<T>(GrainReference reference, IInvokable request, InvokeMethodOptions options)
        { Requests.Add(request); return default; }
        public ValueTask InvokeMethodAsync(GrainReference reference, IInvokable request, InvokeMethodOptions options)
        { Requests.Add(request); return default; }
        public void InvokeMethod(GrainReference reference, IInvokable request, InvokeMethodOptions options) => Requests.Add(request);
        public object Cast(IAddressable grain, Type interfaceType) => grain;
    }

    private sealed class CompletionSource : IResponseCompletionSource
    {
        internal Response Response { get; private set; } = null!;
        internal int Count { get; private set; }
        internal TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Complete(Response value) { Response = value; Count++; Completed.TrySetResult(); }
        public void Complete() => Complete(Response.Completed);
    }

    private sealed class TransportFixture : IDisposable
    {
        internal TransportFixture()
        {
            Services = new ServiceCollection().AddMetrics().AddSerializer().BuildServiceProvider();
            var instruments = new OrleansInstruments(Services.GetRequiredService<IMeterFactory>());
            var messaging = new MessagingInstruments(instruments);
            var trace = new MessagingTrace(NullLoggerFactory.Instance, messaging, new MessagingProcessingInstruments(instruments));
            Shared = new(trace, new ConnectionTrace(NullLoggerFactory.Instance), Serializer,
                new MessageFactory(Services.GetRequiredService<DeepCopier>(), NullLogger<MessageFactory>.Instance, trace),
                Substitute.For<IMessageCenter>(), messaging);
            var connectionServices = Substitute.For<IServiceProvider>();
            connectionServices.GetService(typeof(MessageHandlerShared)).Returns(Shared);
            var common = new ConnectionCommon(connectionServices, Shared.MessageFactory, trace, Shared.ConnectionTrace,
                messaging, new NetworkingInstruments(instruments), new NoOpMessageStatisticsSink());
            var transport = Substitute.For<MessageTransport>();
            transport.Closed.Returns(CancellationToken.None);
            Connection = new TestConnection(transport, common, Shared.MessageCenter);
            Instruments = new ApplicationRequestInstruments(instruments);
        }
        internal ServiceProvider Services { get; }
        internal MessageHandlerShared Shared { get; }
        internal TestConnection Connection { get; }
        internal CompletionSource Completion { get; } = new();
        private ApplicationRequestInstruments Instruments { get; }
        internal MessageSerializer Serializer() => new(Services.GetRequiredService<SerializerSessionPool>(), new SiloMessagingOptions());
        internal Message Message(IInvokable request, bool oneWay = false) => new()
        { BodyObject = request, Direction = oneWay ? Orleans.Runtime.Message.Directions.OneWay : Orleans.Runtime.Message.Directions.Request,
            Id = new CorrelationId(17), TargetGrain = GrainId.Create("owned-rpc", "target") };
        internal CallbackData Callback(Message message) => new(new SharedCallbackData(_ => { },
            NullLogger<CallbackData>.Instance, TimeProvider.System, TimeSpan.FromSeconds(1), false, false, null),
            Completion, message, Instruments);
        public void Dispose() { Shared.Dispose(); Services.Dispose(); }
    }

    private sealed class TestConnection(MessageTransport transport, ConnectionCommon shared, IMessageCenter center) : Connection(transport, shared)
    {
        internal TaskCompletionSource<Message> Retried { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override ConnectionDirection ConnectionDirection => ConnectionDirection.SiloToSilo;
        protected override TimeSpan CloseConnectionTimeout => TimeSpan.FromSeconds(1);
        protected override IMessageCenter MessageCenter => center;
        protected override bool PrepareMessageForSend(Message message) => true;
        protected override void RetryMessage(Message message, Exception? error = null) => Retried.TrySetResult(message);
        protected internal override void OnReceivedMessage(Message message) { }
        protected internal override void RecordMessageReceive(Message message, int totalBytes, int headerBytes) { }
        protected internal override void RecordMessageSend(Message message, int totalBytes, int headerBytes) { }
    }
}

public interface IOwnedTransportCalls : IGrainWithIntegerKey
{
    Task Owned([DisposeOnCompletion] ArcBuffer buffer);
    Task Blocked([DisposeOnCompletion] OwnedTransportArgument argument);
}

public sealed class OwnedTransportArgument(ArcBuffer buffer) : IDisposable
{
    public ArcBuffer Buffer = buffer;
    public bool BlockWriter;
    public Exception? WriterError;
    public int DisposeCount;
    public Exception? DisposeError;
    public TaskCompletionSource WriterEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource WriterRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Dispose()
    {
        DisposeCount++;
        Buffer.Dispose();
        Buffer = default;
        if (DisposeError is { } error) throw error;
    }
}

[RegisterCopier]
internal sealed class OwnedTransportArgumentCopier : IDeepCopier<OwnedTransportArgument>
{
    [return: NotNullIfNotNull(nameof(input))]
    public OwnedTransportArgument? DeepCopy(OwnedTransportArgument? input, CopyContext context) => input is null ? null : new(input.Buffer.Slice(0))
    { BlockWriter = input.BlockWriter, WriterError = input.WriterError };
}

[RegisterSerializer]
internal sealed class OwnedTransportArgumentCodec : IFieldCodec<OwnedTransportArgument>
{
    private readonly ArcBufferCodec _buffers = new();
    public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type? expectedType, OwnedTransportArgument? value)
        where TBufferWriter : IBufferWriter<byte>
    {
        ArgumentNullException.ThrowIfNull(value);
        value.WriterEntered.TrySetResult();
        if (value.BlockWriter) value.WriterRelease.Task.WaitAsync(TimeSpan.FromSeconds(60)).GetAwaiter().GetResult();
        if (value.WriterError is { } error) throw error;
        ReferenceCodec.MarkValueField(writer.Session);
        writer.WriteFieldHeader(fieldIdDelta, expectedType, typeof(OwnedTransportArgument), WireType.TagDelimited);
        _buffers.WriteField(ref writer, 0, typeof(ArcBuffer), value.Buffer);
        writer.WriteEndObject();
    }
    public OwnedTransportArgument ReadValue<TInput>(ref Reader<TInput> reader, Field field)
    {
        ReferenceCodec.MarkValueField(reader.Session);
        field.EnsureWireTypeTagDelimited();
        var buffer = _buffers.ReadValue(ref reader, reader.ReadFieldHeader());
        try
        {
            var end = reader.ReadFieldHeader();
            if (!end.IsEndBaseOrEndObject) throw new InvalidOperationException("Unexpected owned argument field.");
            return new(buffer);
        }
        catch { buffer.Dispose(); throw; }
    }
}
