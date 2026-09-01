using System.Runtime.ExceptionServices;
using NSubstitute;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;
using Xunit;

namespace UnitTests.StreamingTests;

public class StreamSubscriptionHandleImplTests
{
    [Fact]
    public void EarliestAvailableCreatesStartPositionHandshake()
    {
        var stream = CreateStream(isRewindable: true);
        var handle = new StreamSubscriptionHandleImpl<int>(
            CreateSubscriptionId(implicitSubscription: false),
            Substitute.For<IAsyncObserver<int>>(),
            batchObserver: null,
            stream,
            token: null,
            StreamSubscriptionStartPosition.EarliestAvailable,
            filterData: null);

        Assert.IsType<StartPositionToken>(handle.GetSequenceToken());
    }

    [Fact]
    public void ExplicitTokenRemainsInclusiveAndAuthoritative()
    {
        var token = new EventSequenceTokenV2(10);
        var stream = CreateStream(isRewindable: true);
        var handle = new StreamSubscriptionHandleImpl<int>(
            CreateSubscriptionId(implicitSubscription: false),
            Substitute.For<IAsyncObserver<int>>(),
            batchObserver: null,
            stream,
            token,
            default,
            filterData: null);

        var startToken = Assert.IsType<StartToken>(handle.GetSequenceToken());
        Assert.Equal(token, startToken.Token);
    }

    [Fact]
    public void InvalidStartPositionIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ((StreamSubscriptionStartPosition)42).Validate());
    }

    [Fact]
    public async Task CustomObservableUsesLatestDefaultInterfaceBehavior()
    {
        var observable = new LegacyObservable();
        IAsyncObservable<int> observableInterface = observable;
        var observer = Substitute.For<IAsyncObserver<int>>();

        await observableInterface.SubscribeAsync(observer, StreamSubscriptionStartPosition.Latest);

        Assert.True(observable.TokenOverloadCalled);
        Assert.Null(observable.Token);
    }

    [Fact]
    public async Task DefaultLiteralContinuesToSelectTokenOverload()
    {
        var itemObservable = new LegacyObservable();
        IAsyncObservable<int> itemObservableInterface = itemObservable;
        await itemObservableInterface.SubscribeAsync(Substitute.For<IAsyncObserver<int>>(), default);
        Assert.True(itemObservable.TokenOverloadCalled);

        var batchObservable = new LegacyBatchObservable();
        IAsyncBatchObservable<int> batchObservableInterface = batchObservable;
        await batchObservableInterface.SubscribeAsync(Substitute.For<IAsyncBatchObserver<int>>(), default);
        Assert.True(batchObservable.TokenOverloadCalled);
    }

    [Fact]
    public async Task CustomObservableRejectsUnsupportedEarliestPosition()
    {
        IAsyncObservable<int> observable = new LegacyObservable();

        await Assert.ThrowsAsync<NotSupportedException>(
            () => observable.SubscribeAsync(
                Substitute.For<IAsyncObserver<int>>(),
                StreamSubscriptionStartPosition.EarliestAvailable));
    }

    [Fact]
    public async Task CustomBatchObservableUsesLatestDefaultInterfaceBehavior()
    {
        var observable = new LegacyBatchObservable();
        IAsyncBatchObservable<int> observableInterface = observable;
        var observer = Substitute.For<IAsyncBatchObserver<int>>();

        await observableInterface.SubscribeAsync(observer, StreamSubscriptionStartPosition.Latest);

        Assert.True(observable.TokenOverloadCalled);
        Assert.Null(observable.Token);
    }

    [Fact]
    public async Task CustomBatchObservableRejectsUnsupportedEarliestPosition()
    {
        IAsyncBatchObservable<int> observable = new LegacyBatchObservable();

        await Assert.ThrowsAsync<NotSupportedException>(
            () => observable.SubscribeAsync(
                Substitute.For<IAsyncBatchObserver<int>>(),
                StreamSubscriptionStartPosition.EarliestAvailable));
    }

    [Fact]
    public async Task ItemSubscribeOverloadForwardsToStreamConsumer()
    {
        var consumer = new RecordingInternalObservable();
        IAsyncObservable<int> stream = CreateStream(isRewindable: true, consumer);
        var observer = Substitute.For<IAsyncObserver<int>>();

        await stream.SubscribeAsync(
            observer,
            StreamSubscriptionStartPosition.EarliestAvailable,
            filterData: "filter");

        Assert.Same(observer, consumer.ItemObserver);
        Assert.Equal(StreamSubscriptionStartPosition.EarliestAvailable, consumer.StartPosition);
        Assert.Equal("filter", consumer.FilterData);
    }

    [Fact]
    public async Task BatchSubscribeOverloadForwardsToStreamConsumer()
    {
        var consumer = new RecordingInternalObservable();
        IAsyncBatchObservable<int> stream = CreateStream(isRewindable: true, consumer);
        var observer = Substitute.For<IAsyncBatchObserver<int>>();

        await stream.SubscribeAsync(observer, StreamSubscriptionStartPosition.EarliestAvailable);

        Assert.Same(observer, consumer.BatchObserver);
        Assert.Equal(StreamSubscriptionStartPosition.EarliestAvailable, consumer.StartPosition);
    }

    [Fact]
    public void ActiveImplicitSubscriptionRejectsOlderAcknowledgedToken()
    {
        var acknowledgedToken = new EventSequenceTokenV2(10);
        var subscriptionId = CreateSubscriptionId(implicitSubscription: true);

        var exception = Assert.Throws<InvalidOperationException>(
            () => StreamSubscriptionHandleImpl<int>.ValidateResumeToken(
                subscriptionId,
                hasObserver: true,
                StreamHandshakeToken.CreateDeliveyToken(acknowledgedToken),
                new EventSequenceTokenV2(9)));

        Assert.Contains("Implicit subscriptions advance monotonically", exception.Message);
    }

    [Fact]
    public void ActiveImplicitSubscriptionRejectsNewerTokenAfterAcknowledgement()
    {
        var acknowledgedToken = new EventSequenceTokenV2(10);
        var subscriptionId = CreateSubscriptionId(implicitSubscription: true);

        Assert.Throws<InvalidOperationException>(
            () => StreamSubscriptionHandleImpl<int>.ValidateResumeToken(
                subscriptionId,
                hasObserver: true,
                StreamHandshakeToken.CreateDeliveyToken(acknowledgedToken),
                new EventSequenceTokenV2(11)));
    }

    [Fact]
    public void ActiveImplicitSubscriptionAllowsObserverReplacementWithoutToken()
    {
        StreamSubscriptionHandleImpl<int>.ValidateResumeToken(
            CreateSubscriptionId(implicitSubscription: true),
            hasObserver: true,
            StreamHandshakeToken.CreateDeliveyToken(new EventSequenceTokenV2(10)),
            token: null);
    }

    [Fact]
    public void ActiveExplicitSubscriptionAllowsOlderAcknowledgedToken()
    {
        StreamSubscriptionHandleImpl<int>.ValidateResumeToken(
            CreateSubscriptionId(implicitSubscription: false),
            hasObserver: true,
            StreamHandshakeToken.CreateDeliveyToken(new EventSequenceTokenV2(10)),
            new EventSequenceTokenV2(9));
    }

    [Fact]
    public void ReconstructedImplicitSubscriptionAllowsRecoveryToken()
    {
        StreamSubscriptionHandleImpl<int>.ValidateResumeToken(
            CreateSubscriptionId(implicitSubscription: true),
            hasObserver: false,
            StreamHandshakeToken.CreateDeliveyToken(new EventSequenceTokenV2(10)),
            new EventSequenceTokenV2(9));
    }

    private static GuidId CreateSubscriptionId(bool implicitSubscription)
    {
        var subscriptionGuid = implicitSubscription
            ? SubscriptionMarker.MarkAsImplictSubscriptionId(Guid.NewGuid())
            : SubscriptionMarker.MarkAsExplicitSubscriptionId(Guid.NewGuid());
        return GuidId.GetGuidId(subscriptionGuid);
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [TestArea("Streaming")]
    [Fact]
    public async Task DeliverItem_AcknowledgedDuplicate_ReturnsExpectedTokenWithoutRedelivery()
    {
        var token = new EventSequenceTokenV2(42, 3);
        var expectedToken = StreamHandshakeToken.CreateDeliveyToken(token);
        var handshakeState = new StreamSubscriptionHandleImpl<int>.SharedHandshakeState { Token = expectedToken };
        var observer = new RecordingObserver<int>();
        var fixture = CreateFixture(
            isRewindable: true,
            observer: observer,
            handshakeState: handshakeState);
        var diagnostics = new RecordingStreamingEventObserver(fixture.Handle.ProviderName);
        using var subscription = Orleans.Streaming.Diagnostics.StreamingEvents.AllEvents.Subscribe(diagnostics);

        var result = await fixture.Handle.DeliverItem(17, token, expectedToken);

        Assert.Same(expectedToken, result);
        Assert.Same(expectedToken, fixture.Handle.GetSequenceToken());
        Assert.Empty(observer.Items);
        Assert.Empty(diagnostics.Items);
        Assert.Equal(0, fixture.Provider.ConsumerInterfaceAcquisitions);
        Assert.Equal(0, fixture.Provider.ProducerInterfaceAcquisitions);
    }

    private static HandleFixture CreateFixture(
        bool isRewindable,
        IAsyncObserver<int>? observer = null,
        IAsyncBatchObserver<int>? batchObserver = null,
        StreamSequenceToken? token = null,
        StreamSubscriptionOptions options = default,
        string? filterData = "phase-1-filter",
        bool disableHandshake = false,
        StreamSubscriptionHandleImpl<int>.SharedHandshakeState? handshakeState = null,
        GuidId? subscriptionId = null,
        StreamId? streamId = null,
        string? providerName = null,
        string clusterId = "phase-1-cluster")
    {
        var provider = new CountingStreamProvider<int>();
        var actualStreamId = streamId ?? StreamId.Create("phase-1", Guid.NewGuid());
        var actualProviderName = providerName ?? $"phase-1-provider-{Guid.NewGuid():N}";
        var qualifiedStreamId = new QualifiedStreamId(actualProviderName, actualStreamId);
        var runtimeClient = NSubstitute.Substitute.For<IRuntimeClient>();
        var stream = new StreamImpl<int>(qualifiedStreamId, provider, isRewindable, runtimeClient);
        var handle = new StreamSubscriptionHandleImpl<int>(
            subscriptionId ?? CreateSubscriptionId(implicitSubscription: false),
            observer,
            batchObserver,
            stream,
            token,
            options,
            filterData,
            disableHandshake,
            handshakeState,
            siloAddress: null,
            clusterId);
        return new(handle, stream, provider);
    }

    private sealed record HandleFixture(
        StreamSubscriptionHandleImpl<int> Handle,
        StreamImpl<int> Stream,
        CountingStreamProvider<int> Provider);

    private sealed class CountingStreamProvider<T> : IInternalStreamProvider
    {
        public CountingStreamProvider()
        {
            Consumer = new RecordingConsumer<T>();
            Producer = new NoOpProducer<T>();
        }

        public int ConsumerInterfaceAcquisitions { get; private set; }
        public int ProducerInterfaceAcquisitions { get; private set; }
        public RecordingConsumer<T> Consumer { get; }
        public NoOpProducer<T> Producer { get; }

        IInternalAsyncObservable<TRequested> IInternalStreamProvider.GetConsumerInterface<TRequested>(
            IAsyncStream<TRequested> streamId)
        {
            ConsumerInterfaceAcquisitions++;
            Assert.Equal(typeof(T), typeof(TRequested));
            return (IInternalAsyncObservable<TRequested>)(object)Consumer;
        }

        IInternalAsyncBatchObserver<TRequested> IInternalStreamProvider.GetProducerInterface<TRequested>(
            IAsyncStream<TRequested> streamId)
        {
            ProducerInterfaceAcquisitions++;
            Assert.Equal(typeof(T), typeof(TRequested));
            return (IInternalAsyncBatchObserver<TRequested>)(object)Producer;
        }
    }

    private sealed class NoOpProducer<T> : IInternalAsyncBatchObserver<T>
    {
        public Task Cleanup() => Task.CompletedTask;
        public Task OnCompletedAsync() => Task.CompletedTask;
        public Task OnErrorAsync(Exception ex) => Task.CompletedTask;
        public Task OnNextAsync(T item, StreamSequenceToken? token = null) => Task.CompletedTask;
        public Task OnNextBatchAsync(IEnumerable<T> batch, StreamSequenceToken? token = null) => Task.CompletedTask;
    }

    private sealed class RecordingConsumer<T> : IInternalAsyncObservable<T>
    {
        public List<StreamSubscriptionHandle<T>> ResumeHandles { get; } = [];
        public List<IAsyncObserver<T>> ResumeObservers { get; } = [];
        public List<IAsyncBatchObserver<T>> ResumeBatchObservers { get; } = [];
        public List<StreamSequenceToken?> ResumeTokens { get; } = [];
        public List<StreamSubscriptionHandle<T>> UnsubscribeHandles { get; } = [];
        public StreamSubscriptionHandle<T>? Replacement { get; set; }
        public Exception? ResumeException { get; set; }
        public Exception? UnsubscribeException { get; set; }

        public Task Cleanup() => Task.CompletedTask;

        public Task<IList<StreamSubscriptionHandle<T>>> GetAllSubscriptions()
            => Task.FromResult<IList<StreamSubscriptionHandle<T>>>([]);

        public Task<StreamSubscriptionHandle<T>> ResumeAsync(
            StreamSubscriptionHandle<T> handle,
            IAsyncObserver<T> observer,
            StreamSequenceToken? token = null)
        {
            ResumeHandles.Add(handle);
            ResumeObservers.Add(observer);
            ResumeTokens.Add(token);
            return ResumeException is null
                ? Task.FromResult(Replacement!)
                : Task.FromException<StreamSubscriptionHandle<T>>(ResumeException);
        }

        public Task<StreamSubscriptionHandle<T>> ResumeAsync(
            StreamSubscriptionHandle<T> handle,
            IAsyncBatchObserver<T> observer,
            StreamSequenceToken? token = null)
        {
            ResumeHandles.Add(handle);
            ResumeBatchObservers.Add(observer);
            ResumeTokens.Add(token);
            return ResumeException is null
                ? Task.FromResult(Replacement!)
                : Task.FromException<StreamSubscriptionHandle<T>>(ResumeException);
        }

        public Task<StreamSubscriptionHandle<T>> SubscribeAsync(IAsyncObserver<T> observer)
            => throw new NotSupportedException();

        public Task<StreamSubscriptionHandle<T>> SubscribeAsync(
            IAsyncObserver<T> observer,
            StreamSequenceToken? token,
            string? filterData = null)
            => throw new NotSupportedException();

        public Task<StreamSubscriptionHandle<T>> SubscribeAsync(IAsyncBatchObserver<T> observer)
            => throw new NotSupportedException();

        public Task<StreamSubscriptionHandle<T>> SubscribeAsync(
            IAsyncBatchObserver<T> observer,
            StreamSequenceToken? token)
            => throw new NotSupportedException();

        public Task UnsubscribeAsync(StreamSubscriptionHandle<T> handle)
        {
            UnsubscribeHandles.Add(handle);
            return UnsubscribeException is null
                ? Task.CompletedTask
                : Task.FromException(UnsubscribeException);
        }
    }

    private sealed class RecordingObserver<T> : IAsyncObserver<T>
    {
        private int nextCallCount;

        public List<T> Items { get; } = [];
        public List<StreamSequenceToken?> Tokens { get; } = [];
        public List<object?> RequestContexts { get; } = [];
        public List<Exception> Errors { get; } = [];
        public List<string> Timeline { get; } = [];
        public int? FailOnNextCall { get; set; }
        public Exception? NextException { get; set; }
        public Exception? CompletionException { get; set; }
        public Exception? ErrorCallbackException { get; set; }
        public int CompletionCalls { get; private set; }

        public Task OnNextAsync(T item, StreamSequenceToken? token = null)
        {
            nextCallCount++;
            Items.Add(item);
            Tokens.Add(token);
            RequestContexts.Add(RequestContext.Get(RequestContextKey));
            Timeline.Add($"observer-attempt:{item}");
            if (nextCallCount == FailOnNextCall)
            {
                return Task.FromException(NextException!);
            }

            Timeline.Add($"observer-accepted:{item}");
            return Task.CompletedTask;
        }

        public Task OnCompletedAsync()
        {
            CompletionCalls++;
            return CompletionException is null
                ? Task.CompletedTask
                : Task.FromException(CompletionException);
        }

        public Task OnErrorAsync(Exception ex)
        {
            Errors.Add(ex);
            return ErrorCallbackException is null
                ? Task.CompletedTask
                : Task.FromException(ErrorCallbackException);
        }
    }

    private const string RequestContextKey = "stream-subscription-handle-phase-1";

    [Fact, TestSuite("BVT"), TestProvider("None"), TestArea("Streaming")]
    public void Constructor_RewindableHandle_PreservesExactIdentityAndStartToken()
    {
        var subscriptionId = CreateSubscriptionId(implicitSubscription: false);
        var streamId = StreamId.Create("orders", "rewindable");
        var startToken = new EventSequenceTokenV2(17, 4);
        var observer = new RecordingObserver<int>();
        var fixture = CreateFixture(
            isRewindable: true,
            observer: observer,
            token: startToken,
            filterData: "region=west",
            subscriptionId: subscriptionId,
            streamId: streamId,
            providerName: "rewindable-provider");

        Assert.True(fixture.Handle.IsValid);
        Assert.True(fixture.Handle.IsRewindable);
        Assert.True(fixture.Handle.HasObserver);
        Assert.Same(subscriptionId, fixture.Handle.SubscriptionId);
        Assert.Equal(subscriptionId.Guid, fixture.Handle.HandleId);
        Assert.Equal("rewindable-provider", fixture.Handle.ProviderName);
        Assert.Equal(streamId, fixture.Handle.StreamId);
        Assert.Equal("region=west", fixture.Handle.FilterData);
        Assert.True(fixture.Handle.SameStreamId(fixture.Stream.InternalStreamId));
        var handshake = Assert.IsType<StartToken>(fixture.Handle.GetSequenceToken());
        Assert.Same(startToken, handshake.Token);
        AssertNoProviderAcquisitions(fixture);
    }

    [Fact, TestSuite("BVT"), TestProvider("None"), TestArea("Streaming")]
    public void Constructor_NonRewindableHandle_HasNoStartToken()
    {
        var subscriptionId = CreateSubscriptionId(implicitSubscription: false);
        var streamId = StreamId.Create("orders", "non-rewindable");
        var suppliedToken = new EventSequenceTokenV2(23, 2);
        var batchObserver = new RecordingBatchObserver<int>();
        var fixture = CreateFixture(
            isRewindable: false,
            batchObserver: batchObserver,
            token: suppliedToken,
            filterData: "priority",
            subscriptionId: subscriptionId,
            streamId: streamId,
            providerName: "non-rewindable-provider");

        Assert.True(fixture.Handle.IsValid);
        Assert.False(fixture.Handle.IsRewindable);
        Assert.True(fixture.Handle.HasObserver);
        Assert.Same(subscriptionId, fixture.Handle.SubscriptionId);
        Assert.Equal(subscriptionId.Guid, fixture.Handle.HandleId);
        Assert.Equal("non-rewindable-provider", fixture.Handle.ProviderName);
        Assert.Equal(streamId, fixture.Handle.StreamId);
        Assert.Equal("priority", fixture.Handle.FilterData);
        Assert.Null(fixture.Handle.GetSequenceToken());
        AssertNoProviderAcquisitions(fixture);
    }

    [Fact, TestSuite("BVT"), TestProvider("None"), TestArea("Streaming")]
    public void Constructor_DisabledRewind_HasNoStartToken()
    {
        var suppliedToken = new EventSequenceTokenV2(29, 7);
        var fixture = CreateFixture(
            isRewindable: true,
            token: suppliedToken,
            disableHandshake: true);

        Assert.True(fixture.Handle.IsValid);
        Assert.False(fixture.Handle.IsRewindable);
        Assert.Null(fixture.Handle.GetSequenceToken());
        AssertNoProviderAcquisitions(fixture);
    }

    [Fact, TestSuite("BVT"), TestProvider("None"), TestArea("Streaming")]
    public async Task HandleId_RemainsStableAcrossDispatchAndTerminalCallbacks()
    {
        var dispatchId = CreateSubscriptionId(implicitSubscription: false);
        var dispatchObserver = new RecordingObserver<int>();
        var dispatch = CreateFixture(
            isRewindable: true,
            observer: dispatchObserver,
            token: new EventSequenceTokenV2(30),
            subscriptionId: dispatchId);
        var dispatchToken = new EventSequenceTokenV2(31, 1);
        await dispatch.Handle.DeliverItem(31, dispatchToken, dispatch.Handle.GetSequenceToken());

        var completionId = CreateSubscriptionId(implicitSubscription: false);
        var completionObserver = new RecordingObserver<int>();
        var completion = CreateFixture(
            isRewindable: false,
            observer: completionObserver,
            subscriptionId: completionId);
        await completion.Handle.CompleteStream();

        var errorId = CreateSubscriptionId(implicitSubscription: false);
        var errorObserver = new RecordingBatchObserver<int>();
        var error = CreateFixture(
            isRewindable: false,
            batchObserver: errorObserver,
            subscriptionId: errorId);
        var streamError = new InvalidOperationException("terminal");
        await error.Handle.ErrorInStream(streamError);

        Assert.Equal(dispatchId.Guid, dispatch.Handle.HandleId);
        Assert.Equal(completionId.Guid, completion.Handle.HandleId);
        Assert.Equal(errorId.Guid, error.Handle.HandleId);
        Assert.Equal([31], dispatchObserver.Items);
        Assert.Equal(1, completionObserver.CompletionCalls);
        Assert.Same(streamError, Assert.Single(errorObserver.Errors));
        AssertNoProviderAcquisitions(dispatch);
        AssertNoProviderAcquisitions(completion);
        AssertNoProviderAcquisitions(error);
    }

    [Fact, TestSuite("BVT"), TestProvider("None"), TestArea("Streaming")]
    public void EqualityAndHashCode_AreBasedOnlyOnSubscriptionId()
    {
        var sharedId = CreateSubscriptionId(implicitSubscription: false);
        var firstFixture = CreateFixture(
            isRewindable: false,
            subscriptionId: sharedId,
            streamId: StreamId.Create("first", "stream"));
        var sameIdFixture = CreateFixture(
            isRewindable: true,
            subscriptionId: sharedId,
            streamId: StreamId.Create("second", "stream"));
        var differentIdFixture = CreateFixture(
            isRewindable: false,
            subscriptionId: CreateSubscriptionId(implicitSubscription: false));
        var first = firstFixture.Handle;
        var sameIdDifferentStream = sameIdFixture.Handle;
        var differentId = differentIdFixture.Handle;
        var foreign = new ForeignSubscriptionHandle(first.StreamId);

        Assert.Equal(first, sameIdDifferentStream);
        Assert.True(first.Equals((object)sameIdDifferentStream));
        Assert.Equal(first.GetHashCode(), sameIdDifferentStream.GetHashCode());
        Assert.NotEqual(first, differentId);
        Assert.False(first.Equals((StreamSubscriptionHandle<int>?)null));
        Assert.False(first.Equals(foreign));
        Assert.False(first.Equals(new object()));
        AssertNoProviderAcquisitions(firstFixture);
        AssertNoProviderAcquisitions(sameIdFixture);
        AssertNoProviderAcquisitions(differentIdFixture);
    }

    [Fact, TestSuite("BVT"), TestProvider("None"), TestArea("Streaming")]
    public void ToString_ReportsExactValidAndInvalidIdentity()
    {
        var subscriptionId = CreateSubscriptionId(implicitSubscription: false);
        var fixture = CreateFixture(
            isRewindable: false,
            subscriptionId: subscriptionId,
            streamId: StreamId.Create("format", "identity"),
            providerName: "format-provider");
        var expectedStreamIdentity = fixture.Stream.InternalStreamId.ToString();

        Assert.Equal(
            $"StreamSubscriptionHandleImpl:Stream={expectedStreamIdentity},HandleId={subscriptionId.Guid}",
            fixture.Handle.ToString());

        fixture.Handle.Invalidate();

        Assert.Equal(
            $"StreamSubscriptionHandleImpl:Stream=null,HandleId={subscriptionId.Guid}",
            fixture.Handle.ToString());
        AssertNoProviderAcquisitions(fixture);
    }

    [Fact, TestSuite("BVT"), TestProvider("None"), TestArea("Streaming")]
    public void ValidateResumeToken_NonDeliveryExpectedToken_ReturnsExpectedToken()
    {
        var startToken = new EventSequenceTokenV2(40, 2);
        var fixture = CreateFixture(
            isRewindable: true,
            observer: new RecordingObserver<int>(),
            token: startToken,
            subscriptionId: CreateSubscriptionId(implicitSubscription: true));
        var expected = fixture.Handle.GetSequenceToken();

        fixture.Handle.ValidateResumeToken(new EventSequenceTokenV2(39, 9));

        Assert.Same(expected, fixture.Handle.GetSequenceToken());
        Assert.Same(startToken, Assert.IsType<StartToken>(expected).Token);
        AssertNoProviderAcquisitions(fixture);
    }

    [Fact, TestSuite("BVT"), TestProvider("None"), TestArea("Streaming")]
    public void ValidateResumeToken_WithoutObserver_ReturnsExpectedToken()
    {
        var acknowledgedToken = new EventSequenceTokenV2(41, 3);
        var expected = StreamHandshakeToken.CreateDeliveyToken(acknowledgedToken);
        var fixture = CreateFixture(
            isRewindable: true,
            handshakeState: new StreamSubscriptionHandleImpl<int>.SharedHandshakeState { Token = expected },
            subscriptionId: CreateSubscriptionId(implicitSubscription: true));

        fixture.Handle.ValidateResumeToken(new EventSequenceTokenV2(40, 8));

        Assert.False(fixture.Handle.HasObserver);
        Assert.Same(expected, fixture.Handle.GetSequenceToken());
        Assert.Same(acknowledgedToken, Assert.IsType<DeliveryToken>(expected).Token);
        AssertNoProviderAcquisitions(fixture);
    }

    [Fact, TestSuite("BVT"), TestProvider("None"), TestArea("Streaming")]
    public void ValidateResumeToken_ExplicitSubscriptionId_ReturnsExpectedToken()
    {
        var subscriptionId = CreateSubscriptionId(implicitSubscription: false);
        var acknowledgedToken = new EventSequenceTokenV2(43, 5);
        var expected = StreamHandshakeToken.CreateDeliveyToken(acknowledgedToken);
        var fixture = CreateFixture(
            isRewindable: true,
            observer: new RecordingObserver<int>(),
            handshakeState: new StreamSubscriptionHandleImpl<int>.SharedHandshakeState { Token = expected },
            subscriptionId: subscriptionId);

        fixture.Handle.ValidateResumeToken(new EventSequenceTokenV2(42, 1));

        Assert.Same(subscriptionId, fixture.Handle.SubscriptionId);
        Assert.Equal(subscriptionId.Guid, fixture.Handle.HandleId);
        Assert.Same(expected, fixture.Handle.GetSequenceToken());
        AssertNoProviderAcquisitions(fixture);
    }

    [Fact, TestSuite("BVT"), TestProvider("None"), TestArea("Streaming")]
    public void ValidateResumeToken_NullToken_UsesTheDocumentedInitialHandshake()
    {
        var acknowledgedToken = new EventSequenceTokenV2(44, 6);
        var expected = StreamHandshakeToken.CreateDeliveyToken(acknowledgedToken);
        var fixture = CreateFixture(
            isRewindable: true,
            observer: new RecordingObserver<int>(),
            handshakeState: new StreamSubscriptionHandleImpl<int>.SharedHandshakeState { Token = expected },
            subscriptionId: CreateSubscriptionId(implicitSubscription: true));

        fixture.Handle.ValidateResumeToken(token: null);

        Assert.Same(expected, fixture.Handle.GetSequenceToken());
        Assert.Same(acknowledgedToken, Assert.IsType<DeliveryToken>(fixture.Handle.GetSequenceToken()).Token);
        AssertNoProviderAcquisitions(fixture);
    }

    [Fact, TestSuite("BVT"), TestProvider("None"), TestArea("Streaming")]
    public async Task DeliverItem_MatchingHandshake_ForwardsExactItemAndTokenThenAcknowledges()
    {
        var startToken = new EventSequenceTokenV2(50);
        var deliveredToken = new EventSequenceTokenV2(51, 2);
        var observer = new RecordingObserver<int>();
        var fixture = CreateFixture(isRewindable: true, observer: observer, token: startToken);
        var diagnostics = new RecordingStreamingEventObserver(fixture.Handle.ProviderName, observer.Timeline);
        using var subscription = Orleans.Streaming.Diagnostics.StreamingEvents.AllEvents.Subscribe(diagnostics);
        var expectedHandshake = fixture.Handle.GetSequenceToken();

        var result = await fixture.Handle.DeliverItem(5102, deliveredToken, expectedHandshake);

        Assert.Null(result);
        Assert.Equal([5102], observer.Items);
        Assert.Same(deliveredToken, Assert.Single(observer.Tokens));
        Assert.Equal(
            ["observer-attempt:5102", "observer-accepted:5102", "diagnostic:51:2"],
            observer.Timeline);
        AssertDeliveryHandshake(fixture.Handle, deliveredToken);
        var diagnostic = Assert.Single(diagnostics.Items);
        Assert.Equal(fixture.Handle.ProviderName, diagnostic.StreamProvider);
        Assert.Equal(fixture.Handle.StreamId, diagnostic.StreamId);
        Assert.Equal(fixture.Handle.HandleId, diagnostic.SubscriptionId);
        Assert.Equal("phase-1-cluster", diagnostic.ClusterId);
        Assert.Same(deliveredToken, diagnostic.SequenceToken);
        Assert.Null(diagnostic.SiloAddress);
        AssertNoProviderAcquisitions(fixture);
    }

    [Fact, TestSuite("BVT"), TestProvider("None"), TestArea("Streaming")]
    public async Task DeliverItem_EventIndexedTokens_PreserveCallOrderAndTokenIdentity()
    {
        var token0 = new EventSequenceTokenV2(60, 0);
        var token1 = new EventSequenceTokenV2(60, 1);
        var token2 = new EventSequenceTokenV2(60, 2);
        var observer = new RecordingObserver<string>();
        var fixture = CreateStringFixture(observer, token0);

        await fixture.Handle.DeliverItem("zero", token0, fixture.Handle.GetSequenceToken());
        await fixture.Handle.DeliverItem("one", token1, fixture.Handle.GetSequenceToken());
        await fixture.Handle.DeliverItem("two", token2, fixture.Handle.GetSequenceToken());

        Assert.Equal(["zero", "one", "two"], observer.Items);
        Assert.Collection(
            observer.Tokens,
            actual => Assert.Same(token0, actual),
            actual => Assert.Same(token1, actual),
            actual => Assert.Same(token2, actual));
        Assert.Equal([60L, 60L, 60L], observer.Tokens.Select(token => token!.SequenceNumber));
        Assert.Equal([0, 1, 2], observer.Tokens.Select(token => token!.EventIndex));
        AssertDeliveryHandshake(fixture.Handle, token2);
        AssertNoProviderAcquisitions(fixture);
    }

    [Fact, TestSuite("BVT"), TestProvider("None"), TestArea("Streaming")]
    public async Task DeliverBatch_ItemObserver_PreservesItemOrderAndEventIndexedTokenIdentity()
    {
        var startToken = new EventSequenceTokenV2(69);
        var token0 = new EventSequenceTokenV2(70, 0);
        var token1 = new EventSequenceTokenV2(70, 1);
        var token2 = new EventSequenceTokenV2(70, 2);
        var observer = new RecordingObserver<int>();
        var fixture = CreateFixture(isRewindable: true, observer: observer, token: startToken);
        var batch = new TestBatchContainer(
            fixture.Handle.StreamId,
            token2,
            (700, token0),
            (701, token1),
            (702, token2));

        var result = await fixture.Handle.DeliverBatch(batch, fixture.Handle.GetSequenceToken());

        Assert.Null(result);
        Assert.Equal([700, 701, 702], observer.Items);
        Assert.Collection(
            observer.Tokens,
            actual => Assert.Same(token0, actual),
            actual => Assert.Same(token1, actual),
            actual => Assert.Same(token2, actual));
        Assert.Equal([70L, 70L, 70L], observer.Tokens.Select(token => token!.SequenceNumber));
        Assert.Equal([0, 1, 2], observer.Tokens.Select(token => token!.EventIndex));
        AssertDeliveryHandshake(fixture.Handle, token2);
        AssertNoProviderAcquisitions(fixture);
    }

    [Fact, TestSuite("BVT"), TestProvider("None"), TestArea("Streaming")]
    public async Task DeliverBatch_BatchObserver_PreservesSequentialItemsAndTokenIdentity()
    {
        var startToken = new EventSequenceTokenV2(79);
        var token0 = new EventSequenceTokenV2(80, 0);
        var token1 = new EventSequenceTokenV2(80, 1);
        var token2 = new EventSequenceTokenV2(80, 2);
        var observer = new RecordingBatchObserver<int>();
        var fixture = CreateFixture(isRewindable: true, batchObserver: observer, token: startToken);
        var batch = new TestBatchContainer(
            fixture.Handle.StreamId,
            token2,
            (800, token0),
            (801, token1),
            (802, token2));

        var result = await fixture.Handle.DeliverBatch(batch, fixture.Handle.GetSequenceToken());

        Assert.Null(result);
        var delivered = Assert.Single(observer.Batches);
        Assert.Equal([800, 801, 802], delivered.Select(item => item.Item));
        Assert.Collection(
            delivered,
            item => Assert.Same(token0, item.Token),
            item => Assert.Same(token1, item.Token),
            item => Assert.Same(token2, item.Token));
        Assert.Equal([80L, 80L, 80L], delivered.Select(item => item.Token.SequenceNumber));
        Assert.Equal([0, 1, 2], delivered.Select(item => item.Token.EventIndex));
        AssertDeliveryHandshake(fixture.Handle, token2);
        AssertNoProviderAcquisitions(fixture);
    }

    [Fact, TestSuite("BVT"), TestProvider("None"), TestArea("Streaming")]
    public async Task DeliverBatch_EmptyBatch_DoesNotInvokeBatchObserver()
    {
        var startToken = new EventSequenceTokenV2(89);
        var batchToken = new EventSequenceTokenV2(90);
        var observer = new RecordingBatchObserver<int>();
        var fixture = CreateFixture(isRewindable: true, batchObserver: observer, token: startToken);
        var batch = new TestBatchContainer(fixture.Handle.StreamId, batchToken);
        var diagnostics = new RecordingStreamingEventObserver(fixture.Handle.ProviderName);
        using var subscription = Orleans.Streaming.Diagnostics.StreamingEvents.AllEvents.Subscribe(diagnostics);

        var result = await fixture.Handle.DeliverBatch(batch, fixture.Handle.GetSequenceToken());

        Assert.Null(result);
        Assert.Empty(observer.Batches);
        Assert.Empty(diagnostics.Items);
        AssertDeliveryHandshake(fixture.Handle, batchToken);
        AssertNoProviderAcquisitions(fixture);
    }

    [Fact, TestSuite("BVT"), TestProvider("None"), TestArea("Streaming")]
    public async Task DeliverBatch_ContainerBatch_ItemObserver_PreservesInnerOrderAndRequestContextScopes()
    {
        RequestContext.Clear();
        var firstToken = new EventSequenceTokenV2(100, 0);
        var secondToken = new EventSequenceTokenV2(101, 0);
        var thirdToken = new EventSequenceTokenV2(101, 1);
        var observer = new RecordingObserver<int>();
        var fixture = CreateFixture(
            isRewindable: true,
            observer: observer,
            token: new EventSequenceTokenV2(99));
        var first = new TestBatchContainer(
            fixture.Handle.StreamId,
            firstToken,
            contextValue: "first-context",
            (1000, firstToken));
        var second = new TestBatchContainer(
            fixture.Handle.StreamId,
            thirdToken,
            contextValue: "second-context",
            (1010, secondToken),
            (1011, thirdToken));
        var batch = new TestBatchContainerBatch(
            fixture.Handle.StreamId,
            thirdToken,
            first,
            second);

        try
        {
            var result = await fixture.Handle.DeliverBatch(batch, fixture.Handle.GetSequenceToken());

            Assert.Null(result);
            Assert.Equal([1000, 1010, 1011], observer.Items);
            Assert.Equal(["first-context", "second-context", "second-context"], observer.RequestContexts);
            Assert.Equal(1, first.ImportRequestContextCalls);
            Assert.Equal(1, second.ImportRequestContextCalls);
            Assert.Equal([null], first.ContextBeforeImport);
            Assert.Equal([null], second.ContextBeforeImport);
            Assert.Null(RequestContext.Get(RequestContextKey));
            AssertDeliveryHandshake(fixture.Handle, thirdToken);
            AssertNoProviderAcquisitions(fixture);
        }
        finally
        {
            RequestContext.Clear();
        }
    }

    [Fact, TestSuite("BVT"), TestProvider("None"), TestArea("Streaming")]
    public async Task DeliverBatch_ContainerBatch_BatchObserver_FlattensWithoutReordering()
    {
        var token0 = new EventSequenceTokenV2(110, 0);
        var token1 = new EventSequenceTokenV2(111, 0);
        var token2 = new EventSequenceTokenV2(111, 1);
        var observer = new RecordingBatchObserver<int>();
        var fixture = CreateFixture(
            isRewindable: true,
            batchObserver: observer,
            token: new EventSequenceTokenV2(109));
        var first = new TestBatchContainer(
            fixture.Handle.StreamId,
            token0,
            contextValue: "not-imported-first",
            (1100, token0));
        var second = new TestBatchContainer(
            fixture.Handle.StreamId,
            token2,
            contextValue: "not-imported-second",
            (1110, token1),
            (1111, token2));
        var batch = new TestBatchContainerBatch(fixture.Handle.StreamId, token2, first, second);

        var result = await fixture.Handle.DeliverBatch(batch, fixture.Handle.GetSequenceToken());

        Assert.Null(result);
        var delivered = Assert.Single(observer.Batches);
        Assert.Equal([1100, 1110, 1111], delivered.Select(item => item.Item));
        Assert.Collection(
            delivered,
            item => Assert.Same(token0, item.Token),
            item => Assert.Same(token1, item.Token),
            item => Assert.Same(token2, item.Token));
        Assert.Equal(0, first.ImportRequestContextCalls);
        Assert.Equal(0, second.ImportRequestContextCalls);
        AssertDeliveryHandshake(fixture.Handle, token2);
        AssertNoProviderAcquisitions(fixture);
    }

    [Fact, TestSuite("BVT"), TestProvider("None"), TestArea("Streaming")]
    public async Task DeliverBatch_AcceptedItems_EmitOneDiagnosticEachAfterAcceptanceWithExactMetadata()
    {
        var token0 = new EventSequenceTokenV2(120, 0);
        var token1 = new EventSequenceTokenV2(120, 1);
        var observer = new RecordingObserver<int>();
        var subscriptionId = CreateSubscriptionId(implicitSubscription: false);
        var streamId = StreamId.Create("diagnostics", "metadata");
        var fixture = CreateFixture(
            isRewindable: true,
            observer: observer,
            token: new EventSequenceTokenV2(119),
            subscriptionId: subscriptionId,
            streamId: streamId,
            providerName: "diagnostic-provider",
            clusterId: "diagnostic-cluster");
        var batch = new TestBatchContainer(
            streamId,
            token1,
            (1200, token0),
            (1201, token1));
        var diagnostics = new RecordingStreamingEventObserver("diagnostic-provider", observer.Timeline);
        using var subscription = Orleans.Streaming.Diagnostics.StreamingEvents.AllEvents.Subscribe(diagnostics);

        await fixture.Handle.DeliverBatch(batch, fixture.Handle.GetSequenceToken());

        Assert.Equal(
            [
                "observer-attempt:1200",
                "observer-accepted:1200",
                "diagnostic:120:0",
                "observer-attempt:1201",
                "observer-accepted:1201",
                "diagnostic:120:1",
            ],
            observer.Timeline);
        Assert.Collection(
            diagnostics.Items,
            item =>
            {
                Assert.Equal("diagnostic-provider", item.StreamProvider);
                Assert.Equal(streamId, item.StreamId);
                Assert.Equal(subscriptionId.Guid, item.SubscriptionId);
                Assert.Equal("diagnostic-cluster", item.ClusterId);
                Assert.Null(item.SiloAddress);
                Assert.Same(token0, item.SequenceToken);
            },
            item =>
            {
                Assert.Equal("diagnostic-provider", item.StreamProvider);
                Assert.Equal(streamId, item.StreamId);
                Assert.Equal(subscriptionId.Guid, item.SubscriptionId);
                Assert.Equal("diagnostic-cluster", item.ClusterId);
                Assert.Null(item.SiloAddress);
                Assert.Same(token1, item.SequenceToken);
            });
        AssertDeliveryHandshake(fixture.Handle, token1);
        AssertNoProviderAcquisitions(fixture);
    }

    [Fact, TestSuite("BVT"), TestProvider("None"), TestArea("Streaming")]
    public async Task DeliverItem_ObserverFailsOnce_PropagatesSameExceptionAndRetryRedeliversThenAcknowledges()
    {
        var startToken = new EventSequenceTokenV2(129);
        var deliveredToken = new EventSequenceTokenV2(130, 4);
        var expectedFailure = new InvalidOperationException("fail once");
        var observer = new RecordingObserver<int>
        {
            FailOnNextCall = 1,
            NextException = expectedFailure,
        };
        var fixture = CreateFixture(isRewindable: true, observer: observer, token: startToken);
        var diagnostics = new RecordingStreamingEventObserver(fixture.Handle.ProviderName);
        using var subscription = Orleans.Streaming.Diagnostics.StreamingEvents.AllEvents.Subscribe(diagnostics);
        var expectedHandshake = fixture.Handle.GetSequenceToken();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Handle.DeliverItem(1304, deliveredToken, expectedHandshake));

        Assert.Same(expectedFailure, failure);
        Assert.Same(expectedHandshake, fixture.Handle.GetSequenceToken());
        Assert.Equal([1304], observer.Items);
        Assert.Empty(diagnostics.Items);

        var result = await fixture.Handle.DeliverItem(1304, deliveredToken, expectedHandshake);

        Assert.Null(result);
        Assert.Equal([1304, 1304], observer.Items);
        Assert.Collection(
            observer.Tokens,
            actual => Assert.Same(deliveredToken, actual),
            actual => Assert.Same(deliveredToken, actual));
        Assert.Same(deliveredToken, Assert.Single(diagnostics.Items).SequenceToken);
        AssertDeliveryHandshake(fixture.Handle, deliveredToken);
        AssertNoProviderAcquisitions(fixture);
    }

    [Fact, TestSuite("BVT"), TestProvider("None"), TestArea("Streaming")]
    public async Task DeliverBatch_ItemObserverFailsAtItemN_RetryRedeliversWholeBatchInExactOrder()
    {
        var token0 = new EventSequenceTokenV2(140, 0);
        var token1 = new EventSequenceTokenV2(140, 1);
        var token2 = new EventSequenceTokenV2(140, 2);
        var expectedFailure = new InvalidOperationException("second item fails once");
        var observer = new RecordingObserver<int>
        {
            FailOnNextCall = 2,
            NextException = expectedFailure,
        };
        var fixture = CreateFixture(
            isRewindable: true,
            observer: observer,
            token: new EventSequenceTokenV2(139));
        var batch = new TestBatchContainer(
            fixture.Handle.StreamId,
            token2,
            (1400, token0),
            (1401, token1),
            (1402, token2));
        var diagnostics = new RecordingStreamingEventObserver(fixture.Handle.ProviderName);
        using var subscription = Orleans.Streaming.Diagnostics.StreamingEvents.AllEvents.Subscribe(diagnostics);
        var expectedHandshake = fixture.Handle.GetSequenceToken();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Handle.DeliverBatch(batch, expectedHandshake));

        Assert.Same(expectedFailure, failure);
        Assert.Equal([1400, 1401], observer.Items);
        Assert.Same(expectedHandshake, fixture.Handle.GetSequenceToken());
        Assert.Same(token0, Assert.Single(diagnostics.Items).SequenceToken);

        var result = await fixture.Handle.DeliverBatch(batch, expectedHandshake);

        Assert.Null(result);
        Assert.Equal([1400, 1401, 1400, 1401, 1402], observer.Items);
        Assert.Collection(
            diagnostics.Items,
            item => Assert.Same(token0, item.SequenceToken),
            item => Assert.Same(token0, item.SequenceToken),
            item => Assert.Same(token1, item.SequenceToken),
            item => Assert.Same(token2, item.SequenceToken));
        AssertDeliveryHandshake(fixture.Handle, token2);
        AssertNoProviderAcquisitions(fixture);
    }

    [Fact, TestSuite("BVT"), TestProvider("None"), TestArea("Streaming")]
    public async Task DeliverBatch_BatchObserverFailsOnce_EmitsNoDiagnosticsUntilRetrySucceeds()
    {
        var token0 = new EventSequenceTokenV2(150, 0);
        var token1 = new EventSequenceTokenV2(150, 1);
        var expectedFailure = new InvalidOperationException("batch fails once");
        var observer = new RecordingBatchObserver<int>
        {
            FailOnNextCall = 1,
            NextException = expectedFailure,
        };
        var fixture = CreateFixture(
            isRewindable: true,
            batchObserver: observer,
            token: new EventSequenceTokenV2(149));
        var batch = new TestBatchContainer(
            fixture.Handle.StreamId,
            token1,
            (1500, token0),
            (1501, token1));
        var diagnostics = new RecordingStreamingEventObserver(fixture.Handle.ProviderName);
        using var subscription = Orleans.Streaming.Diagnostics.StreamingEvents.AllEvents.Subscribe(diagnostics);
        var expectedHandshake = fixture.Handle.GetSequenceToken();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Handle.DeliverBatch(batch, expectedHandshake));

        Assert.Same(expectedFailure, failure);
        Assert.Same(expectedHandshake, fixture.Handle.GetSequenceToken());
        Assert.Empty(diagnostics.Items);
        Assert.Single(observer.Batches);

        var result = await fixture.Handle.DeliverBatch(batch, expectedHandshake);

        Assert.Null(result);
        Assert.Equal(2, observer.Batches.Count);
        Assert.All(observer.Batches, delivered => Assert.Equal([1500, 1501], delivered.Select(item => item.Item)));
        Assert.All(
            observer.Batches,
            delivered => Assert.Collection(
                delivered,
                item => Assert.Same(token0, item.Token),
                item => Assert.Same(token1, item.Token)));
        Assert.Collection(
            diagnostics.Items,
            item => Assert.Same(token0, item.SequenceToken),
            item => Assert.Same(token1, item.SequenceToken));
        AssertDeliveryHandshake(fixture.Handle, token1);
        AssertNoProviderAcquisitions(fixture);
    }

    [Fact, TestSuite("BVT"), TestProvider("None"), TestArea("Streaming")]
    public async Task DeliverItem_MismatchedHandshake_ReturnsExpectedTokenWithoutSideEffects()
    {
        var observer = new RecordingObserver<int>();
        var fixture = CreateFixture(
            isRewindable: true,
            observer: observer,
            token: new EventSequenceTokenV2(159));
        var expected = fixture.Handle.GetSequenceToken();
        var mismatched = StreamHandshakeToken.CreateStartToken(new EventSequenceTokenV2(158));
        var diagnostics = new RecordingStreamingEventObserver(fixture.Handle.ProviderName);
        using var subscription = Orleans.Streaming.Diagnostics.StreamingEvents.AllEvents.Subscribe(diagnostics);

        var result = await fixture.Handle.DeliverItem(1600, new EventSequenceTokenV2(160), mismatched);

        Assert.Same(expected, result);
        Assert.Same(expected, fixture.Handle.GetSequenceToken());
        Assert.Empty(observer.Items);
        Assert.Empty(diagnostics.Items);
        AssertNoProviderAcquisitions(fixture);
    }

    [Fact, TestSuite("BVT"), TestProvider("None"), TestArea("Streaming")]
    public async Task DeliverBatch_MismatchedHandshake_ReturnsExpectedTokenWithoutSideEffects()
    {
        var observer = new RecordingBatchObserver<int>();
        var fixture = CreateFixture(
            isRewindable: true,
            batchObserver: observer,
            token: new EventSequenceTokenV2(169));
        var expected = fixture.Handle.GetSequenceToken();
        var token = new EventSequenceTokenV2(170);
        var batch = new TestBatchContainer(fixture.Handle.StreamId, token, (1700, token));
        var mismatched = StreamHandshakeToken.CreateStartToken(new EventSequenceTokenV2(168));
        var diagnostics = new RecordingStreamingEventObserver(fixture.Handle.ProviderName);
        using var subscription = Orleans.Streaming.Diagnostics.StreamingEvents.AllEvents.Subscribe(diagnostics);

        var result = await fixture.Handle.DeliverBatch(batch, mismatched);

        Assert.Same(expected, result);
        Assert.Same(expected, fixture.Handle.GetSequenceToken());
        Assert.Empty(observer.Batches);
        Assert.Empty(diagnostics.Items);
        AssertNoProviderAcquisitions(fixture);
    }

    [Fact, TestSuite("BVT"), TestProvider("None"), TestArea("Streaming")]
    public async Task DeliverBatch_AcknowledgedDuplicate_ReturnsExpectedTokenWithoutRedelivery()
    {
        var token = new EventSequenceTokenV2(180, 3);
        var expected = StreamHandshakeToken.CreateDeliveyToken(token);
        var observer = new RecordingObserver<int>();
        var fixture = CreateFixture(
            isRewindable: true,
            observer: observer,
            handshakeState: new StreamSubscriptionHandleImpl<int>.SharedHandshakeState { Token = expected });
        var batch = new TestBatchContainer(fixture.Handle.StreamId, token, (1803, token));
        var diagnostics = new RecordingStreamingEventObserver(fixture.Handle.ProviderName);
        using var subscription = Orleans.Streaming.Diagnostics.StreamingEvents.AllEvents.Subscribe(diagnostics);

        var result = await fixture.Handle.DeliverBatch(batch, expected);

        Assert.Same(expected, result);
        Assert.Same(expected, fixture.Handle.GetSequenceToken());
        Assert.Empty(observer.Items);
        Assert.Empty(diagnostics.Items);
        AssertNoProviderAcquisitions(fixture);
    }

    [Theory, TestSuite("BVT"), TestProvider("None"), TestArea("Streaming")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task CompleteStream_ForwardsOrPropagatesExactly(int mode)
    {
        var itemObserver = mode is 0 or 2 ? new RecordingObserver<int>() : null;
        var batchObserver = mode is 1 or 3 ? new RecordingBatchObserver<int>() : null;
        var expectedFailure = new InvalidOperationException("completion callback failed");
        if (mode == 2)
        {
            itemObserver!.CompletionException = expectedFailure;
        }
        else if (mode == 3)
        {
            batchObserver!.CompletionException = expectedFailure;
        }

        var fixture = CreateFixture(
            isRewindable: false,
            observer: itemObserver,
            batchObserver: batchObserver);

        if (mode is 2 or 3)
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(fixture.Handle.CompleteStream);
            Assert.Same(expectedFailure, failure);
        }
        else
        {
            var task = fixture.Handle.CompleteStream();
            Assert.True(task.IsCompletedSuccessfully);
            await task;
        }

        Assert.Equal(mode is 0 or 2 ? 1 : 0, itemObserver?.CompletionCalls ?? 0);
        Assert.Equal(mode is 1 or 3 ? 1 : 0, batchObserver?.CompletionCalls ?? 0);
        AssertNoProviderAcquisitions(fixture);
    }

    [Theory, TestSuite("BVT"), TestProvider("None"), TestArea("Streaming")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task ErrorInStream_ForwardsOrPropagatesExactly(int mode)
    {
        var itemObserver = mode is 0 or 2 ? new RecordingObserver<int>() : null;
        var batchObserver = mode is 1 or 3 ? new RecordingBatchObserver<int>() : null;
        var streamError = new InvalidOperationException("stream error");
        var callbackFailure = new ApplicationException("error callback failed");
        if (mode == 2)
        {
            itemObserver!.ErrorCallbackException = callbackFailure;
        }
        else if (mode == 3)
        {
            batchObserver!.ErrorCallbackException = callbackFailure;
        }

        var fixture = CreateFixture(
            isRewindable: false,
            observer: itemObserver,
            batchObserver: batchObserver);

        if (mode is 2 or 3)
        {
            var failure = await Assert.ThrowsAsync<ApplicationException>(
                () => fixture.Handle.ErrorInStream(streamError));
            Assert.Same(callbackFailure, failure);
        }
        else
        {
            var task = fixture.Handle.ErrorInStream(streamError);
            Assert.True(task.IsCompletedSuccessfully);
            await task;
        }

        if (itemObserver is not null)
        {
            Assert.Same(streamError, Assert.Single(itemObserver.Errors));
        }
        else if (batchObserver is not null)
        {
            Assert.Same(streamError, Assert.Single(batchObserver.Errors));
        }
        AssertNoProviderAcquisitions(fixture);
    }

    [Theory, TestSuite("BVT"), TestProvider("None"), TestArea("Streaming")]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task ResumeAsync_DelegatesExactArgumentsAndPreservesResultOrException(
        bool useBatchObserver,
        bool supplyToken,
        bool fail)
    {
        var fixture = CreateFixture(isRewindable: false);
        var replacement = CreateFixture(
            isRewindable: false,
            subscriptionId: fixture.Handle.SubscriptionId).Handle;
        var token = supplyToken ? new EventSequenceTokenV2(190, 7) : null;
        var expectedFailure = new InvalidOperationException("resume failed");
        fixture.Provider.Consumer.Replacement = replacement;
        fixture.Provider.Consumer.ResumeException = fail ? expectedFailure : null;
        var itemObserver = new RecordingObserver<int>();
        var batchObserver = new RecordingBatchObserver<int>();

        Task<StreamSubscriptionHandle<int>> operation = useBatchObserver
            ? fixture.Handle.ResumeAsync(batchObserver, token)
            : fixture.Handle.ResumeAsync(itemObserver, token);

        if (fail)
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => operation);
            Assert.Same(expectedFailure, failure);
        }
        else
        {
            Assert.Same(replacement, await operation);
        }

        Assert.Same(fixture.Handle, Assert.Single(fixture.Provider.Consumer.ResumeHandles));
        Assert.Same(token, Assert.Single(fixture.Provider.Consumer.ResumeTokens));
        if (useBatchObserver)
        {
            Assert.Same(batchObserver, Assert.Single(fixture.Provider.Consumer.ResumeBatchObservers));
            Assert.Empty(fixture.Provider.Consumer.ResumeObservers);
        }
        else
        {
            Assert.Same(itemObserver, Assert.Single(fixture.Provider.Consumer.ResumeObservers));
            Assert.Empty(fixture.Provider.Consumer.ResumeBatchObservers);
        }
        Assert.Equal(1, fixture.Provider.ConsumerInterfaceAcquisitions);
        Assert.Equal(0, fixture.Provider.ProducerInterfaceAcquisitions);
    }

    [Theory, TestSuite("BVT"), TestProvider("None"), TestArea("Streaming")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnsubscribeAsync_DelegatesExactHandleAndPropagatesSameException(bool fail)
    {
        var fixture = CreateFixture(isRewindable: false);
        var expectedFailure = new InvalidOperationException("unsubscribe failed");
        fixture.Provider.Consumer.UnsubscribeException = fail ? expectedFailure : null;

        var operation = fixture.Handle.UnsubscribeAsync();
        if (fail)
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => operation);
            Assert.Same(expectedFailure, failure);
        }
        else
        {
            await operation;
            Assert.True(operation.IsCompletedSuccessfully);
        }

        Assert.Same(fixture.Handle, Assert.Single(fixture.Provider.Consumer.UnsubscribeHandles));
        Assert.Equal(1, fixture.Provider.ConsumerInterfaceAcquisitions);
        Assert.Equal(0, fixture.Provider.ProducerInterfaceAcquisitions);
    }

    [Fact, TestSuite("BVT"), TestProvider("None"), TestArea("Streaming")]
    public async Task Invalidate_ClearsStreamAndObserversAndMarksHandleInvalid()
    {
        var subscriptionId = CreateSubscriptionId(implicitSubscription: false);
        var itemObserver = new RecordingObserver<int>();
        var batchObserver = new RecordingBatchObserver<int>();
        var fixture = CreateFixture(
            isRewindable: false,
            observer: itemObserver,
            batchObserver: batchObserver,
            subscriptionId: subscriptionId);

        fixture.Handle.Invalidate();
        await fixture.Handle.CompleteStream();
        await fixture.Handle.ErrorInStream(new InvalidOperationException("ignored"));

        Assert.False(fixture.Handle.IsValid);
        Assert.False(fixture.Handle.HasObserver);
        Assert.Equal(subscriptionId.Guid, fixture.Handle.HandleId);
        Assert.Equal(0, itemObserver.CompletionCalls);
        Assert.Empty(itemObserver.Errors);
        Assert.Equal(0, batchObserver.CompletionCalls);
        Assert.Empty(batchObserver.Errors);
        Assert.Throws<NullReferenceException>(() => fixture.Handle.ProviderName);
        Assert.Throws<NullReferenceException>(() => fixture.Handle.StreamId);
        AssertNoProviderAcquisitions(fixture);
    }

    [Fact, TestSuite("BVT"), TestProvider("None"), TestArea("Streaming")]
    public async Task InvalidHandle_IgnoresDeliveryCompletionAndErrorWithoutProviderAccess()
    {
        var itemObserver = new RecordingObserver<int>();
        var batchObserver = new RecordingBatchObserver<int>();
        var fixture = CreateFixture(
            isRewindable: false,
            observer: itemObserver,
            batchObserver: batchObserver);
        var token = new EventSequenceTokenV2(200);
        var batch = new TestBatchContainer(fixture.Handle.StreamId, token, (2000, token));
        var diagnostics = new RecordingStreamingEventObserver(fixture.Handle.ProviderName);
        using var subscription = Orleans.Streaming.Diagnostics.StreamingEvents.AllEvents.Subscribe(diagnostics);
        fixture.Handle.Invalidate();

        var itemResult = await fixture.Handle.DeliverItem(2000, token, handshakeToken: null);
        var batchResult = await fixture.Handle.DeliverBatch(batch, handshakeToken: null);
        var completion = fixture.Handle.CompleteStream();
        var error = fixture.Handle.ErrorInStream(new InvalidOperationException("ignored"));
        await Task.WhenAll(completion, error);

        Assert.Null(itemResult);
        Assert.Null(batchResult);
        Assert.True(completion.IsCompletedSuccessfully);
        Assert.True(error.IsCompletedSuccessfully);
        Assert.Empty(itemObserver.Items);
        Assert.Equal(0, itemObserver.CompletionCalls);
        Assert.Empty(itemObserver.Errors);
        Assert.Empty(batchObserver.Batches);
        Assert.Equal(0, batchObserver.CompletionCalls);
        Assert.Empty(batchObserver.Errors);
        Assert.Empty(diagnostics.Items);
        AssertNoProviderAcquisitions(fixture);
    }

    [Theory, TestSuite("BVT"), TestProvider("None"), TestArea("Streaming")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task InvalidHandle_ResumeAndUnsubscribeFailImmediatelyWithoutProviderAccess(int operation)
    {
        var fixture = CreateFixture(isRewindable: false);
        var itemObserver = new RecordingObserver<int>();
        var batchObserver = new RecordingBatchObserver<int>();
        var token = new EventSequenceTokenV2(210, 6);
        fixture.Handle.Invalidate();

        var failure = operation switch
        {
            0 => await Assert.ThrowsAsync<InvalidOperationException>(
                () => fixture.Handle.ResumeAsync(itemObserver)),
            1 => await Assert.ThrowsAsync<InvalidOperationException>(
                () => fixture.Handle.ResumeAsync(itemObserver, token)),
            2 => await Assert.ThrowsAsync<InvalidOperationException>(
                () => fixture.Handle.ResumeAsync(batchObserver)),
            3 => await Assert.ThrowsAsync<InvalidOperationException>(
                () => fixture.Handle.ResumeAsync(batchObserver, token)),
            _ => await Assert.ThrowsAsync<InvalidOperationException>(fixture.Handle.UnsubscribeAsync),
        };

        Assert.Equal(
            "Handle is no longer valid. It has been used to unsubscribe or resume.",
            failure.Message);
        Assert.Empty(fixture.Provider.Consumer.ResumeHandles);
        Assert.Empty(fixture.Provider.Consumer.UnsubscribeHandles);
        AssertNoProviderAcquisitions(fixture);
    }

    [Fact, TestSuite("BVT"), TestProvider("None"), TestArea("Streaming")]
    public async Task DeliverItem_BatchObserver_ForwardsOneExactSequentialItemAndAdvancesExactDeliveryToken()
    {
        var startToken = new EventSequenceTokenV2(220, 1);
        var deliveredToken = new EventSequenceTokenV2(221, 7);
        var observer = new RecordingBatchObserver<int>();
        var fixture = CreateFixture(isRewindable: true, batchObserver: observer, token: startToken);
        var diagnostics = new RecordingStreamingEventObserver(fixture.Handle.ProviderName);
        using var subscription = Orleans.Streaming.Diagnostics.StreamingEvents.AllEvents.Subscribe(diagnostics);
        var expectedHandshake = fixture.Handle.GetSequenceToken();

        var result = await fixture.Handle.DeliverItem(2217, deliveredToken, expectedHandshake);

        Assert.Null(result);
        var batch = Assert.Single(observer.Batches);
        var item = Assert.Single(batch);
        Assert.Equal(2217, item.Item);
        Assert.Same(deliveredToken, item.Token);
        Assert.Equal(221L, item.Token.SequenceNumber);
        Assert.Equal(7, item.Token.EventIndex);
        AssertDeliveryHandshake(fixture.Handle, deliveredToken);
        var diagnostic = Assert.Single(diagnostics.Items);
        Assert.Same(deliveredToken, diagnostic.SequenceToken);
        Assert.Equal(fixture.Handle.HandleId, diagnostic.SubscriptionId);
        AssertNoProviderAcquisitions(fixture);
    }

    [Fact, TestSuite("BVT"), TestProvider("None"), TestArea("Streaming")]
    public async Task DeliverItem_WrongItemType_ThrowsDocumentedInvalidCastWithoutSideEffects()
    {
        var startToken = new EventSequenceTokenV2(230, 2);
        var deliveredToken = new EventSequenceTokenV2(231, 3);
        var observer = new RecordingBatchObserver<int>();
        var fixture = CreateFixture(isRewindable: true, batchObserver: observer, token: startToken);
        var diagnostics = new RecordingStreamingEventObserver(fixture.Handle.ProviderName);
        using var subscription = Orleans.Streaming.Diagnostics.StreamingEvents.AllEvents.Subscribe(diagnostics);
        var expectedHandshake = fixture.Handle.GetSequenceToken();

        var exception = await Assert.ThrowsAsync<InvalidCastException>(
            () => fixture.Handle.DeliverItem("not-an-int", deliveredToken, expectedHandshake));

        Assert.Equal("Received an item of type String, expected System.Int32", exception.Message);
        Assert.Same(expectedHandshake, fixture.Handle.GetSequenceToken());
        Assert.Empty(observer.Batches);
        Assert.Empty(observer.Errors);
        Assert.Equal(0, observer.CompletionCalls);
        Assert.Empty(diagnostics.Items);
        AssertNoProviderAcquisitions(fixture);
    }

    [Fact, TestSuite("BVT"), TestProvider("None"), TestArea("Streaming")]
    public async Task DeliverItem_ObserverReplacesHandshakeDuringAcceptance_ReturnsReplacementWithoutOverwritingIt()
    {
        var initialSequenceToken = new EventSequenceTokenV2(240, 4);
        var initialHandshake = StreamHandshakeToken.CreateStartToken(initialSequenceToken);
        var replacementSequenceToken = new EventSequenceTokenV2(300, 6);
        var replacementHandshake = StreamHandshakeToken.CreateStartToken(replacementSequenceToken);
        var deliveredToken = new EventSequenceTokenV2(241, 5);
        var handshakeState = new StreamSubscriptionHandleImpl<int>.SharedHandshakeState { Token = initialHandshake };
        var observer = new HandshakeReplacingObserver<int>(
            () => handshakeState.Token = replacementHandshake);
        var fixture = CreateFixture(
            isRewindable: true,
            observer: observer,
            handshakeState: handshakeState);

        var result = await fixture.Handle.DeliverItem(2415, deliveredToken, initialHandshake);

        Assert.Same(replacementHandshake, result);
        Assert.Same(replacementHandshake, handshakeState.Token);
        Assert.Same(replacementHandshake, fixture.Handle.GetSequenceToken());
        Assert.Same(replacementSequenceToken, Assert.IsType<StartToken>(fixture.Handle.GetSequenceToken()).Token);
        Assert.Equal([2415], observer.Items);
        Assert.Same(deliveredToken, Assert.Single(observer.Tokens));
        Assert.Equal(1, observer.ReplacementCalls);
        AssertNoProviderAcquisitions(fixture);
    }

    private static void AssertNoProviderAcquisitions(HandleFixture fixture)
    {
        Assert.Equal(0, fixture.Provider.ConsumerInterfaceAcquisitions);
        Assert.Equal(0, fixture.Provider.ProducerInterfaceAcquisitions);
    }

    private static void AssertDeliveryHandshake(
        StreamSubscriptionHandleImpl<int> handle,
        StreamSequenceToken expectedToken)
    {
        Assert.Same(expectedToken, Assert.IsType<DeliveryToken>(handle.GetSequenceToken()).Token);
    }

    private static void AssertDeliveryHandshake(
        StreamSubscriptionHandleImpl<string> handle,
        StreamSequenceToken expectedToken)
    {
        Assert.Same(expectedToken, Assert.IsType<DeliveryToken>(handle.GetSequenceToken()).Token);
    }

    private static StringHandleFixture CreateStringFixture(
        IAsyncObserver<string> observer,
        StreamSequenceToken token)
    {
        var provider = new CountingStreamProvider<string>();
        var streamId = StreamId.Create("phase-1", Guid.NewGuid());
        var qualifiedStreamId = new QualifiedStreamId($"phase-1-provider-{Guid.NewGuid():N}", streamId);
        var runtimeClient = NSubstitute.Substitute.For<IRuntimeClient>();
        var stream = new StreamImpl<string>(qualifiedStreamId, provider, isRewindable: true, runtimeClient);
        var handle = new StreamSubscriptionHandleImpl<string>(
            CreateSubscriptionId(implicitSubscription: false),
            observer,
            batchObserver: null,
            stream,
            token,
            filterData: null,
            clusterId: "phase-1-cluster");
        return new(handle, provider);
    }

    private static void AssertNoProviderAcquisitions(StringHandleFixture fixture)
    {
        Assert.Equal(0, fixture.Provider.ConsumerInterfaceAcquisitions);
        Assert.Equal(0, fixture.Provider.ProducerInterfaceAcquisitions);
    }

    private sealed record StringHandleFixture(
        StreamSubscriptionHandleImpl<string> Handle,
        CountingStreamProvider<string> Provider);

    private sealed class RecordingBatchObserver<T> : IAsyncBatchObserver<T>
    {
        private int nextCallCount;

        public List<IList<SequentialItem<T>>> Batches { get; } = [];
        public List<Exception> Errors { get; } = [];
        public int? FailOnNextCall { get; set; }
        public Exception? NextException { get; set; }
        public Exception? CompletionException { get; set; }
        public Exception? ErrorCallbackException { get; set; }
        public int CompletionCalls { get; private set; }

        public Task OnNextAsync(IList<SequentialItem<T>> items)
        {
            nextCallCount++;
            Batches.Add(items.ToList());
            return nextCallCount == FailOnNextCall
                ? Task.FromException(NextException!)
                : Task.CompletedTask;
        }

        public Task OnCompletedAsync()
        {
            CompletionCalls++;
            return CompletionException is null
                ? Task.CompletedTask
                : Task.FromException(CompletionException);
        }

        public Task OnErrorAsync(Exception ex)
        {
            Errors.Add(ex);
            return ErrorCallbackException is null
                ? Task.CompletedTask
                : Task.FromException(ErrorCallbackException);
        }
    }

    private sealed class TestBatchContainer : IBatchContainer
    {
        private readonly (int Item, StreamSequenceToken Token)[] events;
        private readonly object? contextValue;

        public TestBatchContainer(
            StreamId streamId,
            StreamSequenceToken sequenceToken,
            params (int Item, StreamSequenceToken Token)[] events)
            : this(streamId, sequenceToken, contextValue: null, events)
        {
        }

        public TestBatchContainer(
            StreamId streamId,
            StreamSequenceToken sequenceToken,
            object? contextValue,
            params (int Item, StreamSequenceToken Token)[] events)
        {
            StreamId = streamId;
            SequenceToken = sequenceToken;
            this.contextValue = contextValue;
            this.events = events;
        }

        public StreamId StreamId { get; }
        public StreamSequenceToken SequenceToken { get; }
        public int ImportRequestContextCalls { get; private set; }
        public List<object?> ContextBeforeImport { get; } = [];

        public IEnumerable<Tuple<T, StreamSequenceToken>> GetEvents<T>()
            => events.Select(item => Tuple.Create((T)(object)item.Item, item.Token));

        public bool ImportRequestContext()
        {
            ImportRequestContextCalls++;
            ContextBeforeImport.Add(RequestContext.Get(RequestContextKey));
            if (contextValue is null)
            {
                return false;
            }

            RequestContext.Set(RequestContextKey, contextValue);
            return true;
        }
    }

    private sealed class TestBatchContainerBatch(
        StreamId streamId,
        StreamSequenceToken sequenceToken,
        params IBatchContainer[] batchContainers) : IBatchContainerBatch
    {
        public List<IBatchContainer> BatchContainers { get; } = [.. batchContainers];
        public StreamId StreamId { get; } = streamId;
        public StreamSequenceToken SequenceToken { get; } = sequenceToken;

        public IEnumerable<Tuple<T, StreamSequenceToken>> GetEvents<T>()
            => throw new InvalidOperationException("The outer container must be flattened through BatchContainers.");

        public bool ImportRequestContext()
            => throw new InvalidOperationException("The outer container must not import request context.");
    }

    private sealed class RecordingStreamingEventObserver(
        string providerName,
        List<string>? timeline = null)
        : IObserver<Orleans.Streaming.Diagnostics.StreamingEvents.StreamingEvent>
    {
        public List<Orleans.Streaming.Diagnostics.StreamingEvents.ItemDelivered> Items { get; } = [];

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
            ExceptionDispatchInfo.Throw(error);
        }

        public void OnNext(Orleans.Streaming.Diagnostics.StreamingEvents.StreamingEvent value)
        {
            if (value is Orleans.Streaming.Diagnostics.StreamingEvents.ItemDelivered item
                && string.Equals(providerName, item.StreamProvider, StringComparison.Ordinal))
            {
                Items.Add(item);
                timeline?.Add($"diagnostic:{item.SequenceToken!.SequenceNumber}:{item.SequenceToken.EventIndex}");
            }
        }
    }

    private sealed class ForeignSubscriptionHandle(StreamId streamId) : StreamSubscriptionHandle<int>
    {
        public override Guid HandleId { get; } = Guid.NewGuid();
        public override string ProviderName => "foreign";
        public override StreamId StreamId { get; } = streamId;

        public override bool Equals(StreamSubscriptionHandle<int>? other) => ReferenceEquals(this, other);
        public override bool Equals(object? obj) => ReferenceEquals(this, obj);
        public override int GetHashCode() => HandleId.GetHashCode();

        public override Task<StreamSubscriptionHandle<int>> ResumeAsync(
            IAsyncObserver<int> observer,
            StreamSequenceToken? token = null)
            => throw new NotSupportedException();

        public override Task<StreamSubscriptionHandle<int>> ResumeAsync(
            IAsyncBatchObserver<int> observer,
            StreamSequenceToken? token = null)
            => throw new NotSupportedException();

        public override Task UnsubscribeAsync() => throw new NotSupportedException();
    }

    private sealed class HandshakeReplacingObserver<T>(Action replaceHandshake) : IAsyncObserver<T>
    {
        public List<T> Items { get; } = [];
        public List<StreamSequenceToken?> Tokens { get; } = [];
        public int ReplacementCalls { get; private set; }

        public Task OnNextAsync(T item, StreamSequenceToken? token = null)
        {
            Items.Add(item);
            Tokens.Add(token);
            ReplacementCalls++;
            replaceHandshake();
            return Task.CompletedTask;
        }

        public Task OnCompletedAsync() => throw new NotSupportedException();

        public Task OnErrorAsync(Exception ex) => throw new NotSupportedException();
    }

    private static StreamImpl<int> CreateStream(
        bool isRewindable,
        IInternalAsyncObservable<int>? consumer = null)
    {
        return new StreamImpl<int>(
            new QualifiedStreamId("provider", StreamId.Create("namespace", Guid.NewGuid())),
            new TestStreamProvider(consumer),
            isRewindable,
            Substitute.For<IRuntimeClient>());
    }

    private sealed class TestStreamProvider(IInternalAsyncObservable<int>? consumer) : IInternalStreamProvider
    {
        public IInternalAsyncBatchObserver<T> GetProducerInterface<T>(IAsyncStream<T> streamId) => null!;

        public IInternalAsyncObservable<T> GetConsumerInterface<T>(IAsyncStream<T> streamId)
            => (IInternalAsyncObservable<T>)(object)consumer!;
    }

    private sealed class RecordingInternalObservable : IInternalAsyncObservable<int>
    {
        public IAsyncObserver<int>? ItemObserver { get; private set; }
        public IAsyncBatchObserver<int>? BatchObserver { get; private set; }
        public StreamSubscriptionStartPosition StartPosition { get; private set; }
        public string? FilterData { get; private set; }

        public Task<StreamSubscriptionHandle<int>> SubscribeAsync(IAsyncObserver<int> observer)
            => Task.FromResult<StreamSubscriptionHandle<int>>(null!);

        public Task<StreamSubscriptionHandle<int>> SubscribeAsync(
            IAsyncObserver<int> observer,
            StreamSequenceToken? token,
            string? filterData = null)
            => Task.FromResult<StreamSubscriptionHandle<int>>(null!);

        public Task<StreamSubscriptionHandle<int>> SubscribeAsync(
            IAsyncObserver<int> observer,
            StreamSubscriptionStartPosition startPosition,
            string? filterData = null)
        {
            ItemObserver = observer;
            StartPosition = startPosition;
            FilterData = filterData;
            return Task.FromResult<StreamSubscriptionHandle<int>>(null!);
        }

        public Task<StreamSubscriptionHandle<int>> SubscribeAsync(IAsyncBatchObserver<int> observer)
            => Task.FromResult<StreamSubscriptionHandle<int>>(null!);

        public Task<StreamSubscriptionHandle<int>> SubscribeAsync(
            IAsyncBatchObserver<int> observer,
            StreamSequenceToken? token)
            => Task.FromResult<StreamSubscriptionHandle<int>>(null!);

        public Task<StreamSubscriptionHandle<int>> SubscribeAsync(
            IAsyncBatchObserver<int> observer,
            StreamSubscriptionStartPosition startPosition)
        {
            BatchObserver = observer;
            StartPosition = startPosition;
            return Task.FromResult<StreamSubscriptionHandle<int>>(null!);
        }

        public Task<StreamSubscriptionHandle<int>> ResumeAsync(
            StreamSubscriptionHandle<int> handle,
            IAsyncObserver<int> observer,
            StreamSequenceToken? token = null)
            => Task.FromResult<StreamSubscriptionHandle<int>>(null!);

        public Task<StreamSubscriptionHandle<int>> ResumeAsync(
            StreamSubscriptionHandle<int> handle,
            IAsyncBatchObserver<int> observer,
            StreamSequenceToken? token = null)
            => Task.FromResult<StreamSubscriptionHandle<int>>(null!);

        public Task UnsubscribeAsync(StreamSubscriptionHandle<int> handle) => Task.CompletedTask;

        public Task<IList<StreamSubscriptionHandle<int>>> GetAllSubscriptions()
            => Task.FromResult<IList<StreamSubscriptionHandle<int>>>([]);

        public Task Cleanup() => Task.CompletedTask;
    }

    private sealed class LegacyObservable : IAsyncObservable<int>
    {
        public bool TokenOverloadCalled { get; private set; }
        public StreamSequenceToken? Token { get; private set; }

        public Task<StreamSubscriptionHandle<int>> SubscribeAsync(IAsyncObserver<int> observer)
            => Task.FromResult<StreamSubscriptionHandle<int>>(null!);

        public Task<StreamSubscriptionHandle<int>> SubscribeAsync(
            IAsyncObserver<int> observer,
            StreamSequenceToken? token,
            string? filterData = null)
        {
            TokenOverloadCalled = true;
            Token = token;
            return Task.FromResult<StreamSubscriptionHandle<int>>(null!);
        }
    }

    private sealed class LegacyBatchObservable : IAsyncBatchObservable<int>
    {
        public bool TokenOverloadCalled { get; private set; }
        public StreamSequenceToken? Token { get; private set; }

        public Task<StreamSubscriptionHandle<int>> SubscribeAsync(IAsyncBatchObserver<int> observer)
            => Task.FromResult<StreamSubscriptionHandle<int>>(null!);

        public Task<StreamSubscriptionHandle<int>> SubscribeAsync(
            IAsyncBatchObserver<int> observer,
            StreamSequenceToken? token)
        {
            TokenOverloadCalled = true;
            Token = token;
            return Task.FromResult<StreamSubscriptionHandle<int>>(null!);
        }
    }
}
