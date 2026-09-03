using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;
using Orleans.Streams.Core;
using Xunit;

namespace UnitTests.StreamingTests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Streaming")]
public class StreamConsumerLifecycleTests
{
    [Fact]
    public async Task ResumeAsync_SuccessReplacesRegistrationPreservesIdentityAndInvalidatesOldHandle()
    {
        using var fixture = new ConsumerFixture();
        var subscriptionId = CreateSubscriptionId();
        var startToken = new EventSequenceTokenV2(10, 2);
        var oldObserver = new RecordingObserver();
        var oldHandle = fixture.Register(subscriptionId, oldObserver, token: startToken, filterData: "region=west");
        var initialHandshake = oldHandle.GetSequenceToken();
        var replacementObserver = new RecordingObserver();

        var result = await fixture.Consumer.ResumeAsync(oldHandle, replacementObserver);

        var replacement = Assert.IsType<StreamSubscriptionHandleImpl<int>>(result);
        Assert.False(oldHandle.IsValid);
        Assert.False(oldHandle.HasObserver);
        Assert.True(replacement.IsValid);
        Assert.True(replacement.HasObserver);
        Assert.Same(subscriptionId, replacement.SubscriptionId);
        Assert.Equal(oldHandle.HandleId, replacement.HandleId);
        Assert.Equal("region=west", replacement.FilterData);
        Assert.Same(initialHandshake, replacement.GetSequenceToken());
        Assert.Same(replacement, Assert.Single(fixture.Extension.GetAllStreamHandles<int>()));
        Assert.Equal(1, fixture.Runtime.BindCalls);
        Assert.Equal(0, fixture.PubSub.InvocationCount);
        fixture.AssertNoStreamProviderAcquisitions();
    }

    [Fact]
    public async Task ResumeAsync_SetObserverFailureLeavesOldHandleRegisteredAndRetryable()
    {
        using var fixture = new ConsumerFixture();
        var subscriptionId = CreateSubscriptionId();
        var oldObserver = new RecordingObserver();
        var oldHandle = fixture.Register(
            subscriptionId,
            oldObserver,
            token: new EventSequenceTokenV2(20, 1),
            filterData: "retry");
        var initialHandshake = oldHandle.GetSequenceToken();
        var failure = new InvalidOperationException("set-observer failed");
        fixture.Runtime.FailNextObserverRegistration(failure);

        var caught = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Consumer.ResumeAsync(oldHandle, new RecordingObserver()));

        Assert.Same(failure, caught);
        Assert.True(oldHandle.IsValid);
        Assert.True(oldHandle.HasObserver);
        Assert.Same(oldHandle, Assert.Single(fixture.Extension.GetAllStreamHandles<int>()));
        Assert.Same(initialHandshake, oldHandle.GetSequenceToken());
        Assert.Equal(1, fixture.Runtime.BindCalls);

        var replacement = Assert.IsType<StreamSubscriptionHandleImpl<int>>(
            await fixture.Consumer.ResumeAsync(oldHandle, new RecordingObserver()));

        Assert.False(oldHandle.IsValid);
        Assert.True(replacement.IsValid);
        Assert.Same(subscriptionId, replacement.SubscriptionId);
        Assert.Equal(oldHandle.HandleId, replacement.HandleId);
        Assert.Equal("retry", replacement.FilterData);
        Assert.Same(initialHandshake, replacement.GetSequenceToken());
        Assert.Same(replacement, Assert.Single(fixture.Extension.GetAllStreamHandles<int>()));
        Assert.Equal(1, fixture.Runtime.BindCalls);
        Assert.Equal(0, fixture.PubSub.InvocationCount);
        fixture.AssertNoStreamProviderAcquisitions();
    }

    [Fact]
    public async Task UnsubscribeAsync_RemovesLocallyBeforeUnregisterAndInvalidatesAfterSuccess()
    {
        using var fixture = new ConsumerFixture();
        var subscriptionId = CreateSubscriptionId();
        var handle = fixture.Register(subscriptionId, new RecordingObserver());
        var snapshots = new List<(int RegistryCount, bool HandleValid)>();
        fixture.PubSub.OnUnregister = _ =>
        {
            snapshots.Add((fixture.Extension.GetAllStreamHandles<int>().Count, handle.IsValid));
            return Task.CompletedTask;
        };

        await fixture.Consumer.UnsubscribeAsync(handle);

        Assert.Equal([(0, true)], snapshots);
        Assert.False(handle.IsValid);
        Assert.False(handle.HasObserver);
        Assert.Empty(fixture.Extension.GetAllStreamHandles<int>());
        var call = Assert.Single(fixture.PubSub.UnregisterCalls);
        Assert.Same(subscriptionId, call.SubscriptionId);
        Assert.Equal(fixture.Stream.InternalStreamId, call.StreamId);
        Assert.Equal(1, fixture.PubSub.InvocationCount);
        Assert.Equal(1, fixture.Runtime.BindCalls);
        fixture.AssertNoStreamProviderAcquisitions();
    }

    [Fact]
    public async Task UnsubscribeAsync_UnregisterFailureLeavesHandleValidAndSecondCallRetries()
    {
        using var fixture = new ConsumerFixture();
        var subscriptionId = CreateSubscriptionId();
        var handle = fixture.Register(subscriptionId, new RecordingObserver());
        var failure = new InvalidOperationException("unregister failed");
        var snapshots = new List<(int RegistryCount, bool HandleValid)>();
        fixture.PubSub.OnUnregister = call =>
        {
            snapshots.Add((fixture.Extension.GetAllStreamHandles<int>().Count, handle.IsValid));
            return call.Attempt == 1 ? Task.FromException(failure) : Task.CompletedTask;
        };

        var caught = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Consumer.UnsubscribeAsync(handle));

        Assert.Same(failure, caught);
        Assert.Equal([(0, true)], snapshots);
        Assert.True(handle.IsValid);
        Assert.True(handle.HasObserver);
        Assert.Empty(fixture.Extension.GetAllStreamHandles<int>());

        await fixture.Consumer.UnsubscribeAsync(handle);

        Assert.Equal([(0, true), (0, true)], snapshots);
        Assert.False(handle.IsValid);
        Assert.Equal(2, fixture.PubSub.UnregisterCalls.Count);
        Assert.All(fixture.PubSub.UnregisterCalls, call =>
        {
            Assert.Same(subscriptionId, call.SubscriptionId);
            Assert.Equal(fixture.Stream.InternalStreamId, call.StreamId);
        });
        Assert.Equal(2, fixture.PubSub.InvocationCount);
        Assert.Equal(1, fixture.Runtime.BindCalls);
        fixture.AssertNoStreamProviderAcquisitions();
    }

    [Fact]
    public async Task CheckHandleValidity_NullHandleRejectsBeforeAnyMutation()
    {
        using var fixture = new ConsumerFixture();

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(
            () => fixture.Consumer.ResumeAsync(null!, new RecordingObserver()));

        Assert.Equal("handle", exception.ParamName);
        fixture.AssertNoLifecycleMutations();
    }

    [Fact]
    public async Task CheckHandleValidity_ForeignStreamHandleRejectsBeforeAnyMutation()
    {
        using var fixture = new ConsumerFixture();
        var foreignStream = fixture.CreateStream(StreamId.Create("phase-2", "foreign"));
        var handle = new StreamSubscriptionHandleImpl<int>(CreateSubscriptionId(), foreignStream);

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => fixture.Consumer.ResumeAsync(handle, new RecordingObserver()));

        Assert.Equal("handle", exception.ParamName);
        Assert.Contains("not for this stream", exception.Message);
        Assert.True(handle.IsValid);
        fixture.AssertNoLifecycleMutations();
    }

    [Fact]
    public async Task CheckHandleValidity_UnsupportedHandleTypeRejectsBeforeAnyMutation()
    {
        using var fixture = new ConsumerFixture();
        var handle = new ForeignSubscriptionHandle(fixture.Stream.StreamId);

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => fixture.Consumer.ResumeAsync(handle, new RecordingObserver()));

        Assert.Equal("handle", exception.ParamName);
        Assert.Contains("type not supported", exception.Message);
        fixture.AssertNoLifecycleMutations();
    }

    [Fact]
    public async Task StreamConsumerExtension_RegistryRoutesBySubscriptionAndStopsAfterRemoval()
    {
        using var fixture = new ConsumerFixture();
        var itemSubscriptionId = CreateSubscriptionId();
        var batchSubscriptionId = CreateSubscriptionId();
        var itemObserver = new RecordingObserver();
        var batchObserver = new RecordingBatchObserver();
        var itemStartToken = new EventSequenceTokenV2(30, 3);
        var batchStartToken = new EventSequenceTokenV2(40, 4);
        var itemHandle = fixture.Register(itemSubscriptionId, itemObserver, token: itemStartToken);
        var batchHandle = fixture.Register(batchSubscriptionId, batchObserver, token: batchStartToken);
        var itemHandshake = await fixture.Extension.GetSequenceToken(itemSubscriptionId, TestContext.Current.CancellationToken);
        var batchHandshake = await fixture.Extension.GetSequenceToken(batchSubscriptionId, TestContext.Current.CancellationToken);
        var deliveredItemToken = new EventSequenceTokenV2(31, 5);
        var deliveredBatchToken = new EventSequenceTokenV2(41, 6);
        var batch = new TestBatchContainer(fixture.Stream.StreamId, deliveredBatchToken, 71);
        var streamError = new InvalidOperationException("stream failed");

        await fixture.Extension.DeliverMutable(
            itemSubscriptionId,
            fixture.Stream.InternalStreamId,
            61,
            deliveredItemToken,
            itemHandshake,
            TestContext.Current.CancellationToken);
        await fixture.Extension.DeliverBatch(
            batchSubscriptionId,
            fixture.Stream.InternalStreamId,
            batch,
            batchHandshake,
            TestContext.Current.CancellationToken);
        await fixture.Extension.CompleteStream(itemSubscriptionId, TestContext.Current.CancellationToken);
        await fixture.Extension.ErrorInStream(batchSubscriptionId, streamError, TestContext.Current.CancellationToken);

        Assert.Same(itemStartToken, Assert.IsType<StartToken>(itemHandshake).Token);
        Assert.Same(batchStartToken, Assert.IsType<StartToken>(batchHandshake).Token);
        Assert.Equal([61], itemObserver.Items);
        Assert.Same(deliveredItemToken, Assert.Single(itemObserver.Tokens));
        var deliveredBatch = Assert.Single(batchObserver.Batches);
        var deliveredBatchItem = Assert.Single(deliveredBatch);
        Assert.Equal(71, deliveredBatchItem.Item);
        Assert.Same(deliveredBatchToken, deliveredBatchItem.Token);
        Assert.Equal(1, itemObserver.CompletionCalls);
        Assert.Same(streamError, Assert.Single(batchObserver.Errors));
        Assert.Empty(batchObserver.Completions);
        Assert.Empty(itemObserver.Errors);
        Assert.Contains(itemHandle, fixture.Extension.GetAllStreamHandles<int>());
        Assert.Contains(batchHandle, fixture.Extension.GetAllStreamHandles<int>());

        Assert.True(fixture.Extension.RemoveObserver(itemSubscriptionId));
        Assert.False(fixture.Extension.RemoveObserver(itemSubscriptionId));
        Assert.Null(await fixture.Extension.DeliverImmutable(
            itemSubscriptionId,
            fixture.Stream.InternalStreamId,
            62,
            new EventSequenceTokenV2(32),
            handshakeToken: null,
            cancellationToken: TestContext.Current.CancellationToken));
        await fixture.Extension.CompleteStream(itemSubscriptionId, TestContext.Current.CancellationToken);
        await fixture.Extension.ErrorInStream(
            itemSubscriptionId,
            new InvalidOperationException("dropped"),
            TestContext.Current.CancellationToken);

        Assert.Null(await fixture.Extension.GetSequenceToken(itemSubscriptionId, TestContext.Current.CancellationToken));
        Assert.Equal([61], itemObserver.Items);
        Assert.Equal(1, itemObserver.CompletionCalls);
        Assert.Empty(itemObserver.Errors);
        Assert.Same(batchHandle, Assert.Single(fixture.Extension.GetAllStreamHandles<int>()));
        Assert.Equal(0, fixture.PubSub.InvocationCount);
        Assert.Equal(0, fixture.Runtime.BindCalls);
        fixture.AssertNoStreamProviderAcquisitions();
    }

    [Fact]
    public async Task ResumeAsync_InvalidatedConcreteHandle_RejectsBeforeBoundExtensionOrLifecycleMutation()
    {
        using var fixture = new ConsumerFixture();
        var observer = new RecordingObserver();
        var handle = fixture.Register(CreateSubscriptionId(), observer, filterData: "invalidated");
        handle.Invalidate();
        Assert.True(fixture.Runtime.IsExtensionCreated);
        Assert.Same(handle, Assert.Single(fixture.Extension.GetAllStreamHandles<int>()));

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => fixture.Consumer.ResumeAsync(handle, new RecordingObserver()));

        Assert.Equal("handle", exception.ParamName);
        Assert.Equal(
            "Handle is no longer valid.  It has been used to unsubscribe or resume. (Parameter 'handle')",
            exception.Message);
        Assert.False(handle.IsValid);
        Assert.False(handle.HasObserver);
        Assert.Same(handle, Assert.Single(fixture.Extension.GetAllStreamHandles<int>()));
        Assert.Equal(0, fixture.Runtime.BindCalls);
        Assert.Equal(0, fixture.PubSub.InvocationCount);
        Assert.Empty(observer.Items);
        Assert.Empty(observer.Tokens);
        Assert.Empty(observer.Errors);
        Assert.Equal(0, observer.CompletionCalls);
        fixture.AssertNoStreamProviderAcquisitions();
    }

    private static GuidId CreateSubscriptionId()
        => GuidId.GetGuidId(SubscriptionMarker.MarkAsExplicitSubscriptionId(Guid.NewGuid()));

    private sealed class ConsumerFixture : IDisposable
    {
        private const string ProviderName = "phase-2-provider";

        public ConsumerFixture()
        {
            Runtime = new RecordingStreamProviderRuntime();
            PubSub = new RecordingStreamPubSub();
            StreamProvider = new CountingStreamProvider();
            Stream = CreateStream(StreamId.Create("phase-2", Guid.NewGuid()));
            Consumer = new StreamConsumer<int>(
                Stream,
                ProviderName,
                Runtime,
                PubSub,
                NullLogger<StreamConsumer<int>>.Instance,
                isRewindable: true);
        }

        public RecordingStreamProviderRuntime Runtime { get; }
        public RecordingStreamPubSub PubSub { get; }
        public CountingStreamProvider StreamProvider { get; }
        public StreamImpl<int> Stream { get; }
        public StreamConsumer<int> Consumer { get; }
        public StreamConsumerExtension Extension => Runtime.Extension;

        public StreamImpl<int> CreateStream(StreamId streamId)
            => new(
                new QualifiedStreamId(ProviderName, streamId),
                StreamProvider,
                isRewindable: true,
                NSubstitute.Substitute.For<IRuntimeClient>());

        public StreamSubscriptionHandleImpl<int> Register(
            GuidId subscriptionId,
            RecordingObserver observer,
            StreamSequenceToken? token = null,
            string? filterData = null)
            => Extension.SetObserver(
                subscriptionId,
                Stream,
                observer: observer,
                batchObserver: null,
                token: token,
                startPosition: null,
                filterData: filterData);

        public StreamSubscriptionHandleImpl<int> Register(
            GuidId subscriptionId,
            RecordingBatchObserver observer,
            StreamSequenceToken? token = null,
            string? filterData = null)
            => Extension.SetObserver(
                subscriptionId,
                Stream,
                observer: null,
                batchObserver: observer,
                token: token,
                startPosition: null,
                filterData: filterData);

        public void AssertNoStreamProviderAcquisitions()
        {
            Assert.Equal(0, StreamProvider.ConsumerInterfaceAcquisitions);
            Assert.Equal(0, StreamProvider.ProducerInterfaceAcquisitions);
        }

        public void AssertNoLifecycleMutations()
        {
            Assert.False(Runtime.IsExtensionCreated);
            Assert.Equal(0, Runtime.BindCalls);
            Assert.Equal(0, PubSub.InvocationCount);
            AssertNoStreamProviderAcquisitions();
        }

        public void Dispose() => Runtime.Dispose();
    }

    private sealed class RecordingStreamProviderRuntime : IStreamProviderRuntime, IDisposable
    {
        private readonly ServiceProvider serviceProvider;
        private StreamConsumerExtension? extension;
        private Exception? nextObserverRegistrationFailure;

        public RecordingStreamProviderRuntime()
        {
            var services = new ServiceCollection();
            services.AddSingleton<ILogger<StreamConsumerExtension>>(NullLogger<StreamConsumerExtension>.Instance);
            services.AddSingleton<IOptions<ClusterOptions>>(
                Options.Create(new ClusterOptions { ClusterId = "phase-2-cluster" }));
            services.AddSingleton<IGrainContextAccessor>(new NullGrainContextAccessor());
            serviceProvider = services.BuildServiceProvider();
        }

        public int BindCalls { get; private set; }
        public bool IsExtensionCreated => extension is not null;
        public StreamConsumerExtension Extension => extension ??= new StreamConsumerExtension(this);
        public IGrainFactory GrainFactory => throw new NotSupportedException();
        public IServiceProvider ServiceProvider => serviceProvider;

        public (TExtension Extension, TExtensionInterface ExtensionReference)
            BindExtension<TExtension, TExtensionInterface>(Func<TExtension> newExtensionFunc)
            where TExtension : class, TExtensionInterface
            where TExtensionInterface : class, IGrainExtension
        {
            BindCalls++;
            if (typeof(TExtension) != typeof(StreamConsumerExtension)
                || typeof(TExtensionInterface) != typeof(IStreamConsumerExtension))
            {
                throw new NotSupportedException(
                    $"Unexpected extension binding: {typeof(TExtension)} / {typeof(TExtensionInterface)}.");
            }

            extension ??= (StreamConsumerExtension)(object)newExtensionFunc();
            return ((TExtension)(object)extension, (TExtensionInterface)(object)extension);
        }

        public string ExecutingEntityIdentity()
        {
            if (nextObserverRegistrationFailure is { } failure)
            {
                nextObserverRegistrationFailure = null;
                throw failure;
            }

            return "phase-2-consumer";
        }

        public StreamDirectory GetStreamDirectory() => throw new NotSupportedException();
        public IStreamPubSub? PubSub(StreamPubSubType pubSubType) => throw new NotSupportedException();

        public void FailNextObserverRegistration(Exception exception)
            => nextObserverRegistrationFailure = exception;

        public void Dispose() => serviceProvider.Dispose();
    }

    private sealed class NullGrainContextAccessor : IGrainContextAccessor
    {
        public IGrainContext GrainContext => null!;
    }

    private sealed class RecordingStreamPubSub : IStreamPubSub
    {
        public List<UnregisterCall> UnregisterCalls { get; } = [];
        public Func<UnregisterCall, Task>? OnUnregister { get; set; }
        public int InvocationCount { get; private set; }

        public Task UnregisterConsumer(GuidId subscriptionId, QualifiedStreamId streamId)
        {
            InvocationCount++;
            var call = new UnregisterCall(subscriptionId, streamId, UnregisterCalls.Count + 1);
            UnregisterCalls.Add(call);
            return OnUnregister?.Invoke(call) ?? Task.CompletedTask;
        }

        public Task<ISet<PubSubSubscriptionState>> RegisterProducer(
            QualifiedStreamId streamId,
            GrainId streamProducer)
        {
            InvocationCount++;
            return Task.FromResult<ISet<PubSubSubscriptionState>>(new HashSet<PubSubSubscriptionState>());
        }

        public Task UnregisterProducer(QualifiedStreamId streamId, GrainId streamProducer)
        {
            InvocationCount++;
            return Task.CompletedTask;
        }

        public Task RegisterConsumer(
            GuidId subscriptionId,
            QualifiedStreamId streamId,
            GrainId streamConsumer,
            string? filterData)
        {
            InvocationCount++;
            return Task.CompletedTask;
        }

        public Task<int> ProducerCount(QualifiedStreamId streamId)
        {
            InvocationCount++;
            return Task.FromResult(0);
        }

        public Task<int> ConsumerCount(QualifiedStreamId streamId)
        {
            InvocationCount++;
            return Task.FromResult(0);
        }

        public Task<List<StreamSubscription>> GetAllSubscriptions(
            QualifiedStreamId streamId,
            GrainId streamConsumer = default)
        {
            InvocationCount++;
            return Task.FromResult(new List<StreamSubscription>());
        }

        public GuidId CreateSubscriptionId(QualifiedStreamId streamId, GrainId streamConsumer)
        {
            InvocationCount++;
            return StreamConsumerLifecycleTests.CreateSubscriptionId();
        }

        public Task<bool> FaultSubscription(QualifiedStreamId streamId, GuidId subscriptionId)
        {
            InvocationCount++;
            return Task.FromResult(false);
        }
    }

    private sealed record UnregisterCall(
        GuidId SubscriptionId,
        QualifiedStreamId StreamId,
        int Attempt);

    private sealed class CountingStreamProvider : IInternalStreamProvider
    {
        public int ConsumerInterfaceAcquisitions { get; private set; }
        public int ProducerInterfaceAcquisitions { get; private set; }

        IInternalAsyncObservable<T> IInternalStreamProvider.GetConsumerInterface<T>(IAsyncStream<T> streamId)
        {
            ConsumerInterfaceAcquisitions++;
            throw new InvalidOperationException("The lifecycle tests must not acquire the consumer interface.");
        }

        IInternalAsyncBatchObserver<T> IInternalStreamProvider.GetProducerInterface<T>(IAsyncStream<T> streamId)
        {
            ProducerInterfaceAcquisitions++;
            throw new InvalidOperationException("The lifecycle tests must not acquire the producer interface.");
        }
    }

    private sealed class RecordingObserver : IAsyncObserver<int>
    {
        public List<int> Items { get; } = [];
        public List<StreamSequenceToken?> Tokens { get; } = [];
        public List<Exception> Errors { get; } = [];
        public int CompletionCalls { get; private set; }

        public Task OnNextAsync(int item, StreamSequenceToken? token = null)
        {
            Items.Add(item);
            Tokens.Add(token);
            return Task.CompletedTask;
        }

        public Task OnCompletedAsync()
        {
            CompletionCalls++;
            return Task.CompletedTask;
        }

        public Task OnErrorAsync(Exception ex)
        {
            Errors.Add(ex);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingBatchObserver : IAsyncBatchObserver<int>
    {
        public List<IList<SequentialItem<int>>> Batches { get; } = [];
        public List<Exception> Errors { get; } = [];
        public List<bool> Completions { get; } = [];

        public Task OnNextAsync(IList<SequentialItem<int>> items)
        {
            Batches.Add(items.ToList());
            return Task.CompletedTask;
        }

        public Task OnCompletedAsync()
        {
            Completions.Add(true);
            return Task.CompletedTask;
        }

        public Task OnErrorAsync(Exception ex)
        {
            Errors.Add(ex);
            return Task.CompletedTask;
        }
    }

    private sealed class TestBatchContainer(
        StreamId streamId,
        StreamSequenceToken sequenceToken,
        int item) : IBatchContainer
    {
        public StreamId StreamId { get; } = streamId;
        public StreamSequenceToken SequenceToken { get; } = sequenceToken;

        public IEnumerable<Tuple<T, StreamSequenceToken>> GetEvents<T>()
        {
            yield return Tuple.Create((T)(object)item, SequenceToken);
        }

        public bool ImportRequestContext() => false;
    }

    private sealed class ForeignSubscriptionHandle(StreamId streamId) : StreamSubscriptionHandle<int>
    {
        public override Guid HandleId { get; } = Guid.NewGuid();
        public override string ProviderName => "phase-2-provider";
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
}
