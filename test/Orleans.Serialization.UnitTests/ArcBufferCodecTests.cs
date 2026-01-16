using System;
using System.Buffers;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Session;
using Xunit;

namespace Orleans.Serialization.UnitTests;

/// <summary>
/// Tests for ArcBufferCodec, which provides serialization and deserialization for ArcBuffer instances.
/// ArcBuffer is a reference-counted, immutable buffer type used for zero-copy message passing in Orleans.
/// </summary>
[Trait("Category", "BVT")]
public class ArcBufferCodecTests
{
    private readonly ServiceProvider _serviceProvider;
    private readonly Serializer<ArcBuffer> _serializer;
    private readonly DeepCopier<ArcBuffer> _copier;

#if NET6_0_OR_GREATER
    private readonly Random _random = Random.Shared;
#else
    private readonly Random _random = new Random();
#endif

    public ArcBufferCodecTests()
    {
        var services = new ServiceCollection();
        _ = services.AddSerializer();
        _serviceProvider = services.BuildServiceProvider();
        _serializer = _serviceProvider.GetRequiredService<Serializer<ArcBuffer>>();
        _copier = _serviceProvider.GetRequiredService<DeepCopier<ArcBuffer>>();
    }

    /// <summary>
    /// Tests round-trip serialization and deserialization of an empty ArcBuffer.
    /// </summary>
    [Fact]
    public void ArcBufferCodec_EmptyBuffer_RoundTrip()
    {
        var original = default(ArcBuffer);
        var serialized = _serializer.SerializeToArray(original);
        var deserialized = _serializer.Deserialize(serialized);

        Assert.Equal(0, original.Length);
        Assert.Equal(0, deserialized.Length);
    }

    /// <summary>
    /// Tests round-trip serialization of a single-page ArcBuffer.
    /// </summary>
    [Fact]
    public void ArcBufferCodec_SinglePageBuffer_RoundTrip()
    {
        var data = new byte[256];
        _random.NextBytes(data);

        using var writer = new ArcBufferWriter();
        writer.Write(data);
        using var original = writer.ConsumeSlice(data.Length);

        var serialized = _serializer.SerializeToArray(original);
        using var deserialized = _serializer.Deserialize(serialized);

        Assert.Equal(original.Length, deserialized.Length);
        Assert.Equal(data, deserialized.ToArray());
    }

    /// <summary>
    /// Tests round-trip serialization of a multi-page ArcBuffer.
    /// </summary>
    [Fact]
    public void ArcBufferCodec_MultiPageBuffer_RoundTrip()
    {
        var pageSize = ArcBufferWriter.MinimumPageSize;
        var data = new byte[pageSize * 3 + 100]; // 3+ pages
        _random.NextBytes(data);

        using var writer = new ArcBufferWriter();
        writer.Write(data);
        using var original = writer.ConsumeSlice(data.Length);

        var serialized = _serializer.SerializeToArray(original);
        using var deserialized = _serializer.Deserialize(serialized);

        Assert.Equal(original.Length, deserialized.Length);
        Assert.Equal(data, deserialized.ToArray());
    }

    /// <summary>
    /// Tests that serialization correctly handles buffers with different segment sizes.
    /// </summary>
    [Fact]
    public void ArcBufferCodec_VariableSegmentSizes_RoundTrip()
    {
        var totalSize = 5000;
        var data = new byte[totalSize];
        _random.NextBytes(data);

        using var writer = new ArcBufferWriter();
        int written = 0;
        int[] chunkSizes = { 100, 500, 1000, 2000, 1400 };

        foreach (var chunkSize in chunkSizes)
        {
            var actualChunkSize = Math.Min(chunkSize, totalSize - written);
            writer.Write(data.AsSpan(written, actualChunkSize));
            written += actualChunkSize;
        }

        using var original = writer.ConsumeSlice(totalSize);

        var serialized = _serializer.SerializeToArray(original);
        using var deserialized = _serializer.Deserialize(serialized);

        Assert.Equal(original.Length, deserialized.Length);
        Assert.Equal(data, deserialized.ToArray());
    }

    /// <summary>
    /// Tests that slicing an ArcBuffer preserves data through serialization.
    /// </summary>
    [Fact]
    public void ArcBufferCodec_SlicedBuffer_RoundTrip()
    {
        var data = new byte[1000];
        _random.NextBytes(data);

        using var writer = new ArcBufferWriter();
        writer.Write(data);
        using var fullBuffer = writer.ConsumeSlice(data.Length);

        // Create a slice from the middle
        using var sliced = fullBuffer.Slice(100, 500);

        var serialized = _serializer.SerializeToArray(sliced);
        using var deserialized = _serializer.Deserialize(serialized);

        Assert.Equal(500, sliced.Length);
        Assert.Equal(500, deserialized.Length);
        Assert.Equal(data.AsSpan(100, 500).ToArray(), deserialized.ToArray());
    }

    /// <summary>
    /// Tests that the copier performs a shallow copy (reference-counted).
    /// </summary>
    [Fact]
    public void ArcBufferCopier_ShallowCopy_PreservesData()
    {
        var data = new byte[512];
        _random.NextBytes(data);

        using var writer = new ArcBufferWriter();
        writer.Write(data);
        using var original = writer.ConsumeSlice(data.Length);

        using var copied = _copier.Copy(original);

        Assert.Equal(original.Length, copied.Length);
        Assert.Equal(data, copied.ToArray());
    }

    /// <summary>
    /// Tests that the copier handles empty buffers correctly.
    /// </summary>
    [Fact]
    public void ArcBufferCopier_EmptyBuffer_PreservesEmptyState()
    {
        var original = default(ArcBuffer);
        var copied = _copier.Copy(original);

        Assert.Equal(0, original.Length);
        Assert.Equal(0, copied.Length);
    }

    /// <summary>
    /// Tests serialization of very small buffers (1 byte).
    /// </summary>
    [Fact]
    public void ArcBufferCodec_SingleByte_RoundTrip()
    {
        using var writer = new ArcBufferWriter();
        writer.Write(new byte[] { 42 });
        using var original = writer.ConsumeSlice(1);

        var serialized = _serializer.SerializeToArray(original);
        using var deserialized = _serializer.Deserialize(serialized);

        Assert.Equal(1, deserialized.Length);
        Assert.Equal(42, deserialized.ToArray()[0]);
    }

    /// <summary>
    /// Tests serialization of large buffers (64KB+).
    /// </summary>
    [Fact]
    public void ArcBufferCodec_LargeBuffer_RoundTrip()
    {
        var largeSize = 65536 + 1234; // > 64KB
        var data = new byte[largeSize];
        _random.NextBytes(data);

        using var writer = new ArcBufferWriter();
        writer.Write(data);
        using var original = writer.ConsumeSlice(data.Length);

        var serialized = _serializer.SerializeToArray(original);
        using var deserialized = _serializer.Deserialize(serialized);

        Assert.Equal(original.Length, deserialized.Length);
        Assert.Equal(data, deserialized.ToArray());
    }

    /// <summary>
    /// Tests that serialization works with span enumeration.
    /// </summary>
    [Fact]
    public void ArcBufferCodec_VerifySpanEnumeration_DuringWrite()
    {
        var pageSize = ArcBufferWriter.MinimumPageSize;
        var data = new byte[pageSize * 2 + 100]; // Multiple pages
        _random.NextBytes(data);

        using var writer = new ArcBufferWriter();
        writer.Write(data);
        using var original = writer.ConsumeSlice(data.Length);

        // Verify the original has multiple segments
        var segmentCount = 0;
        foreach (var segment in original.SpanSegments)
        {
            segmentCount++;
        }
        Assert.True(segmentCount > 1, "Expected multi-segment buffer for this test");

        // Serialize and deserialize
        var serialized = _serializer.SerializeToArray(original);
        using var deserialized = _serializer.Deserialize(serialized);

        // Verify content matches despite different segmentation
        Assert.Equal(original.Length, deserialized.Length);
        Assert.Equal(data, deserialized.ToArray());
    }

    /// <summary>
    /// Tests that the codec is properly registered with Orleans serialization.
    /// </summary>
    [Fact]
    public void ArcBufferCodec_IsRegistered()
    {
        var codec = _serviceProvider.GetService<IFieldCodec<ArcBuffer>>();
        Assert.NotNull(codec);
        // Note: Orleans may wrap the codec in a holder type, so we just verify it's not null
        // The actual functionality is verified by the round-trip tests above
    }

    /// <summary>
    /// Tests that the copier is properly registered with Orleans serialization.
    /// </summary>
    [Fact]
    public void ArcBufferCopier_IsRegistered()
    {
        var copier = _serviceProvider.GetService<IDeepCopier<ArcBuffer>>();
        Assert.NotNull(copier);
        // Note: Orleans may wrap the copier in a holder type, so we just verify it's not null
        // The actual functionality is verified by the copy tests above
    }

    /// <summary>
    /// Tests that the copier is marked as supporting shallow copy.
    /// </summary>
    [Fact]
    public void ArcBufferCopier_IsShallowCopyable()
    {
        var copier = _serviceProvider.GetService<IDeepCopier<ArcBuffer>>();
        Assert.NotNull(copier);
        Assert.IsAssignableFrom<IOptionalDeepCopier>(copier);
        
        var optionalCopier = (IOptionalDeepCopier)copier;
        Assert.True(optionalCopier.IsShallowCopyable());
    }
}
