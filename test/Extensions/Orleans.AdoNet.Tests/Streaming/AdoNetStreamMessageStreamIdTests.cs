using System.Text;
using Orleans.Runtime;
using Orleans.Streaming.AdoNet;

namespace Tester.AdoNet.Streaming;

/// <summary>
/// Tests that <see cref="AdoNetStreamMessage.StreamId"/> reconstructs the canonical
/// <see cref="StreamId.FullKey"/> bytes and namespace boundary exactly as they would be
/// persisted in the <c>StreamIdBytes</c>/<c>StreamNamespaceLength</c> columns of
/// <c>OrleansStreamMessage</c>. These tests require no database and always run.
/// </summary>
[TestCategory("AdoNet"), TestCategory("Streaming"), TestCategory("BVT")]
[TestProvider("None")]
[TestSuite("BVT")]
[TestArea("Streaming")]
public sealed class AdoNetStreamMessageStreamIdTests
{
    private static AdoNetStreamMessage CreateMessage(StreamId streamId, long messageId = 1) =>
        new(
            ServiceId: "service",
            ProviderId: "provider",
            QueueId: "queue",
            MessageId: messageId,
            StreamIdBytes: streamId.FullKey.ToArray(),
            StreamNamespaceLength: streamId.Namespace.Length,
            CreatedOn: DateTime.UtcNow,
            Payload: [1, 2, 3]);

    [Fact]
    public void StreamId_RoundTrips_WithNamespace()
    {
        var original = StreamId.Create("orders-namespace", Guid.NewGuid());

        var message = CreateMessage(original);

        Assert.Equal(original, message.StreamId);
        Assert.True(original.FullKey.Span.SequenceEqual(message.StreamIdBytes));
        Assert.Equal(original.Namespace.Length, message.StreamNamespaceLength);
        Assert.True(original.Namespace.Span.SequenceEqual(message.StreamId.Namespace.Span));
        Assert.True(original.Key.Span.SequenceEqual(message.StreamId.Key.Span));
    }

    [Fact]
    public void StreamId_RoundTrips_WithoutNamespace()
    {
        var original = StreamId.Create(ns: null, key: Guid.NewGuid());

        var message = CreateMessage(original);

        Assert.Equal(0, original.Namespace.Length);
        Assert.Equal(0, message.StreamNamespaceLength);
        Assert.Equal(original, message.StreamId);
        Assert.True(original.Key.Span.SequenceEqual(message.StreamId.Key.Span));
        Assert.True(original.FullKey.Span.SequenceEqual(message.StreamId.FullKey.Span));
    }

    [Fact]
    public void StreamId_RoundTrips_WithStringKeyAndNamespace()
    {
        var original = StreamId.Create("tenant/42", "order-key-123");

        var message = CreateMessage(original);

        Assert.Equal(original, message.StreamId);
        Assert.Equal(Encoding.UTF8.GetByteCount("tenant/42"), message.StreamNamespaceLength);
        Assert.Equal("order-key-123", Encoding.UTF8.GetString(message.StreamId.Key.Span));
    }

    [Theory]
    [InlineData(null, "just-a-key")]
    [InlineData("", "key-with-empty-namespace")]
    [InlineData("ns", "k")]
    [InlineData("namespace-with-unicode-\u00e9\u00e8", "key-with-unicode-\u00fc")]
    public void StreamId_NamespaceBoundary_SeparatesNamespaceAndKeyExactly(string? ns, string key)
    {
        var original = StreamId.Create(ns, key);
        var message = CreateMessage(original);

        // Re-derive the namespace/key split purely from the stored bytes and boundary,
        // exactly as a storage-layer reader would, rather than trusting StreamId.Equals
        // (which compares only the full key bytes and ignores the namespace boundary).
        var namespaceBytes = message.StreamIdBytes.AsSpan(0, message.StreamNamespaceLength).ToArray();
        var keyBytes = message.StreamIdBytes.AsSpan(message.StreamNamespaceLength).ToArray();

        Assert.True(original.Namespace.Span.SequenceEqual(namespaceBytes));
        Assert.True(original.Key.Span.SequenceEqual(keyBytes));
        Assert.Equal(key, Encoding.UTF8.GetString(keyBytes));
    }

    [Fact]
    public void StreamId_DifferentNamespaceBoundary_ProducesDifferentNamespaceAndKeySplit()
    {
        // StreamId.Equals compares only the full key bytes, so an off-by-one boundary mutation
        // would NOT be caught by an equality assertion alone. Guard the boundary explicitly by
        // asserting the derived Namespace/Key spans themselves differ when the stored
        // StreamNamespaceLength is shifted by one byte.
        var original = StreamId.Create("ns", "key");
        var shiftedMessage = new AdoNetStreamMessage(
            "service", "provider", "queue", 1,
            original.FullKey.ToArray(),
            original.Namespace.Length + 1,
            DateTime.UtcNow,
            [1]);

        var shifted = shiftedMessage.StreamId;

        // Sanity: the underlying full key bytes are unchanged.
        Assert.True(original.FullKey.Span.SequenceEqual(shifted.FullKey.Span));

        // But the namespace/key split has moved, and must be observably different.
        Assert.NotEqual(original.Namespace.Length, shifted.Namespace.Length);
        Assert.False(original.Namespace.Span.SequenceEqual(shifted.Namespace.Span));
        Assert.False(original.Key.Span.SequenceEqual(shifted.Key.Span));
    }

    [Fact]
    public void StreamId_FullKey_IsExactByteConcatenationOfNamespaceAndKey()
    {
        var original = StreamId.Create("orders", "order-42");
        var message = CreateMessage(original);

        var reconstructedFullKey = message.StreamIdBytes;
        var expectedFullKey = new byte[original.Namespace.Length + original.Key.Length];
        original.Namespace.Span.CopyTo(expectedFullKey.AsSpan(0, original.Namespace.Length));
        original.Key.Span.CopyTo(expectedFullKey.AsSpan(original.Namespace.Length));

        Assert.Equal(expectedFullKey, reconstructedFullKey);
        Assert.Equal(original.Namespace.Length + original.Key.Length, reconstructedFullKey.Length);
    }
}
