using System.Buffers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Orleans.Serialization.Buffers;
using Xunit;

namespace Orleans.Journaling.Tests;

/// <summary>
/// Tests for LogManager, the core component of Orleans' journaling infrastructure.
/// 
/// LogManager coordinates multiple durable data structures (DurableDictionary, DurableList, etc.)
/// within a single grain, ensuring that all state changes are atomically journaled and can be
/// recovered together. It manages the lifecycle of state machines, handles persistence through
/// WriteStateAsync calls, and ensures consistent recovery after failures.
/// </summary>
[TestCategory("BVT")]
public class LogManagerTests : JournalingTestBase
{
    /// <summary>
    /// Tests the registration and basic operation of multiple state machines.
    /// Verifies that different types of durable collections can be registered
    /// with the manager and operate independently.
    /// </summary>
    [Fact]
    public async Task LogManager_RegisterStateMachine_Test()
    {
        // Arrange
        var sut = CreateTestSystem();
        var manager = sut.Manager;
        var codec = CodecProvider.GetCodec<int>();

        // Act - Register state machines
        var dictionary = new DurableDictionary<string, int>("dict1", manager, new OrleansBinaryDictionaryOperationCodec<string, int>(new OrleansLogValueCodec<string>(CodecProvider.GetCodec<string>(), SessionPool), new OrleansLogValueCodec<int>(codec, SessionPool)));
        var list = new DurableList<string>("list1", manager, new OrleansBinaryListOperationCodec<string>(new OrleansLogValueCodec<string>(CodecProvider.GetCodec<string>(), SessionPool)));
        var queue = new DurableQueue<int>("queue1", manager, new OrleansBinaryQueueOperationCodec<int>(new OrleansLogValueCodec<int>(codec, SessionPool)));
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
    public async Task LogManager_Initialize_UsesStreamingStorageRead()
    {
        var storage = new StreamingOnlyStorage();
        var sut = CreateTestSystem(storage: storage);

        await sut.Lifecycle.OnStart();

        Assert.True(storage.StreamingReadCalled);
    }

    [Fact]
    public async Task LogManager_Recovery_PreservesUnknownStateMachineEntry()
    {
        var storage = new VolatileLogStorage();
        using var segment = new LogSegmentBuffer();
        using (var entry = segment.CreateLogWriter(new LogStreamId(99)).BeginEntry())
        {
            entry.Writer.Write([1, 2, 3]);
            entry.Commit();
        }

        using var data = segment.GetCommittedBuffer();
        await storage.AppendAsync(data.AsReadOnlySequence(), CancellationToken.None);
        var sut = CreateTestSystem(storage: storage);

        await sut.Lifecycle.OnStart().WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task LogManager_UnknownStateMachineCompaction_PreservesDecodedPayloadThroughFormatWriter()
    {
        var physicalBytes = new byte[] { 0xF0, 0x0D, 0x99 };
        var decodedPayload = new byte[] { 1, 2, 3 };
        var storage = new CapturingStorage { IsCompactionRequested = true };
        using (var data = CreateBuffer(physicalBytes))
        {
            await storage.AppendAsync(data.AsReadOnlySequence(), CancellationToken.None);
        }

        var format = new DecodedPayloadOnlyLogFormat(new LogStreamId(99), decodedPayload);
        var sut = CreateTestSystem(storage: storage, logFormat: format);

        await sut.Lifecycle.OnStart().WaitAsync(TimeSpan.FromSeconds(10));
        await sut.Manager.WriteStateAsync(CancellationToken.None);

        var replacement = Assert.Single(storage.Replaces);
        Assert.NotEqual(physicalBytes, replacement);

        var entries = ReadBinaryEntries(replacement);
        var preserved = Assert.Single(entries, entry => entry.StreamId.Value == 99);
        Assert.Equal(decodedPayload, preserved.Payload);

        var writer = Assert.Single(format.Writers);
        Assert.Contains(99UL, writer.BeganEntryIds);
    }

    [Fact]
    public async Task LogManager_RetiredStateMachineCompaction_WritesPreservedPayloadThroughFormatOwnedEntry()
    {
        var storage = new CapturingStorage();
        var initial = CreateTestSystem(storage: storage);
        var dictionary = new DurableDictionary<string, int>("retired", initial.Manager, CreateDictionaryCodec<string, int>());

        await initial.Lifecycle.OnStart();
        dictionary.Add("key", 7);
        await initial.Manager.WriteStateAsync(CancellationToken.None);

        storage.IsCompactionRequested = true;
        var format = new TrackingLogFormat();
        var compacting = CreateTestSystem(storage: storage, logFormat: format);

        await compacting.Lifecycle.OnStart();
        await compacting.Manager.WriteStateAsync(CancellationToken.None);

        var writer = Assert.Single(format.Writers);
        Assert.Contains(writer.BeganEntryIds, id => id >= 8);
        Assert.Single(storage.Replaces);

        var recovered = CreateTestSystem(storage: storage);
        var recoveredDictionary = new DurableDictionary<string, int>("retired", recovered.Manager, CreateDictionaryCodec<string, int>());
        await recovered.Lifecycle.OnStart();

        Assert.Equal(7, recoveredDictionary["key"]);
    }

    [Fact]
    public async Task LogManager_DirectWrites_UseFormatOwnedCurrentSegmentWriter()
    {
        var storage = new CapturingStorage();
        var format = new TrackingLogFormat();
        var sut = CreateTestSystem(storage: storage, logFormat: format);
        var dictionary = new DurableDictionary<string, int>("dict", sut.Manager, CreateDictionaryCodec<string, int>());

        await sut.Lifecycle.OnStart();
        dictionary.Add("key", 1);
        await sut.Manager.WriteStateAsync(CancellationToken.None);

        var writer = Assert.Single(format.Writers);
        Assert.Single(storage.Appends);
        Assert.Empty(storage.Replaces);
        Assert.Contains(0UL, writer.CreatedLogWriterIds);
        Assert.Contains(writer.CreatedLogWriterIds, id => id >= 8);
        Assert.True(writer.GetCommittedBufferCount > 0);
        Assert.True(writer.ResetCount > 0);
    }

    [Fact]
    public async Task LogManager_AppendLogFlush_UsesFormatOwnedWriter()
    {
        var storage = new CapturingStorage();
        var format = new TrackingLogFormat();
        var sut = CreateTestSystem(storage: storage, logFormat: format);
        var value = new DurableValue<int>("value", sut.Manager, CreateValueCodec<int>());

        await sut.Lifecycle.OnStart();
        value.Value = 42;
        await sut.Manager.WriteStateAsync(CancellationToken.None);

        var writer = Assert.Single(format.Writers);
        Assert.Single(storage.Appends);
        Assert.Contains(writer.CreatedLogWriterIds, id => id >= 8);
        Assert.True(storage.Appends[0].Length > 0);
    }

    [Fact]
    public async Task LogManager_DefaultAppend_StoresBinaryFixed32Entries()
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
    public async Task LogManager_AppendBufferIsBorrowedUntilStorageCompletes()
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
    public async Task LogManager_ReplaceBufferIsBorrowedUntilStorageCompletes()
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
    public async Task LogManager_DefaultSnapshot_StoresBinaryFixed32Entries()
    {
        var storage = new CapturingStorage { IsCompactionRequested = true };
        var sut = CreateTestSystem(storage: storage);
        var dictionary = new DurableDictionary<string, int>("dict", sut.Manager, CreateDictionaryCodec<string, int>());

        await sut.Lifecycle.OnStart();
        dictionary.Add("key", 1);
        await sut.Manager.WriteStateAsync(CancellationToken.None);

        var replacement = Assert.Single(storage.Replaces);
        Assert.Empty(storage.Appends);
        AssertContainsRuntimeAndApplicationEntries(ReadBinaryEntries(replacement));
    }

    [Fact]
    public async Task LogManager_DeleteState_ReallocatesApplicationStreamsAboveInternalRange()
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
    public async Task LogManager_SnapshotFlush_ResetsPendingAppendData()
    {
        var storage = new CapturingStorage { IsCompactionRequested = true };
        var format = new TrackingLogFormat();
        var sut = CreateTestSystem(storage: storage, logFormat: format);
        var dictionary = new DurableDictionary<string, int>("dict", sut.Manager, CreateDictionaryCodec<string, int>());

        await sut.Lifecycle.OnStart();
        dictionary.Add("key", 1);
        await sut.Manager.WriteStateAsync(CancellationToken.None);

        var writer = Assert.Single(format.Writers);
        Assert.Empty(storage.Appends);
        Assert.Single(storage.Replaces);
        Assert.True(writer.ResetCount >= 2);

        var recovered = CreateTestSystem(storage: storage, logFormat: new TrackingLogFormat());
        var recoveredDictionary = new DurableDictionary<string, int>("dict", recovered.Manager, CreateDictionaryCodec<string, int>());
        await recovered.Lifecycle.OnStart();

        Assert.Equal(1, recoveredDictionary["key"]);
    }

    [Fact]
    public async Task LogManager_DirectWriteFailure_AbortsEntryBeforeMutation()
    {
        var storage = new CapturingStorage();
        var format = new TrackingLogFormat();
        var sut = CreateTestSystem(storage: storage, logFormat: format);
        var dictionary = new DurableDictionary<int, int>("dict", sut.Manager, new ThrowingDictionarySetCodec<int, int>());

        await sut.Lifecycle.OnStart();
        var writer = Assert.Single(format.Writers);
        var lengthBefore = writer.Length;

        Assert.Throws<InvalidOperationException>(() => dictionary.Add(1, 1));

        Assert.Empty(dictionary);
        Assert.Equal(lengthBefore, writer.Length);

        await sut.Manager.WriteStateAsync(CancellationToken.None);
    }

    [Fact]
    public async Task LogManager_WriteStateAsync_DoesNotObserveActiveEntry()
    {
        var storage = new CapturingStorage();
        var format = new TrackingLogFormat();
        var sut = CreateTestSystem(storage: storage, logFormat: format);
        var stateMachine = new ManualDirectWriteStateMachine();
        sut.Manager.RegisterStateMachine("manual", stateMachine);

        await sut.Lifecycle.OnStart();

        Task writeTask = Task.CompletedTask;
        var entry = stateMachine.BeginEntry();
        try
        {
            entry.Writer.Write([1, 2, 3]);
            writeTask = Task.Run(async () => await sut.Manager.WriteStateAsync(CancellationToken.None));
            Thread.Sleep(100);

            Assert.False(writeTask.IsCompleted);
            Assert.Empty(storage.Appends);

            entry.Commit();
        }
        finally
        {
            entry.Dispose();
        }

        await writeTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Single(storage.Appends);
    }

    /// <summary>
    /// Tests that all registered state machines are correctly recovered together.
    /// Verifies that the manager maintains consistency across multiple collections
    /// during recovery from persisted state.
    /// </summary>
    [Fact]
    public async Task LogManager_StateRecovery_Test()
    {
        // Arrange
        var sut = CreateTestSystem();

        // Create and populate state machines
        var dictionary = new DurableDictionary<string, int>("dict1", sut.Manager, new OrleansBinaryDictionaryOperationCodec<string, int>(new OrleansLogValueCodec<string>(CodecProvider.GetCodec<string>(), SessionPool), new OrleansLogValueCodec<int>(CodecProvider.GetCodec<int>(), SessionPool)));
        var list = new DurableList<string>("list1", sut.Manager, new OrleansBinaryListOperationCodec<string>(new OrleansLogValueCodec<string>(CodecProvider.GetCodec<string>(), SessionPool)));
        await sut.Lifecycle.OnStart();

        dictionary.Add("key1", 1);
        dictionary.Add("key2", 2);
        list.Add("item1");
        list.Add("item2");

        await sut.Manager.WriteStateAsync(CancellationToken.None);

        // Act - Create new manager with same storage
        var sut2 = CreateTestSystem(storage: sut.Storage);
        var recoveredDict = new DurableDictionary<string, int>("dict1", sut2.Manager, new OrleansBinaryDictionaryOperationCodec<string, int>(new OrleansLogValueCodec<string>(CodecProvider.GetCodec<string>(), SessionPool), new OrleansLogValueCodec<int>(CodecProvider.GetCodec<int>(), SessionPool)));
        var recoveredList = new DurableList<string>("list1", sut2.Manager, new OrleansBinaryListOperationCodec<string>(new OrleansLogValueCodec<string>(CodecProvider.GetCodec<string>(), SessionPool)));
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
    public async Task LogManager_Recovery_UsesSelectedLogFormatRead()
    {
        var storage = new CapturingStorage();
        var initial = CreateTestSystem(storage: storage);
        var value = new DurableValue<int>("value", initial.Manager, CreateValueCodec<int>());

        await initial.Lifecycle.OnStart();
        value.Value = 42;
        await initial.Manager.WriteStateAsync(CancellationToken.None);

        var format = new TrackingLogFormat();
        var recovered = CreateTestSystem(storage: storage, logFormat: format);
        var recoveredValue = new DurableValue<int>("value", recovered.Manager, CreateValueCodec<int>());

        await recovered.Lifecycle.OnStart();

        Assert.Equal(42, recoveredValue.Value);
        Assert.True(format.ReadCount > 0);
    }

    [Fact]
    public async Task LogManager_Recovery_ReadsConcatenatedLogData()
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
        Assert.Equal(1, storage.ReadCallbackCount);
    }

    [Fact]
    public async Task LogManager_Recovery_BuffersEntriesSplitAcrossStorageChunks()
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
        Assert.Equal(persistedBytes.Length, splitStorage.ReadCallbackCount);
    }

    [Fact]
    public async Task LogManager_Recovery_RejectsMalformedTrailingData()
    {
        byte[] bytes;
        using (var segment = new LogSegmentBuffer())
        {
            using (var entry = segment.CreateLogWriter(new LogStreamId(99)).BeginEntry())
            {
                entry.Writer.Write([1, 2, 3]);
                entry.Commit();
            }

            using var committed = segment.GetCommittedBuffer();
            bytes = [.. committed.ToArray(), 1, 2, 3];
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

        Assert.Contains("configured log format key 'orleans-binary'", exception.Message, StringComparison.Ordinal);
        var inner = Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.Contains("Malformed binary log entry stream", inner.Message, StringComparison.Ordinal);
        Assert.Contains("truncated fixed32 entry length prefix", inner.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LogManager_RecoveryRetry_ReplaysFixedStorage()
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
    public async Task LogManager_RecoveryRetry_PreservesUnknownStreamOnce()
    {
        var validBytes = CreateUnknownStreamBytes(new LogStreamId(99), [1, 2, 3]);
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
    public async Task LogManager_RecoveryRetry_RemovesStaleRetiredPlaceholder()
    {
        var storage = new MutableReadStorage([.. CreateNamedUnknownStreamBytes("stale", new LogStreamId(8), [1, 2, 3]), 1, 2, 3]);
        var sut = CreateTestSystem(storage: storage);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.Lifecycle.OnStart().WaitAsync(TimeSpan.FromSeconds(10)));

        storage.Bytes = [];
        await sut.Manager.WriteStateAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(sut.Manager.TryGetStateMachine("stale", out _));
        await sut.Lifecycle.OnStop(CancellationToken.None);
    }

    [Fact]
    public async Task LogManager_Recovery_DoesNotWrapStorageReadException()
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
    public async Task LogManager_MultipleWriteStates_Test()
    {
        // Arrange
        var sut = CreateTestSystem();
        var manager = sut.Manager;
        var dictionary = new DurableDictionary<string, int>("dict1", sut.Manager, new OrleansBinaryDictionaryOperationCodec<string, int>(new OrleansLogValueCodec<string>(CodecProvider.GetCodec<string>(), SessionPool), new OrleansLogValueCodec<int>(CodecProvider.GetCodec<int>(), SessionPool)));
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
        var recoveredDict = new DurableDictionary<string, int>("dict1", sut2.Manager, new OrleansBinaryDictionaryOperationCodec<string, int>(new OrleansLogValueCodec<string>(CodecProvider.GetCodec<string>(), SessionPool), new OrleansLogValueCodec<int>(CodecProvider.GetCodec<int>(), SessionPool)));
        await sut2.Lifecycle.OnStart();

        // Assert - Recovery should have final state
        Assert.Single(recoveredDict);
        Assert.Equal(10, recoveredDict["key1"]);
        Assert.False(recoveredDict.ContainsKey("key2"));
    }

    /// <summary>
    /// Tests managing multiple state machines of different types simultaneously.
    /// Verifies that the manager correctly handles diverse data structures
    /// (dictionaries with different key/value types, lists, values) in a single grain.
    /// </summary>
    [Fact]
    public async Task LogManager_MultipleStateMachines_Test()
    {
        // Arrange
        var sut = CreateTestSystem();
        var manager = sut.Manager;

        // Create multiple state machines with different types
        var intDict = new DurableDictionary<int, string>("intDict", manager, new OrleansBinaryDictionaryOperationCodec<int, string>(new OrleansLogValueCodec<int>(CodecProvider.GetCodec<int>(), SessionPool), new OrleansLogValueCodec<string>(CodecProvider.GetCodec<string>(), SessionPool)));
        var stringList = new DurableList<string>("stringList", manager, new OrleansBinaryListOperationCodec<string>(new OrleansLogValueCodec<string>(CodecProvider.GetCodec<string>(), SessionPool)));
        var personValue = new DurableValue<TestPerson>("personValue", manager, new OrleansBinaryValueOperationCodec<TestPerson>(new OrleansLogValueCodec<TestPerson>(CodecProvider.GetCodec<TestPerson>(), SessionPool)));
        await sut.Lifecycle.OnStart();

        // Act - Populate all state machines
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

        // Create new manager to verify recovery of multiple state machines
        var sut2 = CreateTestSystem(storage: sut.Storage);
        var recoveredIntDict = new DurableDictionary<int, string>("intDict", sut2.Manager, new OrleansBinaryDictionaryOperationCodec<int, string>(new OrleansLogValueCodec<int>(CodecProvider.GetCodec<int>(), SessionPool), new OrleansLogValueCodec<string>(CodecProvider.GetCodec<string>(), SessionPool)));
        var recoveredStringList = new DurableList<string>("stringList", sut2.Manager, new OrleansBinaryListOperationCodec<string>(new OrleansLogValueCodec<string>(CodecProvider.GetCodec<string>(), SessionPool)));
        var recoveredPersonValue = new DurableValue<TestPerson>("personValue", sut2.Manager, new OrleansBinaryValueOperationCodec<TestPerson>(new OrleansLogValueCodec<TestPerson>(CodecProvider.GetCodec<TestPerson>(), SessionPool)));
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
    /// Tests that multiple state machines can operate independently without interference.
    /// Verifies namespace isolation between different state machines with similar keys.
    /// </summary>
    [Fact]
    public async Task LogManager_Concurrency_Test()
    {
        // Arrange
        var sut = CreateTestSystem();
        var manager = sut.Manager;
        var dict1 = new DurableDictionary<string, int>("dict1", manager, new OrleansBinaryDictionaryOperationCodec<string, int>(new OrleansLogValueCodec<string>(CodecProvider.GetCodec<string>(), SessionPool), new OrleansLogValueCodec<int>(CodecProvider.GetCodec<int>(), SessionPool)));
        var dict2 = new DurableDictionary<string, int>("dict2", manager, new OrleansBinaryDictionaryOperationCodec<string, int>(new OrleansLogValueCodec<string>(CodecProvider.GetCodec<string>(), SessionPool), new OrleansLogValueCodec<int>(CodecProvider.GetCodec<int>(), SessionPool)));
        await sut.Lifecycle.OnStart();

        // Act - Simulate concurrent operations on different state machines
        dict1.Add("key1", 1);
        dict2.Add("key1", 100);

        dict1.Add("key2", 2);
        dict2.Add("key2", 200);

        await manager.WriteStateAsync(CancellationToken.None);

        // Assert - Both state machines should have their correct values
        Assert.Equal(2, dict1.Count);
        Assert.Equal(2, dict2.Count);

        Assert.Equal(1, dict1["key1"]);
        Assert.Equal(100, dict2["key1"]);

        Assert.Equal(2, dict1["key2"]);
        Assert.Equal(200, dict2["key2"]);
    }

    /// <summary>
    /// Stress test for state recovery with large amounts of data.
    /// Verifies that the journaling system can handle and recover state machines
    /// containing thousands of entries without data loss or corruption.
    /// </summary>
    [Fact]
    public async Task LogManager_LargeStateRecovery_Test()
    {
        // Arrange
        var sut = CreateTestSystem();
        var largeDict = new DurableDictionary<int, string>("largeDict", sut.Manager, new OrleansBinaryDictionaryOperationCodec<int, string>(new OrleansLogValueCodec<int>(CodecProvider.GetCodec<int>(), SessionPool), new OrleansLogValueCodec<string>(CodecProvider.GetCodec<string>(), SessionPool)));
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
        var recoveredDict = new DurableDictionary<int, string>("largeDict", sut2.Manager, new OrleansBinaryDictionaryOperationCodec<int, string>(new OrleansLogValueCodec<int>(CodecProvider.GetCodec<int>(), SessionPool), new OrleansLogValueCodec<string>(CodecProvider.GetCodec<string>(), SessionPool)));
        await sut2.Lifecycle.OnStart();

        // Assert - All items should be recovered
        Assert.Equal(itemCount, recoveredDict.Count);
        for (int i = 0; i < itemCount; i++)
        {
            Assert.Equal($"Value {i}", recoveredDict[i]);
        }
    }

    /// <summary>
    /// Tests the full lifecycle of a retired state machine. It is preserved and also reintroduced through an
    /// early compaction, but purged eventually after its grace period expires on later compactions.
    /// </summary>
    [Fact]
    public async Task LogManager_AutoRetiringStateMachines()
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
        var dictToKeep1 = CreateTestMachine(DictToKeepKey, sut1.Manager);
        var dictToRetire2 = CreateTestMachine(DictToRetireKey, sut1.Manager);

        await sut1.Lifecycle.OnStart();

        dictToKeep1.Add("a", 1);
        dictToRetire2.Add("b", 1);

        await sut1.Manager.WriteStateAsync(CancellationToken.None);

        // -------------- STEP 2 --------------

        // This time, we only register the dictionary we want to keep, this marks dictToRetire as retired.
        var sut2 = CreateTestSystem(storage, timeProvider);
        var dictToKeep2 = CreateTestMachine(DictToKeepKey, sut2.Manager);

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
        var dictToKeep3 = CreateTestMachine(DictToKeepKey, sut3.Manager);
        var dictToRetire3 = CreateTestMachine(DictToRetireKey, sut3.Manager);

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
        var dictToRetire3Recovered = CreateTestMachine(DictToRetireKey, sut3Recovered.Manager);
        await sut3Recovered.Lifecycle.OnStart();
        Assert.Equal(2, dictToRetire3Recovered["b"]);

        // -------------- STEP 4 --------------

        // Because of re-registration is step 3 (to test it was not purged), this means dictToRetire has been removed from the tracker.
        // Again as in step 2, we only register the dictionary we want to keep, this marks dictToRetire as retired.
        var sut4 = CreateTestSystem(storage, timeProvider);
        var dictToKeep4 = CreateTestMachine(DictToKeepKey, sut4.Manager);

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
        var dictToKeep5 = CreateTestMachine(DictToKeepKey, sut5.Manager);
        var dictToRetire5 = CreateTestMachine(DictToRetireKey, sut5.Manager);

        await sut5.Lifecycle.OnStart();
        Assert.Equal(10, dictToKeep5["a"]);

        // The retired dictionary should now be empty because its state was purged during the compaction.
        // Note that this is a new version of dictToRetire, since the original was removed. Idea here is
        // that if we can register a new dictToRetire (with the same key), it means that the machine itself
        // has been removed but also the data, otherwise a previous machine would have had at least one
        // entry i.e. ["b", 1].

        Assert.Empty(dictToRetire5);

        // Note: The retirement of state machines has the nice benefit of being able to reuse machine names.

        DurableDictionary<string, int> CreateTestMachine(string key, ILogManager manager) =>
            new(key, manager, new OrleansBinaryDictionaryOperationCodec<string, int>(new OrleansLogValueCodec<string>(CodecProvider.GetCodec<string>(), SessionPool), new OrleansLogValueCodec<int>(CodecProvider.GetCodec<int>(), SessionPool)));

        static async Task TriggerCompaction(ILogManager manager, DurableDictionary<string, int> dict)
        {
            for (var i = 0; i < 11; i++)
            {
                dict["a"] = i;
                await manager.WriteStateAsync(CancellationToken.None);
            }
        }
    }

    private sealed class StreamingOnlyStorage : ILogStorage
    {
        public bool StreamingReadCalled { get; private set; }

        public bool IsCompactionRequested => false;

        public ValueTask ReadAsync(ArcBufferWriter buffer, Action<ArcBufferReader> consume, CancellationToken cancellationToken)
        {
            StreamingReadCalled = true;
            return default;
        }

        public ValueTask ReplaceAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken) => default;

        public ValueTask AppendAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken) => default;

        public ValueTask DeleteAsync(CancellationToken cancellationToken) => default;
    }

    private OrleansBinaryDictionaryOperationCodec<K, V> CreateDictionaryCodec<K, V>() where K : notnull =>
        new(new OrleansLogValueCodec<K>(CodecProvider.GetCodec<K>(), SessionPool), new OrleansLogValueCodec<V>(CodecProvider.GetCodec<V>(), SessionPool));

    private OrleansBinaryValueOperationCodec<T> CreateValueCodec<T>() =>
        new(new OrleansLogValueCodec<T>(CodecProvider.GetCodec<T>(), SessionPool));

    private byte[] CreatePersistedValueBytes(string name, int value)
    {
        using var segment = new LogSegmentBuffer();
        AppendDirectorySet(segment, name, new LogStreamId(8));
        var codec = CreateValueCodec<int>();
        using (var entry = segment.CreateLogWriter(new LogStreamId(8)).BeginEntry())
        {
            codec.WriteSet(value, entry.Writer);
            entry.Commit();
        }

        using var committed = segment.GetCommittedBuffer();
        return committed.ToArray();
    }

    private byte[] CreateUnknownStreamBytes(LogStreamId streamId, ReadOnlySpan<byte> payload)
    {
        using var segment = new LogSegmentBuffer();
        using (var entry = segment.CreateLogWriter(streamId).BeginEntry())
        {
            entry.Writer.Write(payload);
            entry.Commit();
        }

        using var committed = segment.GetCommittedBuffer();
        return committed.ToArray();
    }

    private byte[] CreateNamedUnknownStreamBytes(string name, LogStreamId streamId, ReadOnlySpan<byte> payload)
    {
        using var segment = new LogSegmentBuffer();
        AppendDirectorySet(segment, name, streamId);
        using (var entry = segment.CreateLogWriter(streamId).BeginEntry())
        {
            entry.Writer.Write(payload);
            entry.Commit();
        }

        using var committed = segment.GetCommittedBuffer();
        return committed.ToArray();
    }

    private void AppendDirectorySet(LogSegmentBuffer segment, string name, LogStreamId streamId)
    {
        var codec = CreateDictionaryCodec<string, ulong>();
        using var entry = segment.CreateLogWriter(new LogStreamId(0)).BeginEntry();
        codec.WriteSet(name, streamId.Value, entry.Writer);
        entry.Commit();
    }

    private static ArcBuffer CreateBuffer(ReadOnlySpan<byte> value)
    {
        using var writer = new ArcBufferWriter();
        writer.Write(value);
        return writer.ConsumeSlice(writer.Length);
    }

    private static List<CapturedLogEntry> ReadBinaryEntries(ReadOnlySpan<byte> bytes)
    {
        using var writer = new ArcBufferWriter();
        writer.Write(bytes);
        var reader = new ArcBufferReader(writer);
        var consumer = new CapturingLogEntrySink();
        while (((ILogFormat)OrleansBinaryLogFormat.Instance).TryRead(reader, consumer, isCompleted: true))
        {
        }

        return consumer.Entries;
    }

    private readonly record struct CapturedLogEntry(LogStreamId StreamId, byte[] Payload);

    private static void AssertContainsRuntimeAndApplicationEntries(IReadOnlyCollection<CapturedLogEntry> entries)
    {
        Assert.Contains(entries, entry => entry.StreamId.Value == 0 && entry.Payload.Length > 0);
        Assert.Contains(entries, entry => entry.StreamId.Value >= 8 && entry.Payload.Length > 0);
    }

    private sealed class CapturingLogEntrySink : ILogStreamStateMachineResolver, IDurableStateMachine
    {
        private LogStreamId _streamId;

        public List<CapturedLogEntry> Entries { get; } = [];

        object IDurableStateMachine.OperationCodec => this;

        public IDurableStateMachine ResolveStateMachine(LogStreamId streamId)
        {
            _streamId = streamId;
            return this;
        }

        public void Apply(ReadOnlySequence<byte> payload) => Entries.Add(new(_streamId, payload.ToArray()));

        public void Reset(LogWriter storage) { }
        public void AppendEntries(LogWriter writer) { }
        public void AppendSnapshot(LogWriter writer) { }
        public IDurableStateMachine DeepCopy() => throw new NotSupportedException();
    }

    private sealed class DecodedPayloadOnlyLogFormat : ILogFormat
    {
        private readonly LogStreamId _streamId;
        private readonly byte[] _payload;
        private readonly TrackingLogFormat _writerFormat = new();

        public DecodedPayloadOnlyLogFormat(LogStreamId streamId, byte[] payload)
        {
            _streamId = streamId;
            _payload = payload.ToArray();
        }

        public List<TrackingLogSegmentWriter> Writers => _writerFormat.Writers;

        public ILogSegmentWriter CreateWriter() => _writerFormat.CreateWriter();

        public bool TryRead(ArcBufferReader input, ILogStreamStateMachineResolver resolver, bool isCompleted)
        {
            if (input.Length == 0)
            {
                return false;
            }

            var callbackPayload = _payload.ToArray();
            var stateMachine = resolver.ResolveStateMachine(_streamId);
            if (stateMachine is IFormattedLogEntryBuffer formattedEntryBuffer)
            {
                formattedEntryBuffer.AddFormattedEntry(new TestFormattedLogEntry(callbackPayload));
            }
            else
            {
                stateMachine.Apply(new ReadOnlySequence<byte>(callbackPayload));
            }

            Array.Fill(callbackPayload, byte.MaxValue);
            input.Skip(input.Length);
            return true;
        }
    }

    private sealed class TestFormattedLogEntry(ReadOnlyMemory<byte> payload) : IFormattedLogEntry
    {
        public ReadOnlyMemory<byte> Payload { get; } = payload.ToArray();
    }

    private sealed class TrackingLogFormat : ILogFormat
    {
        public List<TrackingLogSegmentWriter> Writers { get; } = [];

        public int ReadCount { get; private set; }

        public ILogSegmentWriter CreateWriter()
        {
            var writer = new TrackingLogSegmentWriter();
            Writers.Add(writer);
            return writer;
        }

        public bool TryRead(ArcBufferReader input, ILogStreamStateMachineResolver consumer, bool isCompleted)
        {
            ReadCount++;
            return ((ILogFormat)OrleansBinaryLogFormat.Instance).TryRead(input, consumer, isCompleted);
        }
    }

    private sealed class TrackingLogSegmentWriter : ILogSegmentWriter
    {
        private readonly LogSegmentBuffer _inner = new();

        public List<ulong> CreatedLogWriterIds { get; } = [];

        public List<ulong> BeganEntryIds { get; } = [];

        public int GetCommittedBufferCount { get; private set; }

        public int ResetCount { get; private set; }

        public long Length => _inner.Length;

        public LogWriter CreateLogWriter(LogStreamId streamId)
        {
            CreatedLogWriterIds.Add(streamId.Value);
            return new(streamId, new TrackingLogWriterTarget(this));
        }

        public ArcBuffer GetCommittedBuffer()
        {
            GetCommittedBufferCount++;
            return _inner.GetCommittedBuffer();
        }

        public void Reset()
        {
            ResetCount++;
            _inner.Reset();
        }

        public void Dispose() => _inner.Dispose();

        private sealed class TrackingLogWriterTarget(TrackingLogSegmentWriter owner) : ILogWriterTarget
        {
            public LogEntryWriter BeginEntry(LogStreamId streamId, ILogEntryWriterCompletion? completion)
            {
                owner.BeganEntryIds.Add(streamId.Value);
                return owner._inner.BeginEntry(streamId, completion);
            }

            public void AppendFormattedEntry(LogStreamId streamId, IFormattedLogEntry entry)
            {
                owner.BeganEntryIds.Add(streamId.Value);
                using var logEntry = new LogEntry(owner._inner.BeginEntry(streamId));
                logEntry.Writer.Write(entry.Payload.Span);
                logEntry.Commit();
            }

            public bool TryAppendFormattedEntry(LogStreamId streamId, IFormattedLogEntry entry)
            {
                AppendFormattedEntry(streamId, entry);
                return true;
            }
        }
    }

    private sealed class CapturingStorage : ILogStorage
    {
        private readonly List<byte[]> _segments = [];

        public List<byte[]> Appends { get; } = [];

        public List<byte[]> Replaces { get; } = [];

        public bool ConcatenateReads { get; set; }

        public int ReadCallbackCount { get; private set; }

        public bool IsCompactionRequested { get; set; }

        public ValueTask ReadAsync(ArcBufferWriter buffer, Action<ArcBufferReader> consume, CancellationToken cancellationToken)
        {
            if (ConcatenateReads)
            {
                foreach (var segment in _segments)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    buffer.Write(segment);
                }

                if (_segments.Count > 0)
                {
                    ReadCallbackCount++;
                    consume(new ArcBufferReader(buffer));
                }

                return default;
            }

            foreach (var segment in _segments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReadCallbackCount++;
                buffer.Write(segment);
                consume(new ArcBufferReader(buffer));
            }

            return default;
        }

        public ValueTask ReplaceAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = value.ToArray();
            Replaces.Add(bytes);
            _segments.Clear();
            _segments.Add(bytes);
            return default;
        }

        public ValueTask AppendAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = value.ToArray();
            Appends.Add(bytes);
            _segments.Add(bytes);
            return default;
        }

        public ValueTask DeleteAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _segments.Clear();
            return default;
        }
    }

    private sealed class RawReadStorage(byte[] bytes) : ILogStorage
    {
        public bool IsCompactionRequested => false;

        public ValueTask ReadAsync(ArcBufferWriter buffer, Action<ArcBufferReader> consume, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            buffer.Write(bytes);
            consume(new ArcBufferReader(buffer));
            return default;
        }

        public ValueTask ReplaceAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken) => default;

        public ValueTask AppendAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken) => default;

        public ValueTask DeleteAsync(CancellationToken cancellationToken) => default;
    }

    private sealed class MutableReadStorage(byte[] bytes) : ILogStorage
    {
        public byte[] Bytes { get; set; } = bytes;

        public List<byte[]> Replaces { get; } = [];

        public bool IsCompactionRequested { get; set; }

        public ValueTask ReadAsync(ArcBufferWriter buffer, Action<ArcBufferReader> consume, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Bytes.Length > 0)
            {
                buffer.Write(Bytes);
                consume(new ArcBufferReader(buffer));
            }

            return default;
        }

        public ValueTask ReplaceAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = value.ToArray();
            Replaces.Add(bytes);
            Bytes = bytes;
            return default;
        }

        public ValueTask AppendAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Bytes = [.. Bytes, .. value.ToArray()];
            return default;
        }

        public ValueTask DeleteAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Bytes = [];
            return default;
        }
    }

    private sealed class ThrowingReadStorage : ILogStorage
    {
        public InvalidOperationException Exception { get; } = new("Storage read failed.");

        public bool IsCompactionRequested => false;

        public ValueTask ReadAsync(ArcBufferWriter buffer, Action<ArcBufferReader> consume, CancellationToken cancellationToken)
            => ValueTask.FromException(Exception);

        public ValueTask ReplaceAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken) => default;

        public ValueTask AppendAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken) => default;

        public ValueTask DeleteAsync(CancellationToken cancellationToken) => default;
    }

    private sealed class ChunkedReadStorage(byte[] bytes, int chunkSize) : ILogStorage
    {
        public int ReadCallbackCount { get; private set; }

        public bool IsCompactionRequested => false;

        public ValueTask ReadAsync(ArcBufferWriter buffer, Action<ArcBufferReader> consume, CancellationToken cancellationToken)
        {
            for (var offset = 0; offset < bytes.Length; offset += chunkSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var length = Math.Min(chunkSize, bytes.Length - offset);
                ReadCallbackCount++;
                buffer.Write(bytes.AsSpan(offset, length));
                consume(new ArcBufferReader(buffer));
            }

            return default;
        }

        public ValueTask ReplaceAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken) => default;

        public ValueTask AppendAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken) => default;

        public ValueTask DeleteAsync(CancellationToken cancellationToken) => default;
    }

    private sealed class DelayedBorrowingStorage : ILogStorage
    {
        public byte[]? AppendBytesAfterYield { get; private set; }

        public byte[]? ReplaceBytesAfterYield { get; private set; }

        public bool IsCompactionRequested { get; set; }

        public ValueTask ReadAsync(ArcBufferWriter buffer, Action<ArcBufferReader> consume, CancellationToken cancellationToken) => default;

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

    private sealed class ManualDirectWriteStateMachine : IDurableStateMachine
    {
        private LogWriter _writer;

        public LogEntry BeginEntry() => _writer.BeginEntry();

        public object OperationCodec => this;

        public void Reset(LogWriter storage) => _writer = storage;

        public void Apply(ReadOnlySequence<byte> logEntry) { }

        public void AppendEntries(LogWriter writer) { }

        public void AppendSnapshot(LogWriter writer) { }

        public IDurableStateMachine DeepCopy() => throw new NotSupportedException();
    }

    private sealed class ThrowingDictionarySetCodec<K, V> : IDurableDictionaryOperationCodec<K, V> where K : notnull
    {
        public void WriteSet(K key, V value, IBufferWriter<byte> output)
        {
            output.GetSpan(1)[0] = 1;
            output.Advance(1);
            throw new InvalidOperationException("Expected test exception.");
        }

        public void WriteRemove(K key, IBufferWriter<byte> output) => throw new NotSupportedException();

        public void WriteClear(IBufferWriter<byte> output) => throw new NotSupportedException();

        public void WriteSnapshot(IReadOnlyCollection<KeyValuePair<K, V>> items, IBufferWriter<byte> output) => throw new NotSupportedException();

        public void Apply(ReadOnlySequence<byte> input, IDurableDictionaryOperationHandler<K, V> consumer) => throw new NotSupportedException();
    }
}
