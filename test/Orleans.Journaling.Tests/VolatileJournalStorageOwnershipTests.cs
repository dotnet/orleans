using System.Buffers;
using System.Runtime.CompilerServices;
using Orleans.Serialization.Buffers;
using Xunit;

namespace Orleans.Journaling.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
public sealed class VolatileJournalStorageOwnershipTests(ITestOutputHelper output)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BorrowedWrite_CopiesBeforeCompletionAndSourcePoisoning(bool replace)
    {
        var storage = new VolatileJournalStorage("test-format");
        byte[] first = [1, 2, 3];
        byte[] second = [4, 5];
        var sequence = new SequenceSegment(first);
        var end = sequence.Append(second);
        var input = new ReadOnlySequence<byte>(sequence, 0, end, second.Length);
        if (replace) await storage.ReplaceAsync(input, Token);
        else await storage.AppendAsync(input, Token);
        first.AsSpan().Fill(0x67);
        second.AsSpan().Fill(0x67);

        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, await Read(storage));
        Assert.Equal(5, storage.MemoryStatistics.CopiedBytes);
        Assert.Equal(0, storage.MemoryStatistics.SharedBytes);
        Assert.Equal(1, storage.MemoryStatistics.RetainedPages);
        await storage.DeleteAsync(Token);
        Assert.Equal(0, storage.MemoryStatistics.RetainedCapacity);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetainedWrite_HasIndependentPinAndReleasesExactlyOnce(bool replace)
    {
        var storage = new VolatileJournalStorage();
        using var writer = new ArcBufferWriter();
        writer.Write(new byte[] { 10, 20, 30 });
        using var source = writer.PeekSlice(writer.Length);
        var page = source.First;
        var baseline = page.ReferenceCount;
        var capability = (IRetainedJournalStorage)storage;
        if (replace) await capability.ReplaceRetainedAsync(source, Token);
        else await capability.AppendRetainedAsync(source, Token);
        Assert.Equal(baseline + 1, page.ReferenceCount);
        Assert.Equal(0, storage.MemoryStatistics.CopiedBytes);
        Assert.Equal(3, storage.MemoryStatistics.SharedBytes);
        Assert.Equal(new byte[] { 10, 20, 30 }, await Read(storage));
        Assert.Equal(baseline + 1, page.ReferenceCount);

        await storage.ReplaceAsync(ReadOnlySequence<byte>.Empty, Token);
        Assert.Equal(baseline, page.ReferenceCount);
        await storage.DeleteAsync(Token);
        await storage.DeleteAsync(Token);
        Assert.Equal(baseline, page.ReferenceCount);
        Assert.Equal(0, storage.MemoryStatistics.RetainedCapacity);
    }

    [Fact]
    public async Task RetainedWrite_SurvivesSourceWriterResetAndDisposal()
    {
        var storage = new VolatileJournalStorage();
        var writer = new ArcBufferWriter();
        writer.Write(new byte[] { 7, 8, 9 });
        var source = writer.PeekSlice(writer.Length);
        await ((IRetainedJournalStorage)storage).AppendRetainedAsync(source, Token);
        source.Dispose();
        writer.Reset();
        writer.Write(new byte[] { 90, 91, 92 });
        writer.Dispose();
        Assert.Equal(new byte[] { 7, 8, 9 }, await Read(storage));
        await storage.DeleteAsync(Token);
        Assert.Equal(0, storage.MemoryStatistics.RetainedPages);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetainedWrite_MultiPageOwnersSurviveSourceDisposalAndRetirement(bool replace)
    {
        var storage = new VolatileJournalStorage();
        using var writer = new ArcBufferWriter();
        var bytes = Enumerable.Range(0, 3 * ArcBufferWriter.MinimumPageSize + 17)
            .Select(static i => (byte)(i % 251)).ToArray();
        writer.Write(bytes);
        var source = writer.PeekSlice(writer.Length);
        using var observation = source.Slice(0);
        var pages = source.Pages.ToArray();
        Assert.Equal(4, pages.Length);
        using (source)
        {
            var retained = (IRetainedJournalStorage)storage;
            if (replace)
            {
                await retained.ReplaceRetainedAsync(source, Token);
            }
            else
            {
                await retained.AppendRetainedAsync(source, Token);
            }

            Assert.All(pages, static page => Assert.Equal(4, page.ReferenceCount));
        }

        writer.Reset();
        writer.Write(new byte[] { 99 });
        writer.Dispose();
        Assert.All(pages, static page => Assert.Equal(2, page.ReferenceCount));
        Assert.Equal(bytes, await Read(storage));
        Assert.All(pages, static page => Assert.Equal(2, page.ReferenceCount));
        Assert.Equal(4, storage.MemoryStatistics.RetainedPages);

        await storage.ReplaceAsync(ReadOnlySequence<byte>.Empty, Token);
        Assert.All(pages, static page => Assert.Equal(1, page.ReferenceCount));
        Assert.Equal(bytes, observation.ToArray());
        await storage.DeleteAsync(Token);
        Assert.All(pages, static page => Assert.Equal(1, page.ReferenceCount));
        Assert.Equal(0, storage.MemoryStatistics.RetainedPages);
    }

    [Fact]
    public async Task ConcurrentReaders_PinStableBytesAndMetadataAcrossSharedHandleReplaceDeleteRecreate()
    {
        var provider = new VolatileJournalStorageProvider();
        var first = Assert.IsType<VolatileJournalStorage>(provider.CreateStorage(new("shared")));
        var second = Assert.IsType<VolatileJournalStorage>(provider.CreateStorage(new("shared")));
        await first.CreateIfNotExistsAsync(new Dictionary<string, string> { ["owner"] = "old" }, Token);
        using var writer = new ArcBufferWriter();
        writer.Write(new byte[] { 1, 2, 3 });
        using var source = writer.PeekSlice(writer.Length);
        await ((IRetainedJournalStorage)first).AppendRetainedAsync(source, Token);
        await second.AppendAsync(new ReadOnlySequence<byte>(new byte[] { 4, 5 }), Token);
        var originalMetadata = await first.GetMetadataAsync(Token);
        var baseline = source.First.ReferenceCount;
        using var entered = new CountdownEvent(2);
        using var release = new ManualResetEventSlim();
        var readers = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
        {
            var consumer = new CapturingConsumer(() =>
            {
                entered.Signal();
                Assert.True(release.Wait(TimeSpan.FromSeconds(30), Token), "Reader release barrier was not signaled.");
            });
            await first.ReadAsync(consumer, Token);
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, consumer.Bytes.ToArray());
            Assert.Equal(originalMetadata!.ETag, consumer.Metadata!.ETag);
            Assert.Equal("old", consumer.Metadata.Properties["owner"]);
            Assert.Equal(1, consumer.CompletionCount);
        }, Token)).ToArray();
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(30), Token), "Both stable readers must enter before replacing storage.");
            Assert.Equal(baseline + 2, source.First.ReferenceCount);
            Assert.Equal(4, second.MemoryStatistics.ReaderReferences);
            await second.ReplaceAsync(new ReadOnlySequence<byte>(new byte[] { 8, 9 }), Token);
            Assert.Equal(baseline + 1, source.First.ReferenceCount); // writer + source + two readers, no store pin
            await second.DeleteAsync(Token);
            Assert.True(await first.CreateIfNotExistsAsync(new Dictionary<string, string> { ["owner"] = "new" }, Token));
            await second.AppendAsync(new ReadOnlySequence<byte>(new byte[] { 10, 11 }), Token);
            Assert.Equal(new byte[] { 10, 11 }, await Read(second));
            Assert.Equal("new", (await first.GetMetadataAsync(Token))!.Properties["owner"]);
        }
        finally
        {
            release.Set();
            await Task.WhenAll(readers);
        }

        Assert.Equal(baseline - 1, source.First.ReferenceCount);
        Assert.Equal(0, first.MemoryStatistics.ReaderReferences);
        await second.DeleteAsync(Token);
        Assert.Equal(0, first.MemoryStatistics.RetainedCapacity);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReaderFailureOrCancellation_ReleasesSnapshotWithoutRetiringStoredPin(bool cancel)
    {
        var storage = new VolatileJournalStorage();
        using var writer = new ArcBufferWriter();
        writer.Write(new byte[] { 1, 2, 3 });
        using var source = writer.PeekSlice(writer.Length);
        await ((IRetainedJournalStorage)storage).AppendRetainedAsync(source, Token);
        await storage.AppendAsync(new ReadOnlySequence<byte>(new byte[] { 4 }), Token);
        var baseline = source.First.ReferenceCount;
        using var cancellation = new CancellationTokenSource();
        var expected = new IOException("Consumer failed after capture.");
        var consumer = new CapturingConsumer(() =>
        {
            Assert.Equal(baseline + 1, source.First.ReferenceCount);
            if (cancel) cancellation.Cancel();
            else throw expected;
        });
        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => storage.ReadAsync(consumer, cancellation.Token).AsTask());
        else
            Assert.Same(expected, await Record.ExceptionAsync(() => storage.ReadAsync(consumer, cancellation.Token).AsTask()));
        Assert.Equal(baseline, source.First.ReferenceCount);
        Assert.Equal(0, storage.MemoryStatistics.ReaderReferences);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await Read(storage));
        await storage.DeleteAsync(Token);
        Assert.Equal(baseline - 1, source.First.ReferenceCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanceledWrites_DoNotAcquirePinsChangeMetadataOrResetCompaction(bool replace)
    {
        var storage = new VolatileJournalStorage();
        await SeedCompactionRequest(storage, [42]);
        var metadata = await storage.GetMetadataAsync(Token);
        using var writer = new ArcBufferWriter();
        writer.Write(new byte[] { 10 });
        using var source = writer.PeekSlice(writer.Length);
        var baseline = source.First.ReferenceCount;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var retained = (IRetainedJournalStorage)storage;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => replace
            ? retained.ReplaceRetainedAsync(source, cancellation.Token).AsTask()
            : retained.AppendRetainedAsync(source, cancellation.Token).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => replace
            ? storage.ReplaceAsync(new ReadOnlySequence<byte>(new byte[] { 99 }), cancellation.Token).AsTask()
            : storage.AppendAsync(new ReadOnlySequence<byte>(new byte[] { 99 }), cancellation.Token).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => storage.DeleteAsync(cancellation.Token).AsTask());
        Assert.Equal(baseline, source.First.ReferenceCount);
        Assert.Equal(metadata!.ETag, (await storage.GetMetadataAsync(Token))!.ETag);
        Assert.True(storage.IsCompactionRequested);
        Assert.Equal(new byte[] { 42 }, await Read(storage));
        await storage.DeleteAsync(Token);
    }

    [Fact]
    public async Task ManyBorrowedCaptures_CoalesceOnOnePageAndReleaseAllRetainedMemory()
    {
        var storage = new VolatileJournalStorage();
        var input = new byte[] { 23 };
        for (var i = 0; i < 1000; i++) await storage.AppendAsync(new ReadOnlySequence<byte>(input), Token);
        var stats = storage.MemoryStatistics;
        Assert.Equal(1000, stats.Segments);
        Assert.Equal(1000, stats.CopiedBytes);
        Assert.Equal(0, stats.SharedBytes);
        Assert.Equal(1, stats.RetainedPages);
        Assert.Equal(ArcBufferWriter.MinimumPageSize, stats.RetainedCapacity);
        Assert.Equal(Enumerable.Repeat((byte)23, 1000).ToArray(), await Read(storage));
        await storage.ReplaceAsync(new ReadOnlySequence<byte>(new byte[] { 7, 8 }), Token);
        Assert.Equal(1, storage.MemoryStatistics.Segments);
        Assert.Equal(1, storage.MemoryStatistics.RetainedPages);
        Assert.Equal(1002, storage.MemoryStatistics.CopiedBytes);
        Assert.Equal(new byte[] { 7, 8 }, await Read(storage));
        await storage.DeleteAsync(Token);
        Assert.Equal((0, 0, 1002L, 0L, 0L, 0), storage.MemoryStatistics);
    }

    [Fact]
    public async Task EmptyBorrowedAndRetainedWrites_PreserveSegmentsMetadataAndAppendLimit()
    {
        var storage = new VolatileJournalStorage("empty-format");
        using var writer = new ArcBufferWriter();
        using var source = writer.PeekSlice(0);
        await ((IRetainedJournalStorage)storage).ReplaceRetainedAsync(source, Token);
        var metadata = await storage.GetMetadataAsync(Token);
        Assert.Equal("empty-format", metadata!.FormatKey);
        Assert.Equal("1", metadata.ETag);
        for (var i = 0; i < 9; i++) await storage.AppendAsync(ReadOnlySequence<byte>.Empty, Token);
        Assert.False(storage.IsCompactionRequested);
        await ((IRetainedJournalStorage)storage).AppendRetainedAsync(source, Token);
        Assert.True(storage.IsCompactionRequested);
        Assert.Equal(11, storage.MemoryStatistics.Segments);
        Assert.Equal(0, storage.MemoryStatistics.RetainedCapacity);
        Assert.Empty(await Read(storage));
        await storage.ReplaceAsync(ReadOnlySequence<byte>.Empty, Token);
        Assert.False(storage.IsCompactionRequested);
        Assert.Equal("12", (await storage.GetMetadataAsync(Token))!.ETag);
        Assert.Single(storage.Segments);
        await storage.DeleteAsync(Token);
    }

    [Fact]
    public void ArcPagePool_ManyReturnsHaveExactBoundAndOversizedPagesAreNotCached()
    {
        var pool = new ArcBufferPagePool();
        var pages = Enumerable.Range(0, 300).Select(_ => pool.Rent()).ToArray();
        foreach (var page in pages) pool.Return(page);
        Assert.Equal(4 * 1024 * 1024, pool.RetainedBytes);
        Assert.Equal(256, pool.RetainedPages);
        Assert.Equal(44, pages.Count(static page => page.Array.Length == 0));
        var oversized = pool.Rent(2 * 1024 * 1024);
        pool.Return(oversized);
        Assert.Empty(oversized.Array);
        Assert.Equal(4 * 1024 * 1024, pool.RetainedBytes);
        for (var i = 0; i < 256; i++) pool.Rent();
        Assert.Equal(0, pool.RetainedBytes);
        Assert.Equal(0, pool.RetainedPages);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BorrowedCopyFailure_IsAtomicAndReleasesUnpublishedPages(bool replace)
    {
        var storage = new VolatileJournalStorage();
        await SeedCompactionRequest(storage, [1, 2, 3]);
        var before = storage.MemoryStatistics;
        var metadata = await storage.GetMetadataAsync(Token);
        using var failing = new FailingMemoryManager();
        var sequence = new SequenceSegment(new byte[100_000]);
        var end = sequence.Append(failing.Memory);
        var input = new ReadOnlySequence<byte>(sequence, 0, end, end.Memory.Length);
        var expected = failing.Exception;
        Assert.Same(expected, await Record.ExceptionAsync(() => replace
            ? storage.ReplaceAsync(input, Token).AsTask()
            : storage.AppendAsync(input, Token).AsTask()));
        Assert.Equal(before, storage.MemoryStatistics);
        Assert.Equal(metadata!.ETag, (await storage.GetMetadataAsync(Token))!.ETag);
        Assert.True(storage.IsCompactionRequested);
        Assert.Equal(new byte[] { 1, 2, 3 }, await Read(storage));
        await storage.DeleteAsync(Token);
        Assert.Equal(0, storage.MemoryStatistics.RetainedPages);
    }

    [Fact]
    public async Task RepeatedPayload_SharingEliminatesCopiesAndRetainsFourPagesInsteadOfFourHundred()
    {
        var borrowed = new VolatileJournalStorage();
        var retained = new VolatileJournalStorage();
        var bytes = Enumerable.Range(0, 64 * 1024).Select(static i => (byte)(i % 251)).ToArray();
        using var writer = new ArcBufferWriter();
        writer.Write(bytes);
        using var source = writer.PeekSlice(writer.Length);
        var sequence = source.AsReadOnlySequence();
        var allocationStart = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++) await ((IRetainedJournalStorage)retained).AppendRetainedAsync(source, Token);
        var retainedAllocations = GC.GetAllocatedBytesForCurrentThread() - allocationStart;
        allocationStart = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++) await borrowed.AppendAsync(sequence, Token);
        var borrowedAllocations = GC.GetAllocatedBytesForCurrentThread() - allocationStart;
        var sharedStats = retained.MemoryStatistics;
        var copyStats = borrowed.MemoryStatistics;
        Assert.Equal(0, sharedStats.CopiedBytes);
        Assert.Equal(6_553_600, sharedStats.SharedBytes);
        Assert.Equal(4, sharedStats.RetainedPages);
        Assert.Equal(65_536, sharedStats.RetainedCapacity);
        Assert.Equal(6_553_600, copyStats.CopiedBytes);
        Assert.Equal(0, copyStats.SharedBytes);
        Assert.Equal(400, copyStats.RetainedPages);
        Assert.Equal(6_553_600, copyStats.RetainedCapacity);
        foreach (var segment in retained.Segments) Assert.Equal(bytes, segment);
        foreach (var segment in borrowed.Segments) Assert.Equal(bytes, segment);
        output.WriteLine($"OWNERSHIP_EVIDENCE retainedAllocatedBytes={retainedAllocations} borrowedAllocatedBytes={borrowedAllocations} sharedRetainedBytes={sharedStats.RetainedCapacity} copiedRetainedBytes={copyStats.RetainedCapacity} eliminatedCopiedBytes={sharedStats.SharedBytes}");
        await retained.DeleteAsync(Token);
        await borrowed.DeleteAsync(Token);
        Assert.Equal(0, retained.MemoryStatistics.RetainedCapacity);
        Assert.Equal(0, borrowed.MemoryStatistics.RetainedCapacity);
        Assert.Equal(2, source.First.ReferenceCount); // source and its writer, no leaked storage/reader references
    }

    private sealed class FailingMemoryManager : MemoryManager<byte>
    {
        public IOException Exception { get; } = new("Borrowed source failed during copying.");
        public override Memory<byte> Memory => CreateMemory(1);
        public override Span<byte> GetSpan() => throw Exception;
        public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();
        public override void Unpin() { }
        protected override void Dispose(bool disposing) { }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitialBorrowedCopyFailure_ReleasesAllocationOwnerAndLeavesStorageAbsent(bool replace)
    {
        var storage = new VolatileJournalStorage();
        using var failing = new FailingMemoryManager();
        var start = new SequenceSegment(new byte[100_000]);
        var end = start.Append(failing.Memory);
        var input = new ReadOnlySequence<byte>(start, 0, end, end.Memory.Length);

        Assert.Same(failing.Exception, await Record.ExceptionAsync(() => replace
            ? storage.ReplaceAsync(input, Token).AsTask()
            : storage.AppendAsync(input, Token).AsTask()));
        Assert.Null(await storage.GetMetadataAsync(Token));
        Assert.Equal((0, 0, 0L, 0L, 0L, 0), storage.MemoryStatistics);
        Assert.Empty(await Read(storage));
        await storage.AppendAsync(new ReadOnlySequence<byte>(new byte[] { 42 }), Token);
        Assert.Equal(new byte[] { 42 }, await Read(storage));
        Assert.Equal("1", (await storage.GetMetadataAsync(Token))!.ETag);
        await storage.DeleteAsync(Token);
        Assert.Equal((0, 0, 1L, 0L, 0L, 0), storage.MemoryStatistics);
    }

    [Fact]
    public async Task ArcPagePool_ConcurrentReturnsRespectExactReservationBound()
    {
        var pool = new ArcBufferPagePool(2 * ArcBufferWriter.MinimumPageSize);
        var pages = Enumerable.Range(0, 200).Select(_ => pool.Rent()).ToArray();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var remaining = pages.Length;
        var returns = pages.Select(page => Task.Run(async () =>
        {
            if (Interlocked.Decrement(ref remaining) == 0) ready.SetResult();
            await release.Task;
            pool.Return(page);
        }, Token)).ToArray();
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(30), Token);
        release.SetResult();
        await Task.WhenAll(returns);
        Assert.Equal(2 * ArcBufferWriter.MinimumPageSize, pool.RetainedBytes);
        Assert.Equal(2, pool.RetainedPages);
        Assert.Equal(198, pages.Count(static page => page.Array.Length == 0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetainedWrite_InvalidOwnershipTokenDoesNotPublishOrResetCounters(bool replace)
    {
        var storage = new VolatileJournalStorage();
        await SeedCompactionRequest(storage, [42]);
        var before = storage.MemoryStatistics;
        var metadata = await storage.GetMetadataAsync(Token);
        using var writer = new ArcBufferWriter();
        writer.Write(new byte[] { 10 });
        var source = writer.PeekSlice(writer.Length);
        source.Dispose();
        Assert.Equal(1, source.First.ReferenceCount); // writer still pins the page, source no longer owns a token
        var capability = (IRetainedJournalStorage)storage;
        await Assert.ThrowsAsync<InvalidOperationException>(() => replace
            ? capability.ReplaceRetainedAsync(source, Token).AsTask()
            : capability.AppendRetainedAsync(source, Token).AsTask());
        Assert.Equal(1, source.First.ReferenceCount);
        Assert.Equal(before, storage.MemoryStatistics);
        Assert.Equal(metadata!.ETag, (await storage.GetMetadataAsync(Token))!.ETag);
        Assert.True(storage.IsCompactionRequested);
        Assert.Equal(new byte[] { 42 }, await Read(storage));
        await storage.DeleteAsync(Token);
    }

    [Fact]
    public async Task UnreachableSharedStore_ReleasesItsLastProviderPinExactlyOnce()
    {
        using var writer = new ArcBufferWriter();
        writer.Write(new byte[] { 1, 2, 3 });
        using var source = writer.PeekSlice(writer.Length);
        var baseline = source.First.ReferenceCount;
        var abandoned = await CreateAbandonedStore(source);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(abandoned.IsAlive);
        Assert.Equal(baseline, source.First.ReferenceCount);
        Assert.Equal(new byte[] { 1, 2, 3 }, source.ToArray());
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async ValueTask<WeakReference> CreateAbandonedStore(ArcBuffer source)
    {
        var store = new VolatileJournalStorage.Store("abandoned");
        var storage = new VolatileJournalStorage(store, journalFormatKey: null);
        var baseline = source.First.ReferenceCount;
        await ((IRetainedJournalStorage)storage).AppendRetainedAsync(source, Token);
        Assert.Equal(baseline + 1, source.First.ReferenceCount);
        var result = new WeakReference(store);
        GC.KeepAlive(store);
        return result;
    }

    private static async Task SeedCompactionRequest(VolatileJournalStorage storage, byte[] bytes)
    {
        await storage.AppendAsync(new ReadOnlySequence<byte>(bytes), Token);
        for (var i = 0; i < 10; i++)
        {
            await storage.AppendAsync(ReadOnlySequence<byte>.Empty, Token);
        }

        Assert.True(storage.IsCompactionRequested);
    }

    private static async Task<byte[]> Read(IJournalStorage storage)
    {
        var consumer = new CapturingConsumer();
        await storage.ReadAsync(consumer, Token);
        Assert.Equal(1, consumer.CompletionCount);
        return consumer.Bytes.ToArray();
    }

    private sealed class CapturingConsumer(Action? firstRead = null) : IJournalStorageConsumer
    {
        private bool _entered;
        public List<byte> Bytes { get; } = [];
        public IJournalMetadata? Metadata { get; private set; }
        public int CompletionCount { get; private set; }
        public void Read(JournalBufferReader buffer, IJournalMetadata? metadata)
        {
            if (!_entered)
            {
                _entered = true;
                firstRead?.Invoke();
            }
            Metadata = metadata;
            Bytes.AddRange(buffer.ToArray());
            buffer.Skip(buffer.Length);
            if (buffer.IsCompleted) CompletionCount++;
        }
    }

    private sealed class SequenceSegment : ReadOnlySequenceSegment<byte>
    {
        public SequenceSegment(ReadOnlyMemory<byte> memory) => Memory = memory;
        public SequenceSegment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new SequenceSegment(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
    }
}
