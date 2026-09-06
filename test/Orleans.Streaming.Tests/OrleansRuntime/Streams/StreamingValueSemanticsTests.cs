using System.Reflection;
using System.Runtime.CompilerServices;
using Orleans;
using Orleans.Providers;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;
using Xunit;

namespace UnitTests.OrleansRuntime.Streams;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Streaming")]
[TestCategory("BVT"), TestCategory("Streaming")]
public class StreamingValueSemanticsTests
{
    [Fact]
    public void CachedMessageUsesAllFieldsAndSegmentIdentity()
    {
        AssertEqual(default(CachedMessage), default);

        byte[] sharedPayload = [1, 2, 3, 4];
        byte[] copiedPayload = [1, 2, 3, 4];
        var original = CreateCachedMessage(new ArraySegment<byte>(sharedPayload, 1, 2));
        var equal = CreateCachedMessage(new ArraySegment<byte>(sharedPayload, 1, 2));

        Assert.Same(original.Segment.Array, equal.Segment.Array);
        AssertEqual(original, equal);
        AssertNotEqual(original, CreateCachedMessage(new ArraySegment<byte>(copiedPayload, 1, 2)));
        AssertNotEqual(original, CreateCachedMessage(original.Segment, sequenceNumber: 11));
        AssertNotEqual(original, CreateCachedMessage(original.Segment, eventIndex: 3));
        AssertNotEqual(original, CreateCachedMessage(original.Segment, enqueueTimeUtc: original.EnqueueTimeUtc.AddTicks(1)));
        AssertNotEqual(original, CreateCachedMessage(original.Segment, dequeueTimeUtc: original.DequeueTimeUtc.AddTicks(1)));
        AssertNotEqual(original, CreateCachedMessage(original.Segment, streamId: StreamId.Create("other", "key")));
        AssertHashCollections(original, equal, CreateCachedMessage(new ArraySegment<byte>(copiedPayload, 1, 2)));
    }

    [Fact]
    public void QueueCacheMissInfoUsesStoredMemberEquality()
    {
        AssertEqual(default(QueueCacheMissInfo), new QueueCacheMissInfo((string?)null, null, null));

        var requested = new string("requested".ToCharArray());
        var equalRequested = new string("requested".ToCharArray());
        Assert.NotSame(requested, equalRequested);
        var original = new QueueCacheMissInfo(requested, "low", "high");
        var equal = new QueueCacheMissInfo(equalRequested, "low", "high");
        AssertEqual(original, equal);

        var token = new IdentityToken(1, "token");
        var sameToken = new QueueCacheMissInfo(token, token, token);
        AssertEqual(sameToken, new QueueCacheMissInfo(token, token, token));
        AssertNotEqual(sameToken, new QueueCacheMissInfo(new IdentityToken(1, "token"), token, token));
        AssertNotEqual(original, new QueueCacheMissInfo("different", "low", "high"));
        AssertNotEqual(original, new QueueCacheMissInfo("requested", "different", "high"));
        AssertNotEqual(original, new QueueCacheMissInfo("requested", "low", "different"));
        AssertHashCollections(original, equal, new QueueCacheMissInfo("different", "low", "high"));
    }

    [Fact]
    public void QueueCacheCursorResultUsesKindCursorAndCacheMiss()
    {
        AssertEqual(default(QueueCacheCursorResult<TestCursor>), default);

        var cursor = new TestCursor();
        var cursorResult = QueueCacheCursorResult<TestCursor>.FromCursor(cursor);
        var equalCursorResult = QueueCacheCursorResult<TestCursor>.FromCursor(cursor);
        Assert.Same(cursorResult.Cursor, equalCursorResult.Cursor);
        AssertEqual(cursorResult, equalCursorResult);
        AssertNotEqual(cursorResult, QueueCacheCursorResult<TestCursor>.FromCursor(new TestCursor()));

        var miss = new QueueCacheMissInfo("requested", "low", "high");
        var missResult = QueueCacheCursorResult<TestCursor>.FromCacheMiss(miss);
        var equalMissResult = QueueCacheCursorResult<TestCursor>.FromCacheMiss(miss);
        AssertEqual(missResult, equalMissResult);
        AssertNotEqual(missResult, QueueCacheCursorResult<TestCursor>.FromCacheMiss(new("different", "low", "high")));
        AssertNotEqual(missResult, QueueCacheCursorResult<TestCursor>.NotSupported);
        AssertEqual(QueueCacheCursorResult<TestCursor>.NotSupported, QueueCacheCursorResult<TestCursor>.NotSupported);
        AssertHashCollections(cursorResult, equalCursorResult, QueueCacheCursorResult<TestCursor>.FromCursor(new TestCursor()));
    }

    [Fact]
    public void QueueCacheCursorMoveResultUsesKindAndCacheMiss()
    {
        AssertEqual(default(QueueCacheCursorMoveResult), default);
        AssertEqual(QueueCacheCursorMoveResult.Success, QueueCacheCursorMoveResult.Success);
        AssertEqual(QueueCacheCursorMoveResult.NoData, QueueCacheCursorMoveResult.NoData);
        AssertNotEqual(QueueCacheCursorMoveResult.Success, QueueCacheCursorMoveResult.NoData);

        var miss = new QueueCacheMissInfo("requested", "low", "high");
        var missResult = QueueCacheCursorMoveResult.FromCacheMiss(miss);
        var equalMissResult = QueueCacheCursorMoveResult.FromCacheMiss(miss);
        AssertEqual(missResult, equalMissResult);
        AssertNotEqual(missResult, QueueCacheCursorMoveResult.FromCacheMiss(new("different", "low", "high")));
        AssertHashCollections(missResult, equalMissResult, QueueCacheCursorMoveResult.NoData);
    }

    [Fact]
    public void MemoryMessageDataSerializationIdsArePreserved()
    {
        Assert.Equal(0U, GetId(nameof(MemoryMessageData.StreamId)));
        Assert.Equal(1U, GetId(nameof(MemoryMessageData.SequenceNumber)));
        Assert.Equal(2U, GetId(nameof(MemoryMessageData.DequeueTimeUtc)));
        Assert.Equal(3U, GetId(nameof(MemoryMessageData.EnqueueTimeUtc)));
        Assert.Equal(4U, GetId(nameof(MemoryMessageData.Payload)));
    }

    private static CachedMessage CreateCachedMessage(
        ArraySegment<byte> segment,
        StreamId? streamId = null,
        long sequenceNumber = 10,
        int eventIndex = 2,
        DateTime? enqueueTimeUtc = null,
        DateTime? dequeueTimeUtc = null) =>
        new()
        {
            StreamId = streamId ?? StreamId.Create("namespace", "key"),
            SequenceNumber = sequenceNumber,
            EventIndex = eventIndex,
            EnqueueTimeUtc = enqueueTimeUtc ?? new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            DequeueTimeUtc = dequeueTimeUtc ?? new DateTime(2026, 1, 2, 3, 4, 6, DateTimeKind.Utc),
            Segment = segment,
        };

    private static uint GetId(string fieldName)
    {
        var field = typeof(MemoryMessageData).GetField(fieldName);
        Assert.NotNull(field);
        var attribute = field.GetCustomAttribute<IdAttribute>();
        Assert.NotNull(attribute);
        return attribute.Id;
    }

    private static void AssertEqual(CachedMessage left, CachedMessage right)
    {
        AssertEqualContract(left, right);
        Assert.True(left == right);
        Assert.False(left != right);
    }

    private static void AssertNotEqual(CachedMessage left, CachedMessage right)
    {
        AssertNotEqualContract(left, right);
        Assert.False(left == right);
        Assert.True(left != right);
    }

    private static void AssertEqual(QueueCacheMissInfo left, QueueCacheMissInfo right)
    {
        AssertEqualContract(left, right);
        Assert.True(left == right);
        Assert.False(left != right);
    }

    private static void AssertNotEqual(QueueCacheMissInfo left, QueueCacheMissInfo right)
    {
        AssertNotEqualContract(left, right);
        Assert.False(left == right);
        Assert.True(left != right);
    }

    private static void AssertEqual(QueueCacheCursorResult<TestCursor> left, QueueCacheCursorResult<TestCursor> right)
    {
        AssertEqualContract(left, right);
        Assert.True(left == right);
        Assert.False(left != right);
    }

    private static void AssertNotEqual(QueueCacheCursorResult<TestCursor> left, QueueCacheCursorResult<TestCursor> right)
    {
        AssertNotEqualContract(left, right);
        Assert.False(left == right);
        Assert.True(left != right);
    }

    private static void AssertEqual(QueueCacheCursorMoveResult left, QueueCacheCursorMoveResult right)
    {
        AssertEqualContract(left, right);
        Assert.True(left == right);
        Assert.False(left != right);
    }

    private static void AssertNotEqual(QueueCacheCursorMoveResult left, QueueCacheCursorMoveResult right)
    {
        AssertNotEqualContract(left, right);
        Assert.False(left == right);
        Assert.True(left != right);
    }

    private static void AssertEqualContract<T>(T left, T right)
        where T : struct, IEquatable<T>
    {
        Assert.Equal(left, right);
        Assert.True(left.Equals(right));
        Assert.True(((object)left).Equals(right));
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
    }

    private static void AssertNotEqualContract<T>(T left, T right)
        where T : struct, IEquatable<T>
    {
        Assert.NotEqual(left, right);
        Assert.False(left.Equals(right));
        Assert.False(((object)left).Equals(right));
    }

    private static void AssertHashCollections<T>(T original, T equal, T unequal)
        where T : notnull
    {
        var set = new HashSet<T> { original };
        var dictionary = new Dictionary<T, string> { [original] = "stored" };

        Assert.Contains(equal, set);
        Assert.DoesNotContain(unequal, set);
        Assert.Equal("stored", dictionary[equal]);
        Assert.False(dictionary.ContainsKey(unequal));
    }

    private sealed class TestCursor;

    private sealed class IdentityToken(long sequenceNumber, string text) : StreamSequenceToken
    {
        public override long SequenceNumber { get; protected set; } = sequenceNumber;
        public override int EventIndex { get; protected set; }
        public override bool Equals(StreamSequenceToken? other) => ReferenceEquals(this, other);
        public override bool Equals(object? obj) => ReferenceEquals(this, obj);
        public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);
        public override int CompareTo(StreamSequenceToken? other) => ReferenceEquals(this, other) ? 0 : 1;
        public override string ToString() => text;
    }

}
