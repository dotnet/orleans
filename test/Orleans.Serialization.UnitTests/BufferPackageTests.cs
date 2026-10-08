using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization.Buffers;

namespace Orleans.Serialization.UnitTests;

[Trait("Category", "BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Serialization")]
public sealed class BufferPackageTests
{
    [Fact]
    public void Build_EmptyPackage_ExposesEmptyBufferAndIndex()
    {
        var package = new BufferPackageBuilder().Build();

        Assert.Equal(0, package.Count);
        Assert.Empty(package.Keys);
        Assert.Equal(0, package.Buffer.Length);
        Assert.Empty(package.Buffer.Memory.ToArray());
        Assert.Empty(package.Buffer.AsReadOnlySequence().ToArray());
        Assert.False(package.TryGetBytes("missing", out var bytes));
        Assert.Equal(default(ReadOnlyMemory<byte>), bytes);
    }

    [Fact]
    public void Add_SpanAndCallbackEntries_ShareOneFrozenBackingBuffer()
    {
        var builder = new BufferPackageBuilder();
        byte[] source = [0x00, 0xff, 0x80];
        IBufferWriter<byte> retainedWriter = null!;
        Memory<byte> retainedMemory = default;
        var calls = 0;
        builder.Add("header", source.AsSpan());
        Array.Fill(source, (byte)0x41);
        builder.Add("empty", ReadOnlySpan<byte>.Empty);
        builder.Add("body", writer =>
        {
            calls++;
            retainedWriter = writer;
            retainedMemory = writer.GetMemory(8);
            new byte[] { 0x11, 0xea, 0x7f, 0x99 }.CopyTo(retainedMemory.Span);
            writer.Advance(3);
        });
        // Callback memory is isolated even before the package is built.
        retainedMemory.Span.Fill(0x42);
        retainedWriter.Write(new byte[] { 0x51, 0x52 });
        builder.Add("tail", new byte[] { 0x22, 0x23 });

        var package = builder.Build();
        retainedMemory.Span.Fill(0x43);
        retainedWriter.Write(new byte[] { 0x61 });

        Assert.Equal(1, calls);
        Assert.Equal(4, package.Count);
        Assert.Equal(new[] { "body", "empty", "header", "tail" }, SortedKeys(package));
        AssertBuffer(package, [0x00, 0xff, 0x80, 0x11, 0xea, 0x7f, 0x22, 0x23]);
        AssertEntry(package, "header", 0, [0x00, 0xff, 0x80]);
        AssertEntry(package, "empty", 3, []);
        AssertEntry(package, "body", 3, [0x11, 0xea, 0x7f]);
        AssertEntry(package, "tail", 6, [0x22, 0x23]);
    }

    [Fact]
    public void Keys_UseOrdinalComparison_AndAllowEmptyKey()
    {
        var builder = new BufferPackageBuilder();
        builder.Add("key", new byte[] { 0x00, 0xff });
        builder.Add("Key", writer => writer.Write(new byte[] { 0x80, 0x31, 0xea }));
        builder.Add("", new byte[] { 0x7f });
        // Culture-sensitive comparers can equate embedded-NUL or canonically
        // equivalent Unicode strings. Raw package keys are strictly ordinal.
        builder.Add("ke\0y", new byte[] { 0x21 });
        builder.Add("\u00e9", new byte[] { 0x22 });
        builder.Add("e\u0301", new byte[] { 0x23 });

        var package = builder.Build();

        Assert.Equal(6, package.Count);
        Assert.Equal(new[] { "", "Key", "e\u0301", "ke\0y", "key", "\u00e9" }, SortedKeys(package));
        AssertBuffer(package, [0x00, 0xff, 0x80, 0x31, 0xea, 0x7f, 0x21, 0x22, 0x23]);
        AssertEntry(package, "key", 0, [0x00, 0xff]);
        AssertEntry(package, "Key", 2, [0x80, 0x31, 0xea]);
        AssertEntry(package, "", 5, [0x7f]);
        AssertEntry(package, "ke\0y", 6, [0x21]);
        AssertEntry(package, "\u00e9", 7, [0x22]);
        AssertEntry(package, "e\u0301", 8, [0x23]);
        Assert.False(package.TryGetBytes("KEY", out var missing));
        Assert.Equal(default(ReadOnlyMemory<byte>), missing);
    }

    [Fact]
    public void TryGetBytes_MissingAndPresentEmpty_HaveDistinctResults()
    {
        var builder = new BufferPackageBuilder();
        builder.Add("prefix", new byte[] { 0x31, 0xff });
        builder.Add("empty", writer => writer.GetMemory(4).Span.Fill(0x80));
        var package = builder.Build();

        Assert.False(package.TryGetBytes("missing", out var missing));
        Assert.Equal(default(ReadOnlyMemory<byte>), missing);
        Assert.True(package.TryGetBytes("empty", out var present));
        Assert.True(present.IsEmpty);
        AssertEntry(package, "empty", 2, []);
        AssertEntry(package, "prefix", 0, [0x31, 0xff]);
        Assert.Equal(2, package.Count);
        AssertBuffer(package, [0x31, 0xff]);
    }

    [Fact]
    public void TryGetBytes_NullKey_RejectsWithoutChangingPackage()
    {
        var builder = new BufferPackageBuilder();
        builder.Add("entry", new byte[] { 0xff, 0x80 });
        var package = builder.Build();

        var exception = Assert.Throws<ArgumentNullException>(() => package.TryGetBytes(null!, out _));

        Assert.Equal("key", exception.ParamName);
        Assert.Equal(1, package.Count);
        Assert.Equal(new[] { "entry" }, SortedKeys(package));
        AssertEntry(package, "entry", 0, [0xff, 0x80]);
        AssertBuffer(package, [0xff, 0x80]);
    }

    [Theory]
    [InlineData("span")]
    [InlineData("callback")]
    public void Add_NullKey_RejectsWithoutMutation(string overload)
    {
        var builder = new BufferPackageBuilder();
        builder.Add("earlier", new byte[] { 0x00, 0xff });
        var calls = 0;

        var exception = Assert.Throws<ArgumentNullException>(() =>
        {
            if (overload == "span")
            {
                builder.Add(null!, new byte[] { 0x99 });
            }
            else
            {
                builder.Add(null!, writer =>
                {
                    calls++;
                    writer.Write(new byte[] { 0x99 });
                });
            }
        });
        builder.Add("later", new byte[] { 0x80, 0x7f });
        var package = builder.Build();

        Assert.Equal("key", exception.ParamName);
        Assert.Equal(0, calls);
        Assert.Equal(2, package.Count);
        Assert.Equal(new[] { "earlier", "later" }, SortedKeys(package));
        AssertBuffer(package, [0x00, 0xff, 0x80, 0x7f]);
        AssertEntry(package, "earlier", 0, [0x00, 0xff]);
        AssertEntry(package, "later", 2, [0x80, 0x7f]);
    }

    [Theory]
    [InlineData("span")]
    [InlineData("callback")]
    public void Add_DuplicateKey_RejectsWithoutMutationOrCallback(string overload)
    {
        var builder = new BufferPackageBuilder();
        builder.Add("key", new byte[] { 0x00, 0xff, 0x80 });
        var calls = 0;

        var exception = Assert.Throws<ArgumentException>(() =>
        {
            if (overload == "span")
            {
                builder.Add("key", new byte[] { 0x99, 0x98 });
            }
            else
            {
                builder.Add("key", writer =>
                {
                    calls++;
                    writer.Write(new byte[] { 0x99, 0x98 });
                });
            }
        });
        builder.Add("Key", new byte[] { 0xea, 0x7f });
        var package = builder.Build();

        Assert.Equal("key", exception.ParamName);
        Assert.Equal(0, calls);
        Assert.Equal(2, package.Count);
        Assert.Equal(new[] { "Key", "key" }, SortedKeys(package));
        AssertBuffer(package, [0x00, 0xff, 0x80, 0xea, 0x7f]);
        AssertEntry(package, "key", 0, [0x00, 0xff, 0x80]);
        AssertEntry(package, "Key", 3, [0xea, 0x7f]);
    }

    [Fact]
    public void Add_NullWriter_DoesNotReserveKey()
    {
        var builder = new BufferPackageBuilder();
        builder.Add("earlier", new byte[] { 0x00, 0xff });

        var exception = Assert.Throws<ArgumentNullException>(() => builder.Add("retry", (Action<IBufferWriter<byte>>)null!));
        var calls = 0;
        builder.Add("retry", writer =>
        {
            calls++;
            writer.Write(new byte[] { 0x80, 0xea });
        });
        var package = builder.Build();

        Assert.Equal("write", exception.ParamName);
        Assert.Equal(1, calls);
        Assert.Equal(2, package.Count);
        Assert.Equal(new[] { "earlier", "retry" }, SortedKeys(package));
        AssertBuffer(package, [0x00, 0xff, 0x80, 0xea]);
        AssertEntry(package, "earlier", 0, [0x00, 0xff]);
        AssertEntry(package, "retry", 2, [0x80, 0xea]);
    }

    [Fact]
    public void Add_FailedWriter_RollsBackBytesIndexAndKey_ThenAllowsRetry()
    {
        var builder = new BufferPackageBuilder();
        builder.Add("earlier", new byte[] { 0x00, 0xff, 0x80 });
        var sentinel = new InvalidOperationException("write sentinel");
        var failedCalls = 0;
        IBufferWriter<byte> failedWriter = null!;

        var exception = Assert.Throws<InvalidOperationException>(() => builder.Add("retry", writer =>
        {
            failedCalls++;
            failedWriter = writer;
            writer.Write(new byte[] { 0x99, 0x98, 0x97, 0x96 });
            throw sentinel;
        }));
        var retryCalls = 0;
        builder.Add("retry", writer =>
        {
            retryCalls++;
            writer.Write(new byte[] { 0xea, 0x7f });
        });
        builder.Add("later", new byte[] { 0x31, 0x32, 0x33 });
        var package = builder.Build();
        failedWriter.Write(new byte[] { 0x95, 0x94 });

        Assert.Same(sentinel, exception);
        Assert.Equal(1, failedCalls);
        Assert.Equal(1, retryCalls);
        Assert.Equal(3, package.Count);
        Assert.Equal(new[] { "earlier", "later", "retry" }, SortedKeys(package));
        AssertBuffer(package, [0x00, 0xff, 0x80, 0xea, 0x7f, 0x31, 0x32, 0x33]);
        AssertEntry(package, "earlier", 0, [0x00, 0xff, 0x80]);
        AssertEntry(package, "retry", 3, [0xea, 0x7f]);
        AssertEntry(package, "later", 5, [0x31, 0x32, 0x33]);
    }

    [Theory]
    [InlineData("span", "caught")]
    [InlineData("callback", "caught")]
    [InlineData("build", "caught")]
    [InlineData("span", "escaped")]
    [InlineData("callback", "escaped")]
    [InlineData("build", "escaped")]
    public void Add_ReentrantMutation_RejectsAndClearsGuard(string operation, string callbackOutcome)
    {
        var builder = new BufferPackageBuilder();
        builder.Add("earlier", new byte[] { 0x00, 0xff });
        var outerCalls = 0;
        var nestedCalls = 0;
        InvalidOperationException? nestedFailure = null;
        Action reenter = () =>
        {
            switch (operation)
            {
                case "span":
                    builder.Add("nested", new byte[] { 0x99 });
                    break;
                case "callback":
                    builder.Add("nested", writer =>
                    {
                        nestedCalls++;
                        writer.Write(new byte[] { 0x99 });
                    });
                    break;
                case "build":
                    builder.Build();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(operation));
            }
        };
        Action<IBufferWriter<byte>> outer = writer =>
        {
            outerCalls++;
            writer.Write(new byte[] { 0x80, 0xea });
            if (callbackOutcome == "caught")
            {
                nestedFailure = Assert.Throws<InvalidOperationException>(reenter);
                writer.Write(new byte[] { 0x7f });
            }
            else
            {
                reenter();
            }
        };

        if (callbackOutcome == "caught")
        {
            builder.Add("outer", outer);
        }
        else
        {
            nestedFailure = Assert.Throws<InvalidOperationException>(() => builder.Add("outer", outer));
            builder.Add("outer", new byte[] { 0x31, 0x32 });
        }

        builder.Add("nested", new byte[] { 0x22 });
        builder.Add("later", writer => writer.Write(new byte[] { 0x23, 0x24 }));
        var package = builder.Build();

        Assert.Equal("The builder cannot be used from a write callback.", nestedFailure!.Message);
        Assert.Equal(1, outerCalls);
        Assert.Equal(0, nestedCalls);
        Assert.Equal(4, package.Count);
        Assert.Equal(new[] { "earlier", "later", "nested", "outer" }, SortedKeys(package));
        AssertEntry(package, "earlier", 0, [0x00, 0xff]);
        if (callbackOutcome == "caught")
        {
            AssertBuffer(package, [0x00, 0xff, 0x80, 0xea, 0x7f, 0x22, 0x23, 0x24]);
            AssertEntry(package, "outer", 2, [0x80, 0xea, 0x7f]);
            AssertEntry(package, "nested", 5, [0x22]);
            AssertEntry(package, "later", 6, [0x23, 0x24]);
        }
        else
        {
            AssertBuffer(package, [0x00, 0xff, 0x31, 0x32, 0x22, 0x23, 0x24]);
            AssertEntry(package, "outer", 2, [0x31, 0x32]);
            AssertEntry(package, "nested", 4, [0x22]);
            AssertEntry(package, "later", 5, [0x23, 0x24]);
        }
    }

    [Fact]
    public void Build_FreezesBothAddOverloadsAndSecondBuild()
    {
        var builder = new BufferPackageBuilder();
        builder.Add("first", new byte[] { 0x00, 0xff });
        builder.Add("second", writer => writer.Write(new byte[] { 0x80, 0xea }));
        var package = builder.Build();
        var calls = 0;

        Assert.Throws<InvalidOperationException>(() => builder.Build());
        Assert.Throws<InvalidOperationException>(() => builder.Add("later", new byte[] { 0x99 }));
        Assert.Throws<InvalidOperationException>(() => builder.Add("callback", writer =>
        {
            calls++;
            writer.Write(new byte[] { 0x98 });
        }));

        Assert.Equal(0, calls);
        Assert.Equal(2, package.Count);
        Assert.Equal(new[] { "first", "second" }, SortedKeys(package));
        AssertBuffer(package, [0x00, 0xff, 0x80, 0xea]);
        AssertEntry(package, "first", 0, [0x00, 0xff]);
        AssertEntry(package, "second", 2, [0x80, 0xea]);
    }

    [Fact]
    public void Build_EntryBoundsAndReadOnlyKeys_RemainStableAfterRejectedMutation()
    {
        byte[] source = [0x00, 0xff, 0x80, 0xea];
        var builder = new BufferPackageBuilder();
        builder.Add("prefix", source.AsSpan(0, 1));
        builder.Add("Key", source.AsSpan(1, 2));
        builder.Add("tail", source.AsSpan(3));
        builder.Add("end", ReadOnlySpan<byte>.Empty);
        var package = builder.Build();
        var keys = Assert.IsAssignableFrom<ICollection<string>>(package.Keys);
        Array.Fill(source, (byte)0x42);

        Assert.True(keys.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => keys.Add("new"));
        Assert.Throws<NotSupportedException>(() => keys.Remove("Key"));
        Assert.Throws<NotSupportedException>(keys.Clear);
        Assert.Throws<InvalidOperationException>(() => builder.Add("Key", new byte[] { 0x99 }));
        Assert.Throws<InvalidOperationException>(() => builder.Build());

        Assert.Equal(4, package.Count);
        Assert.Equal(new[] { "Key", "end", "prefix", "tail" }, SortedKeys(package));
        AssertEntry(package, "prefix", 0, [0x00]);
        AssertEntry(package, "Key", 1, [0xff, 0x80]);
        AssertEntry(package, "tail", 3, [0xea]);
        AssertEntry(package, "end", 4, []);
        Assert.False(package.TryGetBytes("key", out var wrongCase));
        Assert.Equal(default(ReadOnlyMemory<byte>), wrongCase);
        Assert.False(package.TryGetBytes("new", out var missing));
        Assert.Equal(default(ReadOnlyMemory<byte>), missing);
        AssertBuffer(package, [0x00, 0xff, 0x80, 0xea]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Serializer_RoundTripsRawPackage_WithoutApplicationCodecs(bool empty)
    {
        var package = ExamplePackage(empty);
        using var sendingServices = new ServiceCollection().AddSerializer().BuildServiceProvider();
        using var receivingServices = new ServiceCollection().AddSerializer().BuildServiceProvider();

        var wireBytes = sendingServices.GetRequiredService<Serializer<BufferPackage>>().SerializeToArray(package);
        var decoded = receivingServices.GetRequiredService<Serializer<BufferPackage>>().Deserialize(wireBytes)!;

        AssertExamplePackage(decoded, empty);
        AssertExamplePackage(package, empty);
        Assert.NotSame(package, decoded);
        Assert.NotSame(package.Buffer, decoded.Buffer);
    }

    [Fact]
    public void DeepCopy_SharesImmutablePackageBufferAndStableIndex()
    {
        var package = ExamplePackage(false);
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();

        var copy = services.GetRequiredService<DeepCopier>().Copy(package);
        GC.Collect();

        Assert.Same(package, copy);
        Assert.Same(package.Buffer, copy!.Buffer);
        AssertExamplePackage(copy, false);
        Assert.Single(typeof(BufferPackage).GetCustomAttributes(typeof(ImmutableAttribute), false));
        Assert.Single(typeof(BufferPackage).GetCustomAttributes(typeof(GenerateSerializerAttribute), false));
    }

    [Fact]
    public void Serializer_RepeatedPackageAndBufferReferences_PreserveGraphSharing()
    {
        var package = ExamplePackage(false);
        using var sendingServices = new ServiceCollection().AddSerializer().BuildServiceProvider();
        using var receivingServices = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var wireBytes = sendingServices.GetRequiredService<Serializer<object[]>>()
            .SerializeToArray([package, package, package.Buffer]);

        var decoded = receivingServices.GetRequiredService<Serializer<object[]>>().Deserialize(wireBytes)!;

        Assert.Equal(3, decoded.Length);
        var decodedPackage = Assert.IsType<BufferPackage>(decoded[0]);
        Assert.Same(decodedPackage, decoded[1]);
        Assert.Same(decodedPackage.Buffer, Assert.IsType<ImmutableBuffer>(decoded[2]));
        Assert.NotSame(package, decodedPackage);
        Assert.NotSame(package.Buffer, decodedPackage.Buffer);
        AssertExamplePackage(decodedPackage, false);
    }

    private static string[] SortedKeys(BufferPackage package) =>
        package.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray();

    private static void AssertBuffer(BufferPackage package, byte[] expected)
    {
        Assert.Equal(expected.Length, package.Buffer.Length);
        Assert.Equal(expected, package.Buffer.Memory.ToArray());
        Assert.Equal(expected, package.Buffer.AsReadOnlySequence().ToArray());
    }

    private static void AssertEntry(BufferPackage package, string key, int offset, byte[] expected)
    {
        Assert.True(package.TryGetBytes(key, out var bytes), $"Missing entry '{key}'.");
        Assert.Equal(expected.Length, bytes.Length);
        Assert.Equal(expected, bytes.ToArray());
        Assert.True(MemoryMarshal.TryGetArray(package.Buffer.Memory, out var backing));
        Assert.True(MemoryMarshal.TryGetArray(bytes, out var entry));
        Assert.Same(backing.Array, entry.Array);
        Assert.Equal(backing.Offset + offset, entry.Offset);
        Assert.Equal(expected.Length, entry.Count);
    }

    private static BufferPackage ExamplePackage(bool empty)
    {
        var builder = new BufferPackageBuilder();
        if (!empty)
        {
            byte[] source = [0x00, 0xff, 0x80];
            builder.Add("key", source.AsSpan());
            builder.Add("", ReadOnlySpan<byte>.Empty);
            Memory<byte> retained = default;
            builder.Add("Key", writer =>
            {
                retained = writer.GetMemory(4);
                new byte[] { 0xea, 0x31, 0x7f, 0x99 }.CopyTo(retained.Span);
                writer.Advance(3);
            });
            Array.Fill(source, (byte)0x42);
            retained.Span.Fill(0x43);
        }

        return builder.Build();
    }

    private static void AssertExamplePackage(BufferPackage package, bool empty)
    {
        if (empty)
        {
            Assert.Equal(0, package.Count);
            Assert.Empty(package.Keys);
            AssertBuffer(package, []);
        }
        else
        {
            Assert.Equal(3, package.Count);
            Assert.Equal(new[] { "", "Key", "key" }, SortedKeys(package));
            AssertBuffer(package, [0x00, 0xff, 0x80, 0xea, 0x31, 0x7f]);
            AssertEntry(package, "key", 0, [0x00, 0xff, 0x80]);
            AssertEntry(package, "", 3, []);
            AssertEntry(package, "Key", 3, [0xea, 0x31, 0x7f]);
            Assert.False(package.TryGetBytes("KEY", out var wrongCase));
            Assert.Equal(default(ReadOnlyMemory<byte>), wrongCase);
        }

        Assert.False(package.TryGetBytes("missing", out var missing));
        Assert.Equal(default(ReadOnlyMemory<byte>), missing);
    }
}
