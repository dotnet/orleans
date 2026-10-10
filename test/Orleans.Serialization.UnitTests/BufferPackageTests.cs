using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Session;
using Orleans.Serialization.WireProtocol;

namespace Orleans.Serialization.UnitTests;

[Trait("Category", "BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Serialization")]
[OwnershipCodecTests(typeof(BufferPackage))]
public sealed class BufferPackageTests
{
    [Fact]
    public void Build_EmptyPackage_ExposesEmptyBufferAndIndex()
    {
        using var builder = new BufferPackageBuilder();
        using var package = builder.Build();

        Assert.Equal(0, package.Count);
        Assert.Empty(package.Keys);
        Assert.Equal(0, package.Buffer.Length);
        Assert.Null(package.Buffer.First);
        using var retained = package.Retain();
        Assert.Null(retained.Buffer.First);
        Assert.Same(package.Entries, retained.Entries);
        Assert.Empty(package.Buffer.ToArray());
        Assert.Empty(package.Buffer.AsReadOnlySequence().ToArray());
        Assert.False(package.TryGetBytes("missing", out var bytes));
        Assert.Equal(default(ReadOnlySequence<byte>), bytes);
    }

    [Fact]
    public void Build_OnlyEmptyEntries_HasOwnerFreeBufferAndFrozenSharedIndex()
    {
        using var builder = new BufferPackageBuilder();
        builder.Add("span", ReadOnlySpan<byte>.Empty);
        builder.Add("callback", _ => { });
        using var package = builder.Build();
        using var retained = package.Retain();
        Assert.Null(package.Buffer.First);
        Assert.Null(retained.Buffer.First);
        Assert.Equal(2, package.Count);
        Assert.IsType<Dictionary<string, (int Offset, int Length)>>(package.Entries);
        Assert.Same(package.Entries, retained.Entries);
        Assert.True(Assert.IsAssignableFrom<ICollection<string>>(package.Keys).IsReadOnly);
        builder.Dispose();
        package.Release();
        Assert.True(retained.TryGetBytes("span", out var span));
        Assert.True(span.IsEmpty);
        Assert.True(retained.TryGetBytes("callback", out var callback));
        Assert.True(callback.IsEmpty);
    }

    [Fact]
    public void Add_SpanAndCallbackEntries_ShareOneFrozenBackingBuffer()
    {
        using var builder = new BufferPackageBuilder();
        byte[] source = [0x00, 0xff, 0x80];
        IBufferWriter<byte> retainedWriter = null!;
        Memory<byte> retainedMemory = default;
        ArcBuffer callbackPin = default;
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
            callbackPin = ((ArcBufferWriter)writer).PeekSlice(3);
        });
        using var retainedCallbackBytes = callbackPin;
        // An independently pinned callback page cannot mutate the copied entry.
        // Callback memory is isolated even before the package is built.
        retainedMemory.Span.Fill(0x42);
        Assert.Throws<ObjectDisposedException>(() => retainedWriter.Write(new byte[] { 0x51, 0x52 }));
        builder.Add("tail", new byte[] { 0x22, 0x23 });

        using var package = builder.Build();
        retainedMemory.Span.Fill(0x43);
        Assert.Throws<ObjectDisposedException>(() => retainedWriter.Write(new byte[] { 0x61 }));

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
        using var builder = new BufferPackageBuilder();
        builder.Add("key", new byte[] { 0x00, 0xff });
        builder.Add("Key", writer => writer.Write(new byte[] { 0x80, 0x31, 0xea }));
        builder.Add("", new byte[] { 0x7f });
        // Culture-sensitive comparers can equate embedded-NUL or canonically
        // equivalent Unicode strings. Raw package keys are strictly ordinal.
        builder.Add("ke\0y", new byte[] { 0x21 });
        builder.Add("\u00e9", new byte[] { 0x22 });
        builder.Add("e\u0301", new byte[] { 0x23 });

        using var package = builder.Build();

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
        Assert.Equal(default(ReadOnlySequence<byte>), missing);
    }

    [Fact]
    public void TryGetBytes_MissingAndPresentEmpty_HaveDistinctResults()
    {
        using var builder = new BufferPackageBuilder();
        builder.Add("prefix", new byte[] { 0x31, 0xff });
        builder.Add("empty", writer => writer.GetMemory(4).Span.Fill(0x80));
        using var package = builder.Build();

        Assert.False(package.TryGetBytes("missing", out var missing));
        Assert.Equal(default(ReadOnlySequence<byte>), missing);
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
        using var builder = new BufferPackageBuilder();
        builder.Add("entry", new byte[] { 0xff, 0x80 });
        using var package = builder.Build();

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
        using var builder = new BufferPackageBuilder();
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
        using var package = builder.Build();

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
        using var builder = new BufferPackageBuilder();
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
        using var package = builder.Build();

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
        using var builder = new BufferPackageBuilder();
        builder.Add("earlier", new byte[] { 0x00, 0xff });

        var exception = Assert.Throws<ArgumentNullException>(() => builder.Add("retry", (Action<IBufferWriter<byte>>)null!));
        var calls = 0;
        builder.Add("retry", writer =>
        {
            calls++;
            writer.Write(new byte[] { 0x80, 0xea });
        });
        using var package = builder.Build();

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
        using var builder = new BufferPackageBuilder();
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
        using var package = builder.Build();
        Assert.Throws<ObjectDisposedException>(() => failedWriter.Write(new byte[] { 0x95, 0x94 }));

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
        using var builder = new BufferPackageBuilder();
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
        using var package = builder.Build();

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
        using var builder = new BufferPackageBuilder();
        builder.Add("first", new byte[] { 0x00, 0xff });
        builder.Add("second", writer => writer.Write(new byte[] { 0x80, 0xea }));
        using var package = builder.Build();
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
        using var builder = new BufferPackageBuilder();
        builder.Add("prefix", source.AsSpan(0, 1));
        builder.Add("Key", source.AsSpan(1, 2));
        builder.Add("tail", source.AsSpan(3));
        builder.Add("end", ReadOnlySpan<byte>.Empty);
        using var package = builder.Build();
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
        Assert.Equal(default(ReadOnlySequence<byte>), wrongCase);
        Assert.False(package.TryGetBytes("new", out var missing));
        Assert.Equal(default(ReadOnlySequence<byte>), missing);
        AssertBuffer(package, [0x00, 0xff, 0x80, 0xea]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Serializer_RoundTripsRawPackage_WithoutApplicationCodecs(bool empty)
    {
        using var package = ExamplePackage(empty);
        using var sendingServices = new ServiceCollection().AddSerializer().BuildServiceProvider();
        using var receivingServices = new ServiceCollection().AddSerializer().BuildServiceProvider();

        var wireBytes = sendingServices.GetRequiredService<Serializer<BufferPackage>>().SerializeToArray(package);
        using var decoded = receivingServices.GetRequiredService<Serializer<BufferPackage>>().Deserialize(wireBytes)!;

        AssertExamplePackage(decoded, empty);
        AssertExamplePackage(package, empty);
        Assert.NotSame(package, decoded);
        if (!empty) Assert.NotSame(package.Buffer.First, decoded.Buffer.First);
    }

    [Fact]
    public void DeepCopy_RetainsIndependentOwnerAndPreservesGraphIdentity()
    {
        using var package = ExamplePackage(false);
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var references = package.Buffer.First.ReferenceCount;
        var graph = services.GetRequiredService<DeepCopier>().Copy(new[] { package, package });
        using var copy = graph[0];
        Assert.NotSame(package, copy);
        Assert.Same(copy, graph[1]);
        Assert.Same(package.Buffer.First, copy.Buffer.First);
        Assert.Same(package.Entries, copy.Entries);
        Assert.Equal(references + 1, package.Buffer.First.ReferenceCount);
        package.Release();
        package.Release();
        AssertExamplePackage(copy, false);
    }

    [Fact]
    public void Serializer_RepeatedPackageReferences_PreserveGraphIdentity()
    {
        using var package = ExamplePackage(false);
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = services.GetRequiredService<Serializer<BufferPackage[]>>();
        var decoded = serializer.Deserialize(serializer.SerializeToArray([package, package]))!;
        using var result = decoded[0];
        Assert.Same(result, decoded[1]);
        Assert.NotSame(package, result);
        AssertExamplePackage(result, false);
        AssertExamplePackage(package, false);
    }

    [Fact]
    public void Retain_ReleaseAndBuilderDisposal_AreIndependentAndIdempotent()
    {
        using var builder = new BufferPackageBuilder();
        builder.Add("entry", new byte[] { 0x80, 0xff });
        using var package = builder.Build();
        using var retained = package.Retain();
        var page = package.Buffer.First;
        Assert.Equal(2, page.ReferenceCount);
        builder.Dispose();
        builder.Dispose();
        package.Release();
        package.Dispose();
        Assert.Equal(1, page.ReferenceCount);
        Assert.Throws<ObjectDisposedException>(() => package.Retain());
        Assert.Throws<ObjectDisposedException>(() => package.TryGetBytes("entry", out _));
        AssertBuffer(retained, [0x80, 0xff]);
        retained.Release();
        Assert.Equal(0, page.ReferenceCount);
    }

    [Fact]
    public async Task Release_ConcurrentDuplicateCalls_UnpinExactlyOnce()
    {
        using var builder = new BufferPackageBuilder();
        builder.Add("entry", new byte[] { 0x80, 0xff });
        using var package = builder.Build();
        using var retained = package.Retain();
        var page = package.Buffer.First;
        Assert.Equal(2, page.ReferenceCount);
        const int callers = 16;
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = 0;
        var releases = Enumerable.Range(0, callers).Select(async index =>
        {
            if (Interlocked.Increment(ref waiting) == callers) ready.SetResult(true);
            await start.Task;
            if (index % 2 == 0) package.Release();
            else package.Dispose();
        }).ToArray();
        await ready.Task;
        start.SetResult(true);
        await Task.WhenAll(releases);

        Assert.Equal(1, page.ReferenceCount);
        Assert.Throws<ObjectDisposedException>(() => _ = package.Buffer);
        Assert.Throws<ObjectDisposedException>(() => _ = package.Count);
        Assert.Throws<ObjectDisposedException>(() => _ = package.Keys);
        Assert.Throws<ObjectDisposedException>(() => _ = package.Entries);
        Assert.Throws<ObjectDisposedException>(() => package.Retain());
        Assert.Throws<ObjectDisposedException>(() => package.TryGetBytes("entry", out _));
        AssertBuffer(retained, [0x80, 0xff]);
        retained.Release();
        Assert.Equal(0, page.ReferenceCount);
    }

    [Fact]
    public void Dispose_UnbuiltBuilderAndFailedCallback_ReleaseTheirPages()
    {
        var builder = new BufferPackageBuilder();
        ArcBuffer callbackPin = default;
        Assert.Throws<FormatException>(() => builder.Add("entry", output =>
        {
            var writer = Assert.IsType<ArcBufferWriter>(output);
            writer.Write(new byte[] { 0x91 });
            callbackPin = writer.PeekSlice(1);
            throw new FormatException();
        }));
        var page = callbackPin.First;
        Assert.NotNull(page);
        Assert.Equal(1, page.ReferenceCount);
        callbackPin.Dispose();
        Assert.Equal(0, page.ReferenceCount);
        builder.Dispose();
        builder.Dispose();
        Assert.Throws<ObjectDisposedException>(() => builder.Build());
        Assert.Throws<ObjectDisposedException>(() => builder.Add("later", new byte[] { 1 }));
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("bad-offset")]
    [InlineData("duplicate-key")]
    [InlineData("duplicate-buffer")]
    [InlineData("missing-index")]
    [InlineData("missing-length")]
    [InlineData("negative-length")]
    [InlineData("null-key")]
    public void MalformedPackageRead_ReleasesPartiallyReadArcPins(string failure)
    {
        using var services = ArcBufferCodecTests.Services();
        using var bytesWriter = new ArcBufferWriter();
        bytesWriter.Write(ArcBufferCodecTests.Bytes(50037));
        using var payload = bytesWriter.PeekSlice(bytesWriter.Length);
        using var wire = new ArcBufferWriter();
        using (var writeSession = services.GetRequiredService<SerializerSessionPool>().GetSession())
        {
            var writer = Writer.Create(wire, writeSession);
            ReferenceCodec.TryWriteReferenceField(ref writer, 0, typeof(BufferPackage), new object());
            writer.WriteFieldHeader(0, typeof(BufferPackage), typeof(BufferPackage), WireType.TagDelimited);
            var codec = new ArcBufferCodec();
            codec.WriteField(ref writer, 0, typeof(ArcBuffer), payload);
            if (failure == "duplicate-buffer") codec.WriteField(ref writer, 0, typeof(ArcBuffer), payload);
            if (failure != "missing-index")
            {
                UInt32Codec.WriteField(ref writer, 1, failure == "duplicate-key" ? 2u : 1u);
                WriteMalformedEntry(ref writer, 1, failure, services);
                if (failure == "duplicate-key") WriteMalformedEntry(ref writer, 0, failure, services);
            }
            writer.WriteEndObject();
            writer.Commit();
        }
        if (failure == "truncated") wire.Truncate(wire.Length - 1);
        using var input = wire.PeekSlice(wire.Length);
        var pages = input.Pages.ToArray();
        var references = pages.Select(page => page.ReferenceCount).ToArray();
        using var session = services.GetRequiredService<SerializerSessionPool>().GetSession();
        var reader = Reader.Create(input, session);
        var packageCodec = new BufferPackageCodec(services.GetRequiredService<IFieldCodec<string>>());
        BufferPackage? unexpected = null;
        Exception? error = null;
        try { unexpected = packageCodec.ReadValue(ref reader, reader.ReadFieldHeader()); }
        catch (Exception exception) { error = exception; }
        unexpected?.Release();
        Assert.NotNull(error);
        Assert.Equal(references, pages.Select(page => page.ReferenceCount).ToArray());
    }

    private static void WriteMalformedEntry<TBufferWriter>(ref Writer<TBufferWriter> writer, uint delta, string failure, IServiceProvider services)
        where TBufferWriter : IBufferWriter<byte>
    {
        ReferenceCodec.MarkValueField(writer.Session);
        writer.WriteFieldHeader(delta, null, null, WireType.TagDelimited);
        services.GetRequiredService<IFieldCodec<string>>().WriteField(ref writer, 0, typeof(string), failure == "null-key" ? null! : "key");
        Int32Codec.WriteField(ref writer, 1, failure == "bad-offset" ? int.MaxValue : 0);
        if (failure != "missing-length") Int32Codec.WriteField(ref writer, 1, failure == "negative-length" ? -1 : 3);
        writer.WriteEndObject();
    }

    [Fact]
    public void PackageSerialization_IsRepeatableAndRetainsOriginalPin()
    {
        using var package = ExamplePackage(false);
        using var services = ArcBufferCodecTests.Services();
        var serializer = services.GetRequiredService<Serializer<BufferPackage>>();
        var page = package.Buffer.First;
        var count = page.ReferenceCount;
        var first = serializer.SerializeToArray(package);
        var second = serializer.SerializeToArray(package);
        Assert.Equal(first, second);
        Assert.Equal(count, page.ReferenceCount);
        AssertExamplePackage(package, false);
    }

    [Fact]
    public void PackageArcInput_ReadSharesPagesAndReleasesOnePin()
    {
        using var services = ArcBufferCodecTests.Services();
        using var builder = new BufferPackageBuilder();
        var expected = ArcBufferCodecTests.Bytes(50037);
        builder.Add("data", expected);
        using var package = builder.Build();
        var serializer = services.GetRequiredService<Serializer<BufferPackage>>();
        using var wire = new ArcBufferWriter();
        using (var writeSession = services.GetRequiredService<SerializerSessionPool>().GetSession())
        {
            var writer = Writer.Create(wire, writeSession);
            serializer.Serialize(package, ref writer);
            writer.Commit();
        }
        using var input = wire.PeekSlice(wire.Length);
        var pages = input.Pages.ToArray();
        var references = pages.Select(page => page.ReferenceCount).ToArray();
        using var session = services.GetRequiredService<SerializerSessionPool>().GetSession();
        var reader = Reader.Create(input, session);
        using var result = serializer.Deserialize(ref reader)!;
        Assert.Contains(result.Buffer.First, pages);
        Assert.True(result.TryGetBytes("data", out var entry));
        Assert.Equal(expected, entry.ToArray());
        Assert.All(result.Buffer.Pages.ToArray(), page => Assert.Equal(references[Array.IndexOf(pages, page)] + 1, page.ReferenceCount));
        result.Release();
        Assert.Equal(references, pages.Select(page => page.ReferenceCount).ToArray());
    }

    private static string[] SortedKeys(BufferPackage package) =>
        package.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray();

    private static void AssertBuffer(BufferPackage package, byte[] expected)
    {
        Assert.Equal(expected.Length, package.Buffer.Length);
        Assert.Equal(expected, package.Buffer.ToArray());
        Assert.Equal(expected, package.Buffer.AsReadOnlySequence().ToArray());
    }

    private static void AssertEntry(BufferPackage package, string key, int offset, byte[] expected)
    {
        Assert.True(package.TryGetBytes(key, out var bytes), $"Missing entry '{key}'.");
        Assert.Equal(expected.Length, bytes.Length);
        Assert.Equal(expected, bytes.ToArray());
        if (expected.Length > 0)
        {
            using var selected = package.Buffer.Slice(offset, expected.Length);
            Assert.True(MemoryMarshal.TryGetArray(selected.AsReadOnlySequence().First, out var backing));
            Assert.True(MemoryMarshal.TryGetArray(bytes.First, out var entry));
            Assert.Same(backing.Array, entry.Array);
            Assert.Equal(backing.Offset, entry.Offset);
            Assert.Equal(expected.Length, entry.Count);
        }
    }

    private static BufferPackage ExamplePackage(bool empty)
    {
        using var builder = new BufferPackageBuilder();
        if (!empty)
        {
            byte[] source = [0x00, 0xff, 0x80];
            builder.Add("key", source.AsSpan());
            builder.Add("", ReadOnlySpan<byte>.Empty);
            Memory<byte> retained = default;
            ArcBuffer callbackPin = default;
            builder.Add("Key", writer =>
            {
                retained = writer.GetMemory(4);
                new byte[] { 0xea, 0x31, 0x7f, 0x99 }.CopyTo(retained.Span);
                writer.Advance(3);
                callbackPin = ((ArcBufferWriter)writer).PeekSlice(3);
            });
            using var retainedCallbackBytes = callbackPin;
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
            Assert.Equal(default(ReadOnlySequence<byte>), wrongCase);
        }

        Assert.False(package.TryGetBytes("missing", out var missing));
        Assert.Equal(default(ReadOnlySequence<byte>), missing);
    }
}
