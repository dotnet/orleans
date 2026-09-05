using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using NSubstitute;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;
using Xunit;

namespace UnitTests.StreamingTests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Streaming")]
public sealed class LegacySequenceTokenRecoveryTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public Task LegacyFactoryBaseToken_InitialHandshakeRecoversDerivedProviderEvents(bool legacyV2, bool providerV2)
        => LegacyTokenRecoveryFixture.VerifyRecovery(
            LegacyTokenRecoveryFixture.LoadLegacyToken(legacyV2),
            (sequence, index) => CreateProviderToken(providerV2, sequence, index),
            acknowledged: false,
            renegotiate: false,
            token => AssertMetadata(token, providerV2));

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public Task LegacyFactoryBaseToken_DeliveryHandshakeRecoversDerivedProviderEvents(bool v2, bool acknowledged)
        => LegacyTokenRecoveryFixture.VerifyRecovery(
            LegacyTokenRecoveryFixture.LoadLegacyToken(v2),
            (sequence, index) => CreateProviderToken(v2, sequence, index),
            acknowledged,
            renegotiate: true,
            token => AssertMetadata(token, v2));

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public Task LegacyAcknowledgedToken_RecognizesDerivedDuplicate(bool v2, bool batch)
        => LegacyTokenRecoveryFixture.VerifyDuplicate(
            LegacyTokenRecoveryFixture.LoadLegacyToken(v2),
            CreateProviderToken(v2, 500, 2),
            batch);

    [Fact]
    public void InheritedContracts_AreSymmetricTransitiveAndHashCompatibleAcrossVersions()
    {
        StreamSequenceToken[] tokens =
        [
            new EventSequenceToken(500, 2),
            new EventSequenceTokenV2(500, 2),
            new CustomV1Token(500, 2, "first"),
            new CustomV1Token(500, 2, "second"),
            new CustomV2Token(500, 2, "third"),
            new OtherV2Token(500, 2),
        ];

        foreach (var left in tokens)
        {
            foreach (var right in tokens)
            {
                Assert.True(left.Equals(right));
                Assert.True(left.Equals((object)right));
                Assert.True(((IEquatable<StreamSequenceToken?>)left).Equals(right));
                Assert.Equal(0, left.CompareTo(right));
                Assert.Equal(0, ((IComparable<StreamSequenceToken?>)left).CompareTo(right));
                Assert.Equal(left.GetHashCode(), right.GetHashCode());
            }

            Assert.True(left.CompareTo(new EventSequenceTokenV2(500, 1)) > 0);
            Assert.True(new EventSequenceTokenV2(500, 1).CompareTo(left) < 0);
            Assert.True(left.CompareTo(new CustomV1Token(501, 0, "later")) < 0);
            Assert.True(new CustomV1Token(501, 0, "later").CompareTo(left) > 0);
        }

        Assert.Single(new HashSet<StreamSequenceToken>(tokens));
        Assert.Single(new SortedSet<StreamSequenceToken>(Enumerable.Reverse(tokens)));
    }

    [Fact]
    public void OverridingAnyContractMember_PreservesSeparateCompatibility()
    {
        StreamSequenceToken[] overrides =
        [
            new ObjectEqualityToken(500, 2),
            new TokenEqualityToken(500, 2),
            new OrderingToken(500, 2),
            new HashingToken(500, 2),
            new InterfaceEqualityToken(500, 2),
            new InterfaceOrderingToken(500, 2),
        ];

        foreach (var token in overrides)
        {
            LegacyTokenRecoveryFixture.AssertProviderIsolation(token);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CurrentFactory_PreservesSubtypeMetadataAndOriginalBatch(bool v2)
    {
        var original = CreateProviderToken(v2, 500, 0);
        var perEvent = original switch
        {
            EventSequenceToken token => token.CreateSequenceTokenForEvent(2),
            EventSequenceTokenV2 token => (StreamSequenceToken)token.CreateSequenceTokenForEvent(2),
            _ => throw new InvalidOperationException(),
        };

        AssertMetadata(perEvent, v2);
        Assert.Equal(500, perEvent.SequenceNumber);
        Assert.Equal(2, perEvent.EventIndex);
        Assert.Equal(0, original.EventIndex);
        Assert.NotSame(original, perEvent);
    }

    private static StreamSequenceToken CreateProviderToken(bool v2, long sequence, int index)
        => v2
            ? new CustomV2Token(sequence, index, "provider-metadata")
            : new CustomV1Token(sequence, index, "provider-metadata");

    private static void AssertMetadata(StreamSequenceToken token, bool v2)
    {
        var metadata = v2
            ? Assert.IsType<CustomV2Token>(token).Metadata
            : Assert.IsType<CustomV1Token>(token).Metadata;
        Assert.Equal("provider-metadata", metadata);
    }

    private sealed class CustomV1Token(long sequence, int index, string metadata) : EventSequenceToken(sequence, index)
    {
        public string Metadata { get; } = metadata;
    }

    private sealed class CustomV2Token(long sequence, int index, string metadata) : EventSequenceTokenV2(sequence, index)
    {
        public string Metadata { get; } = metadata;
    }

    private sealed class OtherV2Token(long sequence, int index) : EventSequenceTokenV2(sequence, index);

    private sealed class ObjectEqualityToken(long sequence, int index) : EventSequenceTokenV2(sequence, index)
    {
        public override bool Equals(object? obj) => base.Equals(obj);
        public override int GetHashCode() => base.GetHashCode();
    }

    private sealed class TokenEqualityToken(long sequence, int index) : EventSequenceTokenV2(sequence, index)
    {
        public override bool Equals(StreamSequenceToken? other) => base.Equals(other);
    }

    private sealed class OrderingToken(long sequence, int index) : EventSequenceTokenV2(sequence, index)
    {
        public override int CompareTo(StreamSequenceToken? other) => base.CompareTo(other);
    }

    private sealed class HashingToken(long sequence, int index) : EventSequenceTokenV2(sequence, index)
    {
        public override int GetHashCode() => base.GetHashCode();
    }

    private sealed class InterfaceEqualityToken(long sequence, int index)
        : EventSequenceTokenV2(sequence, index), IEquatable<StreamSequenceToken?>
    {
        bool IEquatable<StreamSequenceToken?>.Equals(StreamSequenceToken? other) => base.Equals(other);
    }

    private sealed class InterfaceOrderingToken(long sequence, int index)
        : EventSequenceTokenV2(sequence, index), IComparable<StreamSequenceToken?>
    {
        int IComparable<StreamSequenceToken?>.CompareTo(StreamSequenceToken? other) => base.CompareTo(other);
    }
}

public static class LegacyTokenRecoveryFixture
{
    public static StreamSequenceToken LoadLegacyToken(bool v2)
    {
        // Earlier inherited factories persisted an exact base token, even for a custom provider.
        const string json = """{"SequenceNumber":500,"EventIndex":2}""";
        return v2
            ? Assert.IsType<EventSequenceTokenV2>(JsonConvert.DeserializeObject<EventSequenceTokenV2>(json))
            : Assert.IsType<EventSequenceToken>(JsonConvert.DeserializeObject<EventSequenceToken>(json));
    }

    public static async Task VerifyRecovery(
        StreamSequenceToken legacy,
        Func<long, int, StreamSequenceToken> createToken,
        bool acknowledged,
        bool renegotiate,
        Action<StreamSequenceToken> assertMetadata)
    {
        var currentPosition = createToken(legacy.SequenceNumber, legacy.EventIndex);
        Assert.Equal(0, EventSequenceTokenCompatibility.Compare(legacy, currentPosition));
        Assert.Equal(0, EventSequenceTokenCompatibility.Compare(currentPosition, legacy));
        Assert.True(EventSequenceTokenCompatibility.AreEqual(legacy, currentPosition));
        Assert.True(EventSequenceTokenCompatibility.AreEqual(currentPosition, legacy));

        var streamId = StreamId.Create("legacy-recovery", Guid.NewGuid());
        var adapter = new RecoveryDataAdapter(createToken);
        var cache = new PooledQueueCache(adapter, NullLogger.Instance, null, null);
        var observer = new RecordingObserver();
        var handshake = acknowledged
            ? StreamHandshakeToken.CreateDeliveyToken(legacy)
            : StreamHandshakeToken.CreateStartToken(legacy);
        var handle = CreateHandle(streamId, observer, handshake);
        Add(100, 0);
        var cursor = cache.GetCursor(streamId, handle.GetSequenceToken()!.Token);

        Assert.False(cache.TryGetNextMessage(cursor, out _));
        foreach (var sequence in new[] { 101L, 102L })
        {
            Add(sequence, 0);
            // This is the real Refresh path invoked by StartInactiveCursors after every nonempty read.
            cache.Refresh(cursor, createToken(sequence, 0));
            Assert.False(cache.TryGetNextMessage(cursor, out _));
            Assert.Empty(observer.Tokens);
        }

        Add(499, 0);
        for (var index = 0; index < 4; index++)
        {
            Add(500, index);
        }

        Add(501, 0);
        cache.Refresh(cursor, createToken(499, 0));
        SkipAcknowledgedPosition();
        Assert.True(cache.TryGetNextMessage(cursor, out var first));
        Assert.Equal(500, first.SequenceToken.SequenceNumber);
        Assert.Equal(acknowledged ? 3 : 2, first.SequenceToken.EventIndex);

        if (renegotiate)
        {
            var requested = await handle.DeliverBatch(first, handshakeToken: null);
            Assert.Same(handshake, requested);
            Assert.Same(legacy, requested!.Token);
            Assert.Empty(observer.Tokens);
            cursor = cache.GetCursor(streamId, requested.Token);
            SkipAcknowledgedPosition();
            Assert.True(cache.TryGetNextMessage(cursor, out first));
            Assert.Equal(acknowledged ? 3 : 2, first.SequenceToken.EventIndex);
        }

        // An active refresh keeps its position.
        cache.Refresh(cursor, createToken(700, 0));
        cache.Refresh(cursor, createToken(100, 0));
        Assert.Null(await handle.DeliverBatch(first, handshake));

        while (cache.TryGetNextMessage(cursor, out var batch))
        {
            Assert.Null(await handle.DeliverBatch(batch, handle.GetSequenceToken()));
        }

        Assert.Equal(
            acknowledged ? new[] { 5003, 5010 } : new[] { 5002, 5003, 5010 },
            observer.Items);
        Assert.All(observer.Tokens, assertMetadata);
        var finalToken = Assert.IsType<DeliveryToken>(handle.GetSequenceToken()).Token;
        Assert.Same(observer.Tokens[^1], finalToken);
        Assert.Equal(501, finalToken!.SequenceNumber);
        Assert.Equal(0, finalToken.EventIndex);
        Assert.Equal(500, legacy.SequenceNumber);
        Assert.Equal(2, legacy.EventIndex);

        void SkipAcknowledgedPosition()
        {
            if (!acknowledged)
            {
                return;
            }

            Assert.True(cache.TryGetNextMessage(cursor, out var duplicate));
            Assert.Equal(0, EventSequenceTokenCompatibility.Compare(legacy, duplicate.SequenceToken));
            Assert.Equal(0, EventSequenceTokenCompatibility.Compare(duplicate.SequenceToken, legacy));
        }

        void Add(long sequence, int index)
        {
            var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            cache.Add(
            [
                new CachedMessage
                {
                    StreamId = streamId,
                    SequenceNumber = sequence,
                    EventIndex = index,
                    EnqueueTimeUtc = now,
                    DequeueTimeUtc = now,
                },
            ], now);
        }
    }

    public static async Task VerifyDuplicate(StreamSequenceToken legacy, StreamSequenceToken current, bool batch)
    {
        var streamId = StreamId.Create("legacy-duplicate", Guid.NewGuid());
        var observer = new RecordingObserver();
        var acknowledged = StreamHandshakeToken.CreateDeliveyToken(legacy);
        var handle = CreateHandle(streamId, observer, acknowledged);

        var result = batch
            ? await handle.DeliverBatch(new RecoveryBatch(streamId, current), acknowledged)
            : await handle.DeliverItem(5002, current, acknowledged);

        Assert.Same(acknowledged, result);
        Assert.Same(acknowledged, handle.GetSequenceToken());
        Assert.Empty(observer.Items);
        Assert.Empty(observer.Tokens);
    }

    public static void AssertProviderIsolation(StreamSequenceToken provider)
    {
        StreamSequenceToken[] genericTokens =
        [
            new EventSequenceToken(provider.SequenceNumber, provider.EventIndex),
            new EventSequenceTokenV2(provider.SequenceNumber, provider.EventIndex),
            new InheritedV1Token(provider.SequenceNumber, provider.EventIndex),
            new InheritedV2Token(provider.SequenceNumber, provider.EventIndex),
        ];
        foreach (var generic in genericTokens)
        {
            AssertRecoveryIncompatible(provider, generic);
        }
    }

    public static void AssertRecoveryIncompatible(StreamSequenceToken provider, StreamSequenceToken generic)
    {
        Assert.False(generic.Equals(provider));
        Assert.False(provider.Equals(generic));
        Assert.False(generic.Equals((object)provider));
        Assert.False(provider.Equals((object)generic));
        Assert.False(EventSequenceTokenCompatibility.AreEqual(generic, provider));
        Assert.False(EventSequenceTokenCompatibility.AreEqual(provider, generic));
        Assert.Throws<ArgumentOutOfRangeException>(() => generic.CompareTo(provider));
        Assert.Throws<ArgumentOutOfRangeException>(() => provider.CompareTo(generic));
        Assert.Throws<ArgumentOutOfRangeException>(() => EventSequenceTokenCompatibility.Compare(generic, provider));
        Assert.Throws<ArgumentOutOfRangeException>(() => EventSequenceTokenCompatibility.Compare(provider, generic));
        Assert.Equal(2, new HashSet<StreamSequenceToken> { generic, provider }.Count);
    }

    private sealed class InheritedV1Token(long sequence, int index) : EventSequenceToken(sequence, index);
    private sealed class InheritedV2Token(long sequence, int index) : EventSequenceTokenV2(sequence, index);

    private static StreamSubscriptionHandleImpl<int> CreateHandle(
        StreamId streamId,
        RecordingObserver observer,
        StreamHandshakeToken? handshake)
    {
        var stream = new StreamImpl<int>(
            new QualifiedStreamId("legacy-provider", streamId),
            new RecoveryStreamProvider(),
            isRewindable: true,
            Substitute.For<IRuntimeClient>());
        return new StreamSubscriptionHandleImpl<int>(
            GuidId.GetGuidId(SubscriptionMarker.MarkAsExplicitSubscriptionId(Guid.NewGuid())),
            observer,
            batchObserver: null,
            stream,
            token: null,
            startPosition: null,
            filterData: null,
            handshakeState: new() { Token = handshake },
            clusterId: "legacy-cluster");
    }

    private sealed class RecoveryStreamProvider : IInternalStreamProvider
    {
        public IInternalAsyncBatchObserver<T> GetProducerInterface<T>(IAsyncStream<T> streamId)
            => throw new NotSupportedException();

        public IInternalAsyncObservable<T> GetConsumerInterface<T>(IAsyncStream<T> streamId)
            => throw new NotSupportedException();
    }

    private sealed class RecoveryDataAdapter(Func<long, int, StreamSequenceToken> createToken) : ICacheDataAdapter
    {
        public StreamSequenceToken GetSequenceToken(ref CachedMessage message)
            => createToken(message.SequenceNumber, message.EventIndex);

        public IBatchContainer GetBatchContainer(ref CachedMessage message)
            => new RecoveryBatch(message.StreamId, GetSequenceToken(ref message));
    }

    private sealed class RecoveryBatch(StreamId streamId, StreamSequenceToken token) : IBatchContainer
    {
        public StreamId StreamId => streamId;
        public StreamSequenceToken SequenceToken => token;
        public bool ImportRequestContext() => false;

        public IEnumerable<Tuple<T, StreamSequenceToken>> GetEvents<T>()
        {
            if ((int)(token.SequenceNumber * 10 + token.EventIndex) is T value)
            {
                yield return Tuple.Create(value, token);
            }
        }
    }

    private sealed class RecordingObserver : IAsyncObserver<int>
    {
        public List<int> Items { get; } = [];
        public List<StreamSequenceToken> Tokens { get; } = [];

        public Task OnNextAsync(int item, StreamSequenceToken? token = null)
        {
            Items.Add(item);
            Tokens.Add(Assert.IsAssignableFrom<StreamSequenceToken>(token));
            return Task.CompletedTask;
        }

        public Task OnCompletedAsync() => Task.CompletedTask;
        public Task OnErrorAsync(Exception exception) => Task.FromException(exception);
    }
}
