using System.Buffers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Buffers.Adaptors;
using Orleans.Serialization.Session;
using Orleans.Storage;
using Xunit;

namespace Orleans.Journaling.Tests;

/// <summary>
/// Tests for the state manager, the core component of Orleans' journaling infrastructure.
/// 
/// The state manager coordinates multiple durable data structures (DurableDictionary, DurableList, etc.)
/// within a single grain, ensuring that all state changes are atomically journaled and can be
/// recovered together. It manages the lifecycle of states, handles persistence through
/// WriteStateAsync calls, and ensures consistent recovery after failures.
/// </summary>
[TestCategory("BVT")]
public class StateManagerTests : JournalingTestBase
{
    /// <summary>
    /// Tests the registration and basic operation of multiple states.
    /// Verifies that different types of durable collections can be registered
    /// with the manager and operate independently.
    /// </summary>
    [Fact]
    public async Task StateManager_RegisterState_Test()
    {
        // Arrange
        var sut = CreateTestSystem();
        var manager = sut.Manager;
        var codec = CodecProvider.GetCodec<int>();

        // Act - Register states
        var dictionary = new DurableDictionary<string, int>("dict1", manager, new OrleansBinaryDurableDictionaryCommandCodec<string, int>(CodecProvider.GetCodec<string>(), codec, SessionPool));
        var list = new DurableList<string>("list1", manager, new OrleansBinaryDurableListCommandCodec<string>(CodecProvider.GetCodec<string>(), SessionPool));
        var queue = new DurableQueue<int>("queue1", manager, new OrleansBinaryDurableQueueCommandCodec<int>(codec, SessionPool));
        await sut.Lifecycle.OnStart();

        // Add some data
        dictionary.Add("key1", 1);
        list.Add("item1");
        queue.Enqueue(42);

        // Write state
        await manager.WriteStateAsync(CancellationToken.None);

        // Assert - Data is correctly stored
        Assert.Equal(1, dictionary["key1"]);
        Assert.Equal("item1", list[0]);
        Assert.Equal(42, queue.Peek());
    }

    [Fact]
    public async Task StateManager_Initialize_UsesStreamingStorageRead()
    {
        var storage = new StreamingOnlyStorage();
        var sut = CreateTestSystem(storage: storage);

        await sut.Lifecycle.OnStart();

        Assert.True(storage.StreamingReadCalled);
    }

    [Fact]
    public async Task StateManager_Initialize_ThrowsWhenCompletedReadLeavesData()
    {
        var storage = new VolatileJournalStorage();
        using (var data = CreateBuffer([1, 2, 3]))
        {
            await storage.AppendAsync(data.AsReadOnlySequence(), CancellationToken.None);
        }

        var sut = CreateTestSystem(storage: storage, journalFormat: new NonConsumingJournalFormat());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.Lifecycle.OnStart().WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.NotNull(exception.InnerException);
        Assert.Contains("did not read the completed journal data", exception.InnerException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StateManager_Recovery_PreservesUnknownStateEntry()
    {
        var storage = new VolatileJournalStorage();
        using var segment = new OrleansBinaryJournalBufferWriter();
        using (var entry = segment.CreateJournalStreamWriter(new JournalStreamId(99)).BeginEntry())
        {
            entry.Writer.Write(new byte[] { 1, 2, 3 });
            entry.Commit();
        }

        using var data = segment.GetBuffer();
        var originalBytes = data.ToArray();
        await storage.AppendAsync(data.AsReadOnlySequence(), CancellationToken.None);
        var sut = CreateTestSystem(storage: storage);

        await sut.Lifecycle.OnStart().WaitAsync(TimeSpan.FromSeconds(10));
        await sut.Manager.WriteStateAsync(CancellationToken.None);

        var segmentBytes = Assert.Single(storage.Segments);
        Assert.Equal(originalBytes, segmentBytes);
    }

    [Fact]
    public async Task StateManager_UnknownStateCompaction_PreservesDecodedPayloadThroughFormatWriter()
    {
        var physicalBytes = new byte[] { 0xF0, 0x0D, 0x99 };
        var decodedPayload = new byte[] { 1, 2, 3 };
        var storage = new CapturingStorage { IsCompactionRequested = true };
        using (var data = CreateBuffer(physicalBytes))
        {
            await storage.AppendAsync(data.AsReadOnlySequence(), CancellationToken.None);
        }

        var format = new DecodedPayloadOnlyJournalFormat(new JournalStreamId(99), decodedPayload, SessionPool);
        var sut = CreateTestSystem(storage: storage, journalFormat: format);

        await sut.Lifecycle.OnStart().WaitAsync(TimeSpan.FromSeconds(10));
        await sut.Manager.WriteStateAsync(CancellationToken.None);

        var replacement = Assert.Single(storage.Replaces);
        Assert.NotEqual(physicalBytes, replacement);

        var entries = ReadBinaryEntries(replacement);
        var preserved = Assert.Single(entries, entry => entry.StreamId.Value == 99);
        Assert.Equal(decodedPayload, preserved.Payload);

        Assert.Contains(format.Writers, writer => writer.BeganEntryIds.Contains(99u));
    }

    [Fact]
    public async Task StateManager_RetiredStateCompaction_WritesPreservedPayloadThroughFormatOwnedEntry()
    {
        var storage = new CapturingStorage();
        var initial = CreateTestSystem(storage: storage);
        var dictionary = new DurableDictionary<string, int>("retired", initial.Manager, CreateDictionaryCodec<string, int>());

        await initial.Lifecycle.OnStart();
        dictionary.Add("key", 7);
        await initial.Manager.WriteStateAsync(CancellationToken.None);

        storage.IsCompactionRequested = true;
        var format = new TrackingJournalFormat(SessionPool);
        var compacting = CreateTestSystem(storage: storage, journalFormat: format);

        await compacting.Lifecycle.OnStart();
        await compacting.Manager.WriteStateAsync(CancellationToken.None);

        Assert.Contains(format.Writers, writer => writer.BeganEntryIds.Any(id => id >= 8));
        Assert.Single(storage.Replaces);

        var recovered = CreateTestSystem(storage: storage);
        var recoveredDictionary = new DurableDictionary<string, int>("retired", recovered.Manager, CreateDictionaryCodec<string, int>());
        await recovered.Lifecycle.OnStart();

        Assert.Equal(7, recoveredDictionary["key"]);
    }

    [Fact]
    public async Task StateManager_DirectWrites_UseFormatOwnedCurrentSegmentWriter()
    {
        var storage = new CapturingStorage();
        var format = new TrackingJournalFormat(SessionPool);
        var sut = CreateTestSystem(storage: storage, journalFormat: format);
        var dictionary = new DurableDictionary<string, int>("dict", sut.Manager, CreateDictionaryCodec<string, int>());

        await sut.Lifecycle.OnStart();
        dictionary.Add("key", 1);
        await sut.Manager.WriteStateAsync(CancellationToken.None);

        var writer = Assert.Single(format.Writers);
        Assert.Single(storage.Appends);
        Assert.Empty(storage.Replaces);
        Assert.Contains(0u, writer.BeganEntryIds);
        Assert.Contains(writer.BeganEntryIds, id => id >= 8);
    }

    [Fact]
    public async Task StateManager_AppendJournalFlush_UsesFormatOwnedWriter()
    {
        var storage = new CapturingStorage();
        var format = new TrackingJournalFormat(SessionPool);
        var sut = CreateTestSystem(storage: storage, journalFormat: format);
        var value = new DurableValue<int>("value", sut.Manager, CreateValueCodec<int>());

        await sut.Lifecycle.OnStart();
        value.Value = 42;
        await sut.Manager.WriteStateAsync(CancellationToken.None);

        var writer = Assert.Single(format.Writers);
        Assert.Single(storage.Appends);
        Assert.Contains(writer.BeganEntryIds, id => id >= 8);
        Assert.True(storage.Appends[0].Length > 0);
    }

    [Fact]
    public async Task StateManager_BinaryAppend_StoresBinaryVarUIntEntries()
    {
        var storage = new CapturingStorage();
        var sut = CreateTestSystem(storage: storage);
        var dictionary = new DurableDictionary<string, int>("dict", sut.Manager, CreateDictionaryCodec<string, int>());

        await sut.Lifecycle.OnStart();
        dictionary.Add("key", 1);
        await sut.Manager.WriteStateAsync(CancellationToken.None);

        var append = Assert.Single(storage.Appends);
        Assert.Empty(storage.Replaces);
        AssertContainsRuntimeAndApplicationEntries(ReadBinaryEntries(append));
    }

    [Fact]
    public async Task StateManager_AppendBufferIsBorrowedUntilStorageCompletes()
    {
        var storage = new DelayedBorrowingStorage();
        var sut = CreateTestSystem(storage: storage);
        var value = new DurableValue<int>("value", sut.Manager, CreateValueCodec<int>());

        await sut.Lifecycle.OnStart();
        value.Value = 42;
        await sut.Manager.WriteStateAsync(CancellationToken.None);

        Assert.NotNull(storage.AppendBytesAfterYield);
        Assert.NotEmpty(storage.AppendBytesAfterYield);
    }

    [Fact]
    public async Task StateManager_ReplaceBufferIsBorrowedUntilStorageCompletes()
    {
        var storage = new DelayedBorrowingStorage { IsCompactionRequested = true };
        var sut = CreateTestSystem(storage: storage);
        var value = new DurableValue<int>("value", sut.Manager, CreateValueCodec<int>());

        await sut.Lifecycle.OnStart();
        value.Value = 42;
        await sut.Manager.WriteStateAsync(CancellationToken.None);

        Assert.NotNull(storage.ReplaceBytesAfterYield);
        Assert.NotEmpty(storage.ReplaceBytesAfterYield);
    }

    [Fact]
    public async Task StateManager_BinarySnapshot_StoresBinaryVarUIntEntries()
    {
        var storage = new CapturingStorage { IsCompactionRequested = true };
        var sut = CreateTestSystem(storage: storage);
        var dictionary = new DurableDictionary<string, int>("dict", sut.Manager, CreateDictionaryCodec<string, int>());

        await sut.Lifecycle.OnStart();
        dictionary.Add("key", 1);
        await sut.Manager.WriteStateAsync(CancellationToken.None);

        var replacement = Assert.Single(storage.Replaces);
        Assert.Single(storage.Appends);
        AssertContainsRuntimeAndApplicationEntries(ReadBinaryEntries(replacement));
    }

    [Fact]
    public async Task StateManager_DeleteState_ReallocatesApplicationStreamsAboveInternalRange()
    {
        var storage = new CapturingStorage();
        var sut = CreateTestSystem(storage: storage);
        var dictionary = new DurableDictionary<string, int>("dict", sut.Manager, CreateDictionaryCodec<string, int>());

        await sut.Lifecycle.OnStart();
        dictionary.Add("before", 1);
        await sut.Manager.WriteStateAsync(CancellationToken.None);

        await sut.Manager.DeleteStateAsync(CancellationToken.None);
        dictionary.Add("after", 2);
        await sut.Manager.WriteStateAsync(CancellationToken.None);

        Assert.Equal(2, storage.Appends.Count);
        var entries = ReadBinaryEntries(storage.Appends[^1]);
        Assert.DoesNotContain(entries, entry => entry.StreamId.Value == 1);
        Assert.Contains(entries, entry => entry.StreamId.Value >= 8);

        var recovered = CreateTestSystem(storage: storage);
        var recoveredDictionary = new DurableDictionary<string, int>("dict", recovered.Manager, CreateDictionaryCodec<string, int>());
        await recovered.Lifecycle.OnStart();

        Assert.Single(recoveredDictionary);
        Assert.Equal(2, recoveredDictionary["after"]);
    }

    [Fact]
    public async Task StateManager_DeleteState_ClearsDurableValueDirtyFlag()
    {
        var storage = new CapturingStorage();
        var sut = CreateTestSystem(storage: storage);
        var value = new DurableValue<int>("value", sut.Manager, CreateValueCodec<int>());

        await sut.Lifecycle.OnStart();
        value.Value = 1;
        await sut.Manager.WriteStateAsync(CancellationToken.None);
        value.Value = 2;

        await sut.Manager.DeleteStateAsync(CancellationToken.None);
        await sut.Manager.WriteStateAsync(CancellationToken.None);

        Assert.Equal(2, storage.Appends.Count);
        var postDeleteEntries = ReadBinaryEntries(storage.Appends[^1]);
        Assert.Contains(postDeleteEntries, entry => entry.StreamId.Value == 0);
        Assert.DoesNotContain(postDeleteEntries, entry => entry.StreamId.Value >= 8);
    }

    [Fact]
    public async Task StateManager_SnapshotFlush_ResetsPendingAppendData()
    {
        var storage = new CapturingStorage { IsCompactionRequested = true };
        var format = new TrackingJournalFormat(SessionPool);
        var sut = CreateTestSystem(storage: storage, journalFormat: format);
        var dictionary = new DurableDictionary<string, int>("dict", sut.Manager, CreateDictionaryCodec<string, int>());

        await sut.Lifecycle.OnStart();
        dictionary.Add("key", 1);
        await sut.Manager.WriteStateAsync(CancellationToken.None);

        Assert.Single(storage.Appends);
        Assert.Single(storage.Replaces);

        var recovered = CreateTestSystem(storage: storage, journalFormat: new TrackingJournalFormat(SessionPool));
        var recoveredDictionary = new DurableDictionary<string, int>("dict", recovered.Manager, CreateDictionaryCodec<string, int>());
        await recovered.Lifecycle.OnStart();

        Assert.Equal(1, recoveredDictionary["key"]);
    }

    [Fact]
    public async Task StateManager_DirectWriteFailure_AbortsEntryBeforeMutation()
    {
        var storage = new CapturingStorage();
        var format = new TrackingJournalFormat(SessionPool);
        var sut = CreateTestSystem(storage: storage, journalFormat: format);
        var dictionary = new DurableDictionary<int, int>("dict", sut.Manager, new ThrowingDictionarySetCodec<int, int>());

        await sut.Lifecycle.OnStart();
        var writer = Assert.Single(format.Writers);
        var lengthBefore = GetCommittedLength(writer);

        Assert.Throws<InvalidOperationException>(() => dictionary.Add(1, 1));

        Assert.Empty(dictionary);
        Assert.Equal(lengthBefore, GetCommittedLength(writer));

        await sut.Manager.WriteStateAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StateManager_DirectWriteFailure_DoesNotPersistPartialEntry()
    {
        var storage = new CapturingStorage();
        var format = new TrackingJournalFormat(SessionPool);
        var sut = CreateTestSystem(storage: storage, journalFormat: format);
        var codec = new ToggleThrowingDictionarySetCodec<int, int>(CreateDictionaryCodec<int, int>());
        var dictionary = new DurableDictionary<int, int>("dict", sut.Manager, codec);

        await sut.Lifecycle.OnStart();
        codec.ThrowOnSet = true;
        Assert.Throws<InvalidOperationException>(() => dictionary.Add(1, 1));

        codec.ThrowOnSet = false;
        dictionary.Add(1, 1);
        await sut.Manager.WriteStateAsync(CancellationToken.None);

        var recovered = CreateTestSystem(storage: storage, journalFormat: new TrackingJournalFormat(SessionPool));
        var recoveredDictionary = new DurableDictionary<int, int>("dict", recovered.Manager, CreateDictionaryCodec<int, int>());
        await recovered.Lifecycle.OnStart();

        Assert.Single(recoveredDictionary);
        Assert.Equal(1, recoveredDictionary[1]);
    }

    [Fact]
    public async Task StateManager_WriteStateAsync_RecoversAfterInconsistentStateException()
    {
        var storage = new CapturingStorage();
        var sut = CreateTestSystem(storage: storage);
        var dictionary = new DurableDictionary<string, int>("dict", sut.Manager, CreateDictionaryCodec<string, int>());

        await sut.Lifecycle.OnStart();
        dictionary.Add("first", 1);
        await sut.Manager.WriteStateAsync(CancellationToken.None);

        var expected = new InconsistentStateException("Expected storage write conflict.");
        storage.NextAppendException = expected;
        dictionary.Add("second", 2);

        var exception = await Assert.ThrowsAsync<InconsistentStateException>(
            () => sut.Manager.WriteStateAsync(CancellationToken.None).AsTask());
        Assert.Same(expected, exception);

        await sut.Manager.WriteStateAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(dictionary.ContainsKey("first"));
        Assert.False(dictionary.ContainsKey("second"));

        var recovered = CreateTestSystem(storage: storage);
        var recoveredDictionary = new DurableDictionary<string, int>("dict", recovered.Manager, CreateDictionaryCodec<string, int>());
        await recovered.Lifecycle.OnStart();

        Assert.Equal(1, recoveredDictionary["first"]);
        Assert.False(recoveredDictionary.ContainsKey("second"));
    }

    [Fact]
    public async Task StateManager_WriteStateAsync_PreservesPendingStateAfterTransientStorageWriteFailure()
    {
        var storage = new CapturingStorage();
        var sut = CreateTestSystem(storage: storage);
        var dictionary = new DurableDictionary<string, int>("dict", sut.Manager, CreateDictionaryCodec<string, int>());

        await sut.Lifecycle.OnStart();
        dictionary.Add("first", 1);
        await sut.Manager.WriteStateAsync(CancellationToken.None);
        storage.ResetReadConsumeCount();

        var expected = new IOException("Expected storage write failure.");
        storage.NextAppendException = expected;
        dictionary.Add("second", 2);

        var exception = await Assert.ThrowsAsync<IOException>(
            () => sut.Manager.WriteStateAsync(CancellationToken.None).AsTask());
        Assert.Same(expected, exception);

        await sut.Manager.WriteStateAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, storage.ReadConsumeCount);
        Assert.Equal(2, dictionary["second"]);

        var recovered = CreateTestSystem(storage: storage);
        var recoveredDictionary = new DurableDictionary<string, int>("dict", recovered.Manager, CreateDictionaryCodec<string, int>());
        await recovered.Lifecycle.OnStart();

        Assert.Equal(1, recoveredDictionary["first"]);
        Assert.Equal(2, recoveredDictionary["second"]);
    }

    [Fact]
    public async Task StateManager_WriteStateAsync_CoalescesQueuedWrites()
    {
        var storage = new CapturingStorage();
        var sut = CreateTestSystem(storage: storage);
        var state = new AlwaysWritingState();
        sut.Manager.RegisterState("state", state);

        var first = sut.Manager.WriteStateAsync(CancellationToken.None).AsTask();
        var second = sut.Manager.WriteStateAsync(CancellationToken.None).AsTask();

        await sut.Lifecycle.OnStart().WaitAsync(TimeSpan.FromSeconds(10));
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, state.AppendEntriesCount);
        Assert.Single(storage.Appends);
    }

    [Fact]
    public async Task StateManager_DeleteStateAsync_CoalescesQueuedDeletes()
    {
        var storage = new CapturingStorage();
        var sut = CreateTestSystem(storage: storage);

        var first = sut.Manager.DeleteStateAsync(CancellationToken.None).AsTask();
        var second = sut.Manager.DeleteStateAsync(CancellationToken.None).AsTask();

        await sut.Lifecycle.OnStart().WaitAsync(TimeSpan.FromSeconds(10));
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, storage.DeleteCount);
    }

    [Fact]
    public async Task StateManager_WriteStateAsync_DoesNotObserveActiveEntry()
    {
        var storage = new CapturingStorage();
        var format = new TrackingJournalFormat(SessionPool);
        var sut = CreateTestSystem(storage: storage, journalFormat: format);
        var state = new ManualDirectWriteState();
        sut.Manager.RegisterState("manual", state);

        await sut.Lifecycle.OnStart();

        Task writeTask = Task.CompletedTask;
        using var writeStarted = new ManualResetEventSlim();
        var entry = state.BeginEntry();
        try
        {
            entry.Writer.Write(new byte[] { 1, 2, 3 });
            writeTask = Task.Run(async () =>
            {
                writeStarted.Set();
                await sut.Manager.WriteStateAsync(CancellationToken.None);
            });
            Assert.True(writeStarted.Wait(TimeSpan.FromSeconds(10)));

            Assert.True(SpinWait.SpinUntil(() => writeTask.IsCompleted, TimeSpan.FromSeconds(10)));
            Assert.True(writeTask.IsCompletedSuccessfully, writeTask.Exception?.ToString());
            Assert.False(state.AppendEntriesObservedOpenEntry);
            var append = Assert.Single(storage.Appends);
            Assert.DoesNotContain(ReadBinaryEntries(append), entry => entry.StreamId.Value >= 8);

            state.MarkEntryClosing();
            entry.Commit();
        }
        finally
        {
            state.MarkEntryClosing();
            entry.Dispose();
        }

        await sut.Manager.WriteStateAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, storage.Appends.Count);
        Assert.Contains(ReadBinaryEntries(storage.Appends[1]), entry => entry.StreamId.Value >= 8);
    }

    [Fact]
    public async Task StateManager_SnapshotWrite_DoesNotDiscardActiveEntry()
    {
        var storage = new CapturingStorage { IsCompactionRequested = true, DelayReplace = true };
        var format = new TrackingJournalFormat(SessionPool);
        var sut = CreateTestSystem(storage: storage, journalFormat: format);
        var state = new ManualDirectWriteState();
        sut.Manager.RegisterState("manual", state);

        await sut.Lifecycle.OnStart();

        var writeTask = StartSnapshotAndCommitActiveEntry();

        await writeTask.WaitAsync(TimeSpan.FromSeconds(10));
        var replacement = Assert.Single(storage.Replaces);
        Assert.DoesNotContain(ReadBinaryEntries(replacement), entry => entry.StreamId.Value >= 8);
        Assert.Single(storage.Appends);

        storage.IsCompactionRequested = false;
        await sut.Manager.WriteStateAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, storage.Appends.Count);
        var append = storage.Appends[^1];
        Assert.Contains(ReadBinaryEntries(append), entry => entry.StreamId.Value >= 8);

        Task StartSnapshotAndCommitActiveEntry()
        {
            Task writeTask = Task.CompletedTask;
            using var entry = state.BeginEntry();
            try
            {
                entry.Writer.Write(new byte[] { 1, 2, 3 });
                writeTask = sut.Manager.WriteStateAsync(CancellationToken.None).AsTask();
                Assert.True(SpinWait.SpinUntil(() => storage.ReplaceStarted.Task.IsCompleted, TimeSpan.FromSeconds(10)), writeTask.Exception?.ToString());
                Assert.False(writeTask.IsCompleted);

                state.MarkEntryClosing();
                entry.Commit();
                storage.AllowReplace.SetResult();
                return writeTask;
            }
            finally
            {
                state.MarkEntryClosing();
                entry.Dispose();
            }
        }
    }

    /// <summary>
    /// Tests that all registered states are correctly recovered together.
    /// Verifies that the manager maintains consistency across multiple collections
    /// during recovery from persisted state.
    /// </summary>
    [Fact]
    public async Task StateManager_StateRecovery_Test()
    {
        // Arrange
        var sut = CreateTestSystem();

        // Create and populate states
        var dictionary = new DurableDictionary<string, int>("dict1", sut.Manager, new OrleansBinaryDurableDictionaryCommandCodec<string, int>(CodecProvider.GetCodec<string>(), CodecProvider.GetCodec<int>(), SessionPool));
        var list = new DurableList<string>("list1", sut.Manager, new OrleansBinaryDurableListCommandCodec<string>(CodecProvider.GetCodec<string>(), SessionPool));
        await sut.Lifecycle.OnStart();

        dictionary.Add("key1", 1);
        dictionary.Add("key2", 2);
        list.Add("item1");
        list.Add("item2");

        await sut.Manager.WriteStateAsync(CancellationToken.None);

        // Act - Create new manager with same storage
        var sut2 = CreateTestSystem(storage: sut.Storage);
        var recoveredDict = new DurableDictionary<string, int>("dict1", sut2.Manager, new OrleansBinaryDurableDictionaryCommandCodec<string, int>(CodecProvider.GetCodec<string>(), CodecProvider.GetCodec<int>(), SessionPool));
        var recoveredList = new DurableList<string>("list1", sut2.Manager, new OrleansBinaryDurableListCommandCodec<string>(CodecProvider.GetCodec<string>(), SessionPool));
        await sut2.Lifecycle.OnStart();

        // Assert - State should be recovered
        Assert.Equal(2, recoveredDict.Count);
        Assert.Equal(1, recoveredDict["key1"]);
        Assert.Equal(2, recoveredDict["key2"]);

        Assert.Equal(2, recoveredList.Count);
        Assert.Equal("item1", recoveredList[0]);
        Assert.Equal("item2", recoveredList[1]);
    }

    [Fact]
    public async Task StateManager_Recovery_UsesSelectedJournalFormatRead()
    {
        var storage = new CapturingStorage();
        var initial = CreateTestSystem(storage: storage);
        var value = new DurableValue<int>("value", initial.Manager, CreateValueCodec<int>());

        await initial.Lifecycle.OnStart();
        value.Value = 42;
        await initial.Manager.WriteStateAsync(CancellationToken.None);

        var format = new TrackingJournalFormat(SessionPool);
        var recovered = CreateTestSystem(storage: storage, journalFormat: format);
        var recoveredValue = new DurableValue<int>("value", recovered.Manager, CreateValueCodec<int>());

        await recovered.Lifecycle.OnStart();

        Assert.Equal(42, recoveredValue.Value);
        Assert.True(format.ReadCount > 0);
    }

    [Fact]
    public async Task StateManager_RegisterState_AfterRecovery_AllocatesAboveRecoveredStreamIds()
    {
        var storage = new CapturingStorage();
        var recoveredStreamId = new JournalStreamId(12);
        using (var segment = new OrleansBinaryJournalBufferWriter())
        {
            AppendDirectorySet(segment, "existing", recoveredStreamId);
            CreateValueCodec<int>().WriteSet(42, segment.CreateJournalStreamWriter(recoveredStreamId));
            using var committed = segment.GetBuffer();
            await storage.AppendAsync(committed.AsReadOnlySequence(), CancellationToken.None);
        }

        var sut = CreateTestSystem(storage: storage);
        var existing = new DurableValue<int>("existing", sut.Manager, CreateValueCodec<int>());
        var next = new DurableValue<int>("next", sut.Manager, CreateValueCodec<int>());

        await sut.Lifecycle.OnStart();
        next.Value = 99;
        await sut.Manager.WriteStateAsync(CancellationToken.None);

        Assert.Equal(42, existing.Value);
        var entries = ReadBinaryEntries(storage.Appends[^1]);
        Assert.Contains(entries, entry => entry.StreamId.Value == recoveredStreamId.Value + 1);
    }

    [Fact]
    public async Task StateManager_Recovery_ReadsConcatenatedJournalData()
    {
        var storage = new CapturingStorage();
        var initial = CreateTestSystem(storage: storage);
        var value = new DurableValue<int>("value", initial.Manager, CreateValueCodec<int>());

        await initial.Lifecycle.OnStart();
        value.Value = 1;
        await initial.Manager.WriteStateAsync(CancellationToken.None);
        value.Value = 2;
        await initial.Manager.WriteStateAsync(CancellationToken.None);

        storage.ConcatenateReads = true;
        var recovered = CreateTestSystem(storage: storage);
        var recoveredValue = new DurableValue<int>("value", recovered.Manager, CreateValueCodec<int>());

        await recovered.Lifecycle.OnStart();

        Assert.Equal(2, recoveredValue.Value);
        Assert.Equal(1, storage.ReadConsumeCount);
    }

    [Fact]
    public async Task StateManager_Recovery_BuffersEntriesSplitAcrossStorageChunks()
    {
        var storage = new CapturingStorage();
        var initial = CreateTestSystem(storage: storage);
        var value = new DurableValue<int>("value", initial.Manager, CreateValueCodec<int>());

        await initial.Lifecycle.OnStart();
        value.Value = 1;
        await initial.Manager.WriteStateAsync(CancellationToken.None);
        value.Value = 2;
        await initial.Manager.WriteStateAsync(CancellationToken.None);

        var persistedBytes = storage.Appends.SelectMany(static segment => segment).ToArray();
        var splitStorage = new ChunkedReadStorage(persistedBytes, chunkSize: 1);
        var recovered = CreateTestSystem(storage: splitStorage);
        var recoveredValue = new DurableValue<int>("value", recovered.Manager, CreateValueCodec<int>());

        await recovered.Lifecycle.OnStart();

        Assert.Equal(2, recoveredValue.Value);
        Assert.Equal(persistedBytes.Length, splitStorage.ReadConsumeCount);
    }

    [Fact]
    public async Task StateManager_Recovery_RejectsMalformedTrailingData()
    {
        byte[] bytes;
        using (var segment = new OrleansBinaryJournalBufferWriter())
        {
            using (var entry = segment.CreateJournalStreamWriter(new JournalStreamId(99)).BeginEntry())
            {
                entry.Writer.Write(new byte[] { 1, 2, 3 });
                entry.Commit();
            }

            using var committed = segment.GetBuffer();
            bytes = [.. committed.ToArray(), 0x02];
        }

        var storage = new RawReadStorage(bytes);
        var sut = CreateTestSystem(storage: storage);

        InvalidOperationException exception;
        try
        {
            exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => sut.Lifecycle.OnStart().WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            await sut.Lifecycle.OnStop(CancellationToken.None);
        }

        Assert.Contains("journal format key 'orleans-binary'", exception.Message, StringComparison.Ordinal);
        var inner = Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.Contains("Malformed binary journal entry stream", inner.Message, StringComparison.Ordinal);
        Assert.Contains("truncated varuint32 entry length prefix", inner.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StateManager_RecoveryRetry_ReplaysFixedStorage()
    {
        var validBytes = CreatePersistedValueBytes("value", 42);
        var storage = new MutableReadStorage([.. validBytes, 1, 2, 3]);
        var sut = CreateTestSystem(storage: storage);
        var value = new DurableValue<int>("value", sut.Manager, CreateValueCodec<int>());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.Lifecycle.OnStart().WaitAsync(TimeSpan.FromSeconds(10)));

        storage.Bytes = validBytes;
        await sut.Manager.WriteStateAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(42, value.Value);
        await sut.Lifecycle.OnStop(CancellationToken.None);
    }

    [Fact]
    public async Task StateManager_RecoveryRetry_PreservesUnknownStreamOnce()
    {
        var validBytes = CreateUnknownStreamBytes(new JournalStreamId(99), [1, 2, 3]);
        var storage = new MutableReadStorage([.. validBytes, 1, 2, 3]) { IsCompactionRequested = true };
        var sut = CreateTestSystem(storage: storage);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.Lifecycle.OnStart().WaitAsync(TimeSpan.FromSeconds(10)));

        storage.Bytes = validBytes;
        await sut.Manager.WriteStateAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        var replacement = Assert.Single(storage.Replaces);
        var preserved = Assert.Single(ReadBinaryEntries(replacement), entry => entry.StreamId.Value == 99);
        Assert.Equal([1, 2, 3], preserved.Payload);
        await sut.Lifecycle.OnStop(CancellationToken.None);
    }

    [Fact]
    public async Task StateManager_RecoveryRetry_RemovesStaleRetiredPlaceholder()
    {
        var storage = new MutableReadStorage([.. CreateNamedUnknownStreamBytes("stale", new JournalStreamId(8), [1, 2, 3]), 1, 2, 3]);
        var sut = CreateTestSystem(storage: storage);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.Lifecycle.OnStart().WaitAsync(TimeSpan.FromSeconds(10)));

        storage.Bytes = [];
        await sut.Manager.WriteStateAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(sut.Manager.TryGetState("stale", out _));
        await sut.Lifecycle.OnStop(CancellationToken.None);
    }

    [Fact]
    public async Task StateManager_Recovery_DoesNotWrapStorageReadException()
    {
        var storage = new ThrowingReadStorage();
        var sut = CreateTestSystem(storage: storage);

        InvalidOperationException exception;
        try
        {
            exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => sut.Lifecycle.OnStart().WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            await sut.Lifecycle.OnStop(CancellationToken.None);
        }

        Assert.Same(storage.Exception, exception);
        Assert.Null(exception.InnerException);
    }

    /// <summary>
    /// Tests multiple WriteStateAsync calls with operations in between.
    /// Verifies that each WriteStateAsync creates a consistent checkpoint
    /// and that the final state is correctly recovered.
    /// </summary>
    [Fact]
    public async Task StateManager_MultipleWriteStates_Test()
    {
        // Arrange
        var sut = CreateTestSystem();
        var manager = sut.Manager;
        var dictionary = new DurableDictionary<string, int>("dict1", sut.Manager, new OrleansBinaryDurableDictionaryCommandCodec<string, int>(CodecProvider.GetCodec<string>(), CodecProvider.GetCodec<int>(), SessionPool));
        await sut.Lifecycle.OnStart();

        // Act - Multiple operations with WriteState in between
        dictionary.Add("key1", 1);
        await manager.WriteStateAsync(CancellationToken.None);

        dictionary.Add("key2", 2);
        await manager.WriteStateAsync(CancellationToken.None);

        dictionary["key1"] = 10;
        await manager.WriteStateAsync(CancellationToken.None);

        dictionary.Remove("key2");
        await manager.WriteStateAsync(CancellationToken.None);

        // Assert - Final state is correct
        Assert.Single(dictionary);
        Assert.Equal(10, dictionary["key1"]);
        Assert.False(dictionary.ContainsKey("key2"));

        // Create new manager to verify recovery
        var sut2 = CreateTestSystem(storage: sut.Storage);
        var recoveredDict = new DurableDictionary<string, int>("dict1", sut2.Manager, new OrleansBinaryDurableDictionaryCommandCodec<string, int>(CodecProvider.GetCodec<string>(), CodecProvider.GetCodec<int>(), SessionPool));
        await sut2.Lifecycle.OnStart();

        // Assert - Recovery should have final state
        Assert.Single(recoveredDict);
        Assert.Equal(10, recoveredDict["key1"]);
        Assert.False(recoveredDict.ContainsKey("key2"));
    }

    /// <summary>
    /// Tests managing multiple states of different types simultaneously.
    /// Verifies that the manager correctly handles diverse data structures
    /// (dictionaries with different key/value types, lists, values) in a single grain.
    /// </summary>
    [Fact]
    public async Task StateManager_MultipleStates_Test()
    {
        // Arrange
        var sut = CreateTestSystem();
        var manager = sut.Manager;

        // Create multiple states with different types
        var intDict = new DurableDictionary<int, string>("intDict", manager, new OrleansBinaryDurableDictionaryCommandCodec<int, string>(CodecProvider.GetCodec<int>(), CodecProvider.GetCodec<string>(), SessionPool));
        var stringList = new DurableList<string>("stringList", manager, new OrleansBinaryDurableListCommandCodec<string>(CodecProvider.GetCodec<string>(), SessionPool));
        var personValue = new DurableValue<TestPerson>("personValue", manager, new OrleansBinaryDurableValueCommandCodec<TestPerson>(CodecProvider.GetCodec<TestPerson>(), SessionPool));
        await sut.Lifecycle.OnStart();

        // Act - Populate all states
        intDict.Add(1, "one");
        intDict.Add(2, "two");

        stringList.Add("item1");
        stringList.Add("item2");

        personValue.Value = new TestPerson { Id = 100, Name = "Test Person", Age = 30 };

        await manager.WriteStateAsync(CancellationToken.None);

        // Assert - All should have correct values
        Assert.Equal(2, intDict.Count);
        Assert.Equal("one", intDict[1]);

        Assert.Equal(2, stringList.Count);
        Assert.Equal("item1", stringList[0]);

        Assert.NotNull(personValue.Value);
        Assert.Equal(100, personValue.Value.Id);
        Assert.Equal("Test Person", personValue.Value.Name);

        // Create new manager to verify recovery of multiple states
        var sut2 = CreateTestSystem(storage: sut.Storage);
        var recoveredIntDict = new DurableDictionary<int, string>("intDict", sut2.Manager, new OrleansBinaryDurableDictionaryCommandCodec<int, string>(CodecProvider.GetCodec<int>(), CodecProvider.GetCodec<string>(), SessionPool));
        var recoveredStringList = new DurableList<string>("stringList", sut2.Manager, new OrleansBinaryDurableListCommandCodec<string>(CodecProvider.GetCodec<string>(), SessionPool));
        var recoveredPersonValue = new DurableValue<TestPerson>("personValue", sut2.Manager, new OrleansBinaryDurableValueCommandCodec<TestPerson>(CodecProvider.GetCodec<TestPerson>(), SessionPool));
        await sut2.Lifecycle.OnStart();

        // Assert - All should be recovered with correct values
        Assert.Equal(2, recoveredIntDict.Count);
        Assert.Equal("one", recoveredIntDict[1]);

        Assert.Equal(2, recoveredStringList.Count);
        Assert.Equal("item1", recoveredStringList[0]);

        Assert.NotNull(recoveredPersonValue.Value);
        Assert.Equal(100, recoveredPersonValue.Value.Id);
        Assert.Equal("Test Person", recoveredPersonValue.Value.Name);
    }

    /// <summary>
    /// Tests that multiple states can operate independently without interference.
    /// Verifies namespace isolation between different states with similar keys.
    /// </summary>
    [Fact]
    public async Task StateManager_Concurrency_Test()
    {
        // Arrange
        var sut = CreateTestSystem();
        var manager = sut.Manager;
        var dict1 = new DurableDictionary<string, int>("dict1", manager, new OrleansBinaryDurableDictionaryCommandCodec<string, int>(CodecProvider.GetCodec<string>(), CodecProvider.GetCodec<int>(), SessionPool));
        var dict2 = new DurableDictionary<string, int>("dict2", manager, new OrleansBinaryDurableDictionaryCommandCodec<string, int>(CodecProvider.GetCodec<string>(), CodecProvider.GetCodec<int>(), SessionPool));
        await sut.Lifecycle.OnStart();

        // Act - Simulate concurrent operations on different states
        dict1.Add("key1", 1);
        dict2.Add("key1", 100);

        dict1.Add("key2", 2);
        dict2.Add("key2", 200);

        await manager.WriteStateAsync(CancellationToken.None);

        // Assert - Both states should have their correct values
        Assert.Equal(2, dict1.Count);
        Assert.Equal(2, dict2.Count);

        Assert.Equal(1, dict1["key1"]);
        Assert.Equal(100, dict2["key1"]);

        Assert.Equal(2, dict1["key2"]);
        Assert.Equal(200, dict2["key2"]);
    }

    /// <summary>
    /// Stress test for state recovery with large amounts of data.
    /// Verifies that the journaling system can handle and recover states
    /// containing thousands of entries without data loss or corruption.
    /// </summary>
    [Fact]
    public async Task StateManager_LargeStateRecovery_Test()
    {
        // Arrange
        var sut = CreateTestSystem();
        var largeDict = new DurableDictionary<int, string>("largeDict", sut.Manager, new OrleansBinaryDurableDictionaryCommandCodec<int, string>(CodecProvider.GetCodec<int>(), CodecProvider.GetCodec<string>(), SessionPool));
        await sut.Lifecycle.OnStart();

        // Act - Add many items
        const int itemCount = 1000;
        for (int i = 0; i < itemCount; i++)
        {
            largeDict.Add(i, $"Value {i}");
        }

        await sut.Manager.WriteStateAsync(CancellationToken.None);

        // Create new manager for recovery
        var sut2 = CreateTestSystem(storage: sut.Storage);
        var recoveredDict = new DurableDictionary<int, string>("largeDict", sut2.Manager, new OrleansBinaryDurableDictionaryCommandCodec<int, string>(CodecProvider.GetCodec<int>(), CodecProvider.GetCodec<string>(), SessionPool));
        await sut2.Lifecycle.OnStart();

        // Assert - All items should be recovered
        Assert.Equal(itemCount, recoveredDict.Count);
        for (int i = 0; i < itemCount; i++)
        {
            Assert.Equal($"Value {i}", recoveredDict[i]);
        }
    }

    /// <summary>
    /// Tests the full lifecycle of a retired state. It is preserved and also reintroduced through an
    /// early compaction, but purged eventually after its grace period expires on later compactions.
    /// </summary>
    [Fact]
    public async Task StateManager_AutoRetiringStates()
    {
        const string DictToKeepKey = "dictToKeep";
        const string DictToRetireKey = "dictToRetire";

        var period = ManagerOptions.RetirementGracePeriod;
        var timeProvider = new FakeTimeProvider(DateTime.UtcNow);
        var storage = CreateStorage();

        // -------------- STEP 1 --------------

        // We begin with 2 dictionaries, one of which we will retire by means of not registering it in the manager.
        // This would be in the real-world developers removing it from the grain's ctor as a dependency.
        var sut1 = CreateTestSystem(storage, timeProvider);
        var dictToKeep1 = CreateTestState(DictToKeepKey, sut1.Manager);
        var dictToRetire2 = CreateTestState(DictToRetireKey, sut1.Manager);

        await sut1.Lifecycle.OnStart();

        dictToKeep1.Add("a", 1);
        dictToRetire2.Add("b", 1);

        await sut1.Manager.WriteStateAsync(CancellationToken.None);

        // -------------- STEP 2 --------------

        // This time, we only register the dictionary we want to keep, this marks dictToRetire as retired.
        var sut2 = CreateTestSystem(storage, timeProvider);
        var dictToKeep2 = CreateTestState(DictToKeepKey, sut2.Manager);

        await sut2.Lifecycle.OnStart();
        
        // The manager should have recovered the state for dictToKeep,
        // and created a DurableNothing placeholder for dictToRetire (we cant test for it at this point). 
        Assert.Equal(1, dictToKeep2["a"]);

        // We advance time by half the grace period to see if we can save it from purging.
        timeProvider.Advance(period / 2);

        await TriggerCompaction(sut2.Manager, dictToKeep2);

        // -------------- STEP 3 --------------

        // Verify that the retired dictionary was NOT purged by this compaction, as only half the time has passed.
        var sut3 = CreateTestSystem(storage, timeProvider);
        var dictToKeep3 = CreateTestState(DictToKeepKey, sut3.Manager);
        var dictToRetire3 = CreateTestState(DictToRetireKey, sut3.Manager);

        await sut3.Lifecycle.OnStart();

        Assert.Equal(10, dictToKeep3["a"]);

        // The fact this entry ["b", 1] exists proves that the state of dictToRetire was preserved, even though we did not register it in step 2.
        Assert.Equal(1, dictToRetire3["b"]);

        // By advancing time by another half-period we cover the full period. But since we have re-introduced dictToRetire, we should have un-retired it.
        // This is similar to step 2, but there we avoided purging due to time not being due, whereas here we avoid purging due to re-registration.
        timeProvider.Advance(period / 2);

        dictToRetire3["b"] = 2;
        await TriggerCompaction(sut3.Manager, dictToKeep3);

        var sut3Recovered = CreateTestSystem(storage, timeProvider);
        var dictToRetire3Recovered = CreateTestState(DictToRetireKey, sut3Recovered.Manager);
        await sut3Recovered.Lifecycle.OnStart();
        Assert.Equal(2, dictToRetire3Recovered["b"]);

        // -------------- STEP 4 --------------

        // Because of re-registration is step 3 (to test it was not purged), this means dictToRetire has been removed from the tracker.
        // Again as in step 2, we only register the dictionary we want to keep, this marks dictToRetire as retired.
        var sut4 = CreateTestSystem(storage, timeProvider);
        var dictToKeep4 = CreateTestState(DictToKeepKey, sut4.Manager);

        await sut4.Lifecycle.OnStart();

        // The manager should have recovered the state for dictToKeep.
        // It should have created a DurableNothing placeholder for dictToRetire, but we can not test for that.


        // This time we advance time to cover the full period. Note that this is necessary because a side effect of step 3
        // was that dictToRetire was removed from the tracker (since it came back), so just triggering a compaction won't cut it
        // as time to retire will essentially be reset to "now".
        timeProvider.Advance(period);

        // This compaction should finally purge it.
        await TriggerCompaction(sut4.Manager, dictToKeep4);

        // -------------- STEP 5 --------------

        // At this point, the manager has performed a snapshot, so it should have purged the dictToRetire data.
        // By registering both dictionaries again, we should see what state remains after the snapshot.
        var sut5 = CreateTestSystem(storage, timeProvider);
        var dictToKeep5 = CreateTestState(DictToKeepKey, sut5.Manager);
        var dictToRetire5 = CreateTestState(DictToRetireKey, sut5.Manager);

        await sut5.Lifecycle.OnStart();
        Assert.Equal(10, dictToKeep5["a"]);

        // The retired dictionary should now be empty because its state was purged during the compaction.
        // Note that this is a new version of dictToRetire, since the original was removed. Idea here is
        // that if we can register a new dictToRetire (with the same key), it means that the state itself
        // has been removed but also the data, otherwise a previous state would have had at least one
        // entry i.e. ["b", 1].

        Assert.Empty(dictToRetire5);

        // Note: The retirement of states has the nice benefit of being able to reuse state names.

        DurableDictionary<string, int> CreateTestState(string key, IJournaledStateManager manager) =>
            new(key, manager, new OrleansBinaryDurableDictionaryCommandCodec<string, int>(CodecProvider.GetCodec<string>(), CodecProvider.GetCodec<int>(), SessionPool));

        static async Task TriggerCompaction(IJournaledStateManager manager, DurableDictionary<string, int> dict)
        {
            for (var i = 0; i < 11; i++)
            {
                dict["a"] = i;
                await manager.WriteStateAsync(CancellationToken.None);
            }
        }
    }

    private sealed class StreamingOnlyStorage : IJournalStorage
    {
        public bool StreamingReadCalled { get; private set; }

        public bool IsCompactionRequested => false;

        public ValueTask ReadAsync(IJournalStorageConsumer consumer, CancellationToken cancellationToken)
        {
            StreamingReadCalled = true;
            consumer.Complete(metadata: null);
            return default;
        }

        public ValueTask ReplaceAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken) => default;

        public ValueTask AppendAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken) => default;

        public ValueTask DeleteAsync(CancellationToken cancellationToken) => default;
    }

    private OrleansBinaryDurableDictionaryCommandCodec<K, V> CreateDictionaryCodec<K, V>() where K : notnull =>
        new(CodecProvider.GetCodec<K>(), CodecProvider.GetCodec<V>(), SessionPool);

    private OrleansBinaryDurableValueCommandCodec<T> CreateValueCodec<T>() =>
        new(CodecProvider.GetCodec<T>(), SessionPool);

    private byte[] CreatePersistedValueBytes(string name, int value)
    {
        using var segment = new OrleansBinaryJournalBufferWriter();
        AppendDirectorySet(segment, name, new JournalStreamId(8));
        var codec = CreateValueCodec<int>();
        codec.WriteSet(value, segment.CreateJournalStreamWriter(new JournalStreamId(8)));

        using var committed = segment.GetBuffer();
        return committed.ToArray();
    }

    private byte[] CreateUnknownStreamBytes(JournalStreamId streamId, ReadOnlySpan<byte> payload)
    {
        using var segment = new OrleansBinaryJournalBufferWriter();
        using (var entry = segment.CreateJournalStreamWriter(streamId).BeginEntry())
        {
            entry.Writer.Write(payload);
            entry.Commit();
        }

        using var committed = segment.GetBuffer();
        return committed.ToArray();
    }

    private byte[] CreateNamedUnknownStreamBytes(string name, JournalStreamId streamId, ReadOnlySpan<byte> payload)
    {
        using var segment = new OrleansBinaryJournalBufferWriter();
        AppendDirectorySet(segment, name, streamId);
        using (var entry = segment.CreateJournalStreamWriter(streamId).BeginEntry())
        {
            entry.Writer.Write(payload);
            entry.Commit();
        }

        using var committed = segment.GetBuffer();
        return committed.ToArray();
    }

    private void AppendDirectorySet(OrleansBinaryJournalBufferWriter segment, string name, JournalStreamId streamId)
    {
        var codec = CreateDictionaryCodec<string, ulong>();
        codec.WriteSet(name, streamId.Value, segment.CreateJournalStreamWriter(new JournalStreamId(0)));
    }

    private static ArcBuffer CreateBuffer(ReadOnlySpan<byte> value)
    {
        using var writer = new ArcBufferWriter();
        writer.Write(value);
        return writer.ConsumeSlice(writer.Length);
    }

    private static int GetCommittedLength(JournalBufferWriter writer)
    {
        using var buffer = writer.GetBuffer();
        return buffer.Length;
    }

    private List<CapturedJournalEntry> ReadBinaryEntries(ReadOnlySpan<byte> bytes)
    {
        using var writer = new ArcBufferWriter();
        writer.Write(bytes);
        var reader = new JournalBufferReader(writer.Reader, isCompleted: true);
        var consumer = new CapturingJournalEntrySink();
        var context = JournalTestReplayContext.Create(OrleansBinaryJournalFormat.JournalFormatKey, consumer.Bind(ReadStreamIds(bytes)));
        ((IJournalFormat)new OrleansBinaryJournalFormat(SessionPool)).Replay(reader, context);
        Assert.Equal(0, reader.Length);

        return consumer.Entries;
    }

    private static List<JournalStreamId> ReadStreamIds(ReadOnlySpan<byte> bytes)
    {
        var streamIds = new List<JournalStreamId>();
        using var writer = new ArcBufferWriter();
        writer.Write(bytes);
        using var buffer = writer.PeekSlice(writer.Length);
        var offset = 0;

        while (offset < buffer.Length)
        {
            var remaining = buffer.UnsafeSlice(offset, buffer.Length - offset);
            if (!OrleansBinaryJournalReader.TryReadVersionAndLength(remaining, out var version, out var length, out var lengthPrefixLength))
            {
                throw new InvalidOperationException("The binary journal entry stream is malformed.");
            }

            var entryStart = offset + lengthPrefixLength;
            if (length == 0 || length > buffer.Length - entryStart)
            {
                throw new InvalidOperationException("The binary journal entry stream is malformed.");
            }

            var entry = buffer.UnsafeSlice(entryStart, checked((int)length));
            var streamIdValue = version == OrleansBinaryJournalReader.FramingVersion
                ? OrleansBinaryJournalReader.ReadUInt32LittleEndian(entry.UnsafeSlice(0, sizeof(uint)))
                : checked((uint)Reader.Create(entry, session: null!).ReadVarUInt64());

            var streamId = new JournalStreamId(streamIdValue);
            if (!streamIds.Contains(streamId))
            {
                streamIds.Add(streamId);
            }

            offset = checked(entryStart + (int)length);
        }

        return streamIds;
    }

    private readonly record struct CapturedJournalEntry(JournalStreamId StreamId, byte[] Payload);

    private static void AssertContainsRuntimeAndApplicationEntries(IReadOnlyCollection<CapturedJournalEntry> entries)
    {
        Assert.Contains(entries, entry => entry.StreamId.Value == 0 && entry.Payload.Length > 0);
        Assert.Contains(entries, entry => entry.StreamId.Value >= 8 && entry.Payload.Length > 0);
    }

    private sealed class CapturingJournalEntrySink
    {
        public List<CapturedJournalEntry> Entries { get; } = [];

        public (JournalStreamId StreamId, IJournaledState State)[] Bind(IEnumerable<JournalStreamId> streamIds)
        {
            return streamIds.Select(streamId => (streamId, (IJournaledState)new StreamSink(this, streamId))).ToArray();
        }

        private sealed class StreamSink(CapturingJournalEntrySink owner, JournalStreamId streamId) : IJournaledState
        {
            void IJournaledState.ReplayEntry(JournalEntry entry, JournalReplayContext context) =>
                owner.Entries.Add(new(streamId, entry.Reader.ToArray()));

            public void Reset(JournalStreamWriter writer) { }
            public void AppendEntries(JournalStreamWriter writer) { }
            public void AppendSnapshot(JournalStreamWriter writer) { }
            public IJournaledState DeepCopy() => throw new NotSupportedException();
        }
    }

    private sealed class DecodedPayloadOnlyJournalFormat : IJournalFormat
    {
        private readonly JournalStreamId _streamId;
        private readonly byte[] _payload;
        private readonly TrackingJournalFormat _writerFormat;

        public DecodedPayloadOnlyJournalFormat(JournalStreamId streamId, byte[] payload, SerializerSessionPool sessionPool)
        {
            _streamId = streamId;
            _payload = payload.ToArray();
            _writerFormat = new TrackingJournalFormat(sessionPool);
        }

        public List<TrackingJournalBufferWriter> Writers => _writerFormat.Writers;

        public string FormatKey => OrleansBinaryJournalFormat.JournalFormatKey;

        public string? MimeType => null;

        public JournalBufferWriter CreateWriter() => _writerFormat.CreateWriter();

        public void Replay(JournalBufferReader input, JournalReplayContext context)
        {
            if (input.Length == 0)
            {
                return;
            }

            var callbackPayload = _payload.ToArray();
            var state = context.ResolveState(_streamId);
            state.ReplayEntry(new JournalEntry(FormatKey, CodecTestHelpers.ReadBuffer(callbackPayload)), context);

            Array.Fill(callbackPayload, byte.MaxValue);
            input.Skip(input.Length);
        }
    }

    private sealed class TestPreservedJournalEntry(ReadOnlyMemory<byte> payload) : IPreservedJournalEntry
    {
        public ReadOnlyMemory<byte> Payload { get; } = payload.ToArray();

        public string FormatKey => OrleansBinaryJournalFormat.JournalFormatKey;

    }

    private sealed class NonConsumingJournalFormat : IJournalFormat
    {
        public string FormatKey => OrleansBinaryJournalFormat.JournalFormatKey;

        public string? MimeType => null;

        public JournalBufferWriter CreateWriter() => new OrleansBinaryJournalBufferWriter();

        public void Replay(JournalBufferReader input, JournalReplayContext context)
        {
        }
    }

    private sealed class TrackingJournalFormat(SerializerSessionPool sessionPool) : IJournalFormat
    {
        private readonly OrleansBinaryJournalFormat _inner = new(sessionPool);

        public List<TrackingJournalBufferWriter> Writers { get; } = [];

        public int ReadCount { get; private set; }

        public string FormatKey => OrleansBinaryJournalFormat.JournalFormatKey;

        public string? MimeType => null;

        public JournalBufferWriter CreateWriter()
        {
            var writer = new TrackingJournalBufferWriter();
            Writers.Add(writer);
            return writer;
        }

        public void Replay(JournalBufferReader input, JournalReplayContext context)
        {
            ReadCount++;
            ((IJournalFormat)_inner).Replay(input, context);
        }
    }

    private sealed class TrackingJournalBufferWriter : OrleansBinaryJournalBufferWriter
    {
        public List<uint> BeganEntryIds { get; } = [];

        protected override void StartEntry(JournalStreamId streamId)
        {
            BeganEntryIds.Add(streamId.Value);
            base.StartEntry(streamId);
        }

        protected override void WritePreservedEntry(JournalStreamId streamId, IPreservedJournalEntry entry)
        {
            BeganEntryIds.Add(streamId.Value);
            base.WritePreservedEntry(streamId, entry);
        }
    }

    private sealed class CapturingStorage : IJournalStorage
    {
        private readonly List<byte[]> _segments = [];

        public List<byte[]> Appends { get; } = [];

        public List<byte[]> Replaces { get; } = [];

        public bool ConcatenateReads { get; set; }

        public int DeleteCount { get; private set; }

        public Exception? NextAppendException { get; set; }

        public int ReadConsumeCount { get; private set; }

        public void ResetReadConsumeCount() => ReadConsumeCount = 0;

        public bool IsCompactionRequested { get; set; }

        public bool DelayReplace { get; set; }

        public TaskCompletionSource ReplaceStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowReplace { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask ReadAsync(IJournalStorageConsumer consumer, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(consumer);

            if (ConcatenateReads)
            {
                var totalLength = 0;
                foreach (var segment in _segments)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    totalLength += segment.Length;
                }

                if (totalLength > 0)
                {
                    var concatenated = new byte[totalLength];
                    var offset = 0;
                    foreach (var segment in _segments)
                    {
                        segment.CopyTo(concatenated.AsSpan(offset));
                        offset += segment.Length;
                    }

                    ReadConsumeCount++;
                    consumer.Read(concatenated, metadata: null, complete: true);
                }
                else
                {
                    consumer.Complete(metadata: null);
                }

                return default;
            }

            consumer.Read(GetSegments(), metadata: null, complete: true);
            return default;

            IEnumerable<ReadOnlyMemory<byte>> GetSegments()
            {
                foreach (var segment in _segments)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ReadConsumeCount++;
                    yield return segment;
                }
            }
        }

        public async ValueTask ReplaceAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DelayReplace)
            {
                ReplaceStarted.SetResult();
                await AllowReplace.Task.WaitAsync(cancellationToken);
            }

            var bytes = value.ToArray();
            Replaces.Add(bytes);
            _segments.Clear();
            _segments.Add(bytes);
        }

        public ValueTask AppendAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (NextAppendException is { } exception)
            {
                NextAppendException = null;
                return ValueTask.FromException(exception);
            }

            var bytes = value.ToArray();
            Appends.Add(bytes);
            _segments.Add(bytes);
            return default;
        }

        public ValueTask DeleteAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeleteCount++;
            _segments.Clear();
            return default;
        }
    }

    private sealed class RawReadStorage(byte[] bytes) : IJournalStorage
    {
        public bool IsCompactionRequested => false;

        public ValueTask ReadAsync(IJournalStorageConsumer consumer, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(consumer);
            cancellationToken.ThrowIfCancellationRequested();
            consumer.Read(bytes, metadata: null, complete: true);
            return default;
        }

        public ValueTask ReplaceAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken) => default;

        public ValueTask AppendAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken) => default;

        public ValueTask DeleteAsync(CancellationToken cancellationToken) => default;
    }

    private sealed class MutableReadStorage(byte[] bytes) : IJournalStorage
    {
        private readonly object _lock = new();
        private byte[] _bytes = bytes.ToArray();

        public byte[] Bytes
        {
            get
            {
                lock (_lock)
                {
                    return _bytes.ToArray();
                }
            }

            set
            {
                ArgumentNullException.ThrowIfNull(value);
                lock (_lock)
                {
                    _bytes = value.ToArray();
                }
            }
        }

        public List<byte[]> Replaces { get; } = [];

        public bool IsCompactionRequested { get; set; }

        public ValueTask ReadAsync(IJournalStorageConsumer consumer, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(consumer);
            cancellationToken.ThrowIfCancellationRequested();
            byte[] snapshot;
            lock (_lock)
            {
                snapshot = _bytes.ToArray();
            }

            consumer.Read(snapshot, metadata: null, complete: true);
            return default;
        }

        public ValueTask ReplaceAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = value.ToArray();
            Replaces.Add(bytes);
            lock (_lock)
            {
                _bytes = bytes.ToArray();
            }

            return default;
        }

        public ValueTask AppendAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var appendBytes = value.ToArray();
            lock (_lock)
            {
                _bytes = [.. _bytes, .. appendBytes];
            }

            return default;
        }

        public ValueTask DeleteAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Bytes = [];
            return default;
        }
    }

    private sealed class ThrowingReadStorage : IJournalStorage
    {
        public InvalidOperationException Exception { get; } = new("Storage read failed.");

        public bool IsCompactionRequested => false;

        public ValueTask ReadAsync(IJournalStorageConsumer consumer, CancellationToken cancellationToken)
            => ValueTask.FromException(Exception);

        public ValueTask ReplaceAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken) => default;

        public ValueTask AppendAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken) => default;

        public ValueTask DeleteAsync(CancellationToken cancellationToken) => default;
    }

    private sealed class ChunkedReadStorage(byte[] bytes, int chunkSize) : IJournalStorage
    {
        public int ReadConsumeCount { get; private set; }

        public bool IsCompactionRequested => false;

        public ValueTask ReadAsync(IJournalStorageConsumer consumer, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(consumer);

            consumer.Read(GetChunks(), metadata: null, complete: true);
            return default;

            IEnumerable<ReadOnlyMemory<byte>> GetChunks()
            {
                for (var offset = 0; offset < bytes.Length; offset += chunkSize)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var length = Math.Min(chunkSize, bytes.Length - offset);
                    ReadConsumeCount++;
                    yield return new ReadOnlyMemory<byte>(bytes, offset, length);
                }
            }
        }

        public ValueTask ReplaceAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken) => default;

        public ValueTask AppendAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken) => default;

        public ValueTask DeleteAsync(CancellationToken cancellationToken) => default;
    }

    private sealed class DelayedBorrowingStorage : IJournalStorage
    {
        public byte[]? AppendBytesAfterYield { get; private set; }

        public byte[]? ReplaceBytesAfterYield { get; private set; }

        public bool IsCompactionRequested { get; set; }

        public ValueTask ReadAsync(IJournalStorageConsumer consumer, CancellationToken cancellationToken)
        {
            consumer.Complete(metadata: null);
            return default;
        }

        public async ValueTask ReplaceAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            ReplaceBytesAfterYield = value.ToArray();
        }

        public async ValueTask AppendAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            AppendBytesAfterYield = value.ToArray();
        }

        public ValueTask DeleteAsync(CancellationToken cancellationToken) => default;
    }

    private sealed class ManualDirectWriteState : IJournaledState
    {
        private JournalStreamWriter _writer;
        private bool _entryOpen;

        public bool AppendEntriesObservedOpenEntry { get; private set; }

        public JournalEntryScope BeginEntry()
        {
            _entryOpen = true;
            return _writer.BeginEntry();
        }

        public void MarkEntryClosing() => _entryOpen = false;

        void IJournaledState.ReplayEntry(JournalEntry entry, JournalReplayContext context) { }

        public void Reset(JournalStreamWriter writer) => _writer = writer;

        public void AppendEntries(JournalStreamWriter writer)
        {
            AppendEntriesObservedOpenEntry |= _entryOpen;
        }

        public void AppendSnapshot(JournalStreamWriter writer) { }

        public IJournaledState DeepCopy() => throw new NotSupportedException();
    }

    private sealed class AlwaysWritingState : IJournaledState
    {
        public int AppendEntriesCount { get; private set; }

        void IJournaledState.ReplayEntry(JournalEntry entry, JournalReplayContext context) { }

        public void Reset(JournalStreamWriter writer) { }

        public void AppendEntries(JournalStreamWriter writer)
        {
            AppendEntriesCount++;
            using var entry = writer.BeginEntry();
            entry.Writer.GetSpan(1)[0] = 1;
            entry.Writer.Advance(1);
            entry.Commit();
        }

        public void AppendSnapshot(JournalStreamWriter writer) => AppendEntries(writer);

        public IJournaledState DeepCopy() => throw new NotSupportedException();
    }

    private sealed class ThrowingDictionarySetCodec<K, V> : IDurableDictionaryCommandCodec<K, V> where K : notnull
    {
        public void WriteSet(K key, V value, JournalStreamWriter writer)
        {
            using var entry = writer.BeginEntry();
            entry.Writer.GetSpan(1)[0] = 1;
            entry.Writer.Advance(1);
            throw new InvalidOperationException("Expected test exception.");
        }

        public void WriteRemove(K key, JournalStreamWriter writer) => throw new NotSupportedException();

        public void WriteClear(JournalStreamWriter writer) => throw new NotSupportedException();

        public void WriteSnapshot(IReadOnlyCollection<KeyValuePair<K, V>> items, JournalStreamWriter writer) => throw new NotSupportedException();

        public void Apply(JournalBufferReader input, IDurableDictionaryCommandHandler<K, V> consumer) => throw new NotSupportedException();
    }

    private sealed class ToggleThrowingDictionarySetCodec<K, V>(IDurableDictionaryCommandCodec<K, V> inner) : IDurableDictionaryCommandCodec<K, V>
        where K : notnull
    {
        public bool ThrowOnSet { get; set; }

        public void WriteSet(K key, V value, JournalStreamWriter writer)
        {
            if (!ThrowOnSet)
            {
                inner.WriteSet(key, value, writer);
                return;
            }

            using var entry = writer.BeginEntry();
            entry.Writer.GetSpan(1)[0] = 1;
            entry.Writer.Advance(1);
            throw new InvalidOperationException("Expected test exception.");
        }

        public void WriteRemove(K key, JournalStreamWriter writer) => inner.WriteRemove(key, writer);

        public void WriteClear(JournalStreamWriter writer) => inner.WriteClear(writer);

        public void WriteSnapshot(IReadOnlyCollection<KeyValuePair<K, V>> items, JournalStreamWriter writer) => inner.WriteSnapshot(items, writer);

        public void Apply(JournalBufferReader input, IDurableDictionaryCommandHandler<K, V> consumer) => inner.Apply(input, consumer);
    }
}
