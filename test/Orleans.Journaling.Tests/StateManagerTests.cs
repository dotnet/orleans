using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Buffers.Adaptors;
using Orleans.Serialization.Session;
using Orleans.Storage;
using NSubstitute;
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
[TestSuite("BVT")]
[TestProvider("None")]
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
        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);

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

        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);

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
            sut.Lifecycle.OnStart(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        Assert.NotNull(exception.InnerException);
        Assert.Contains("did not read the completed journal data", exception.InnerException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StateManager_WriteOperations_RequireInitialization()
    {
        var sut = CreateTestSystem();

        var writeException = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.Manager.WriteStateAsync(CancellationToken.None).AsTask());
        var deleteException = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.Manager.DeleteStateAsync(CancellationToken.None).AsTask());

        Assert.Contains("not been initialized", writeException.Message, StringComparison.Ordinal);
        Assert.Contains("not been initialized", deleteException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StateManager_WriteOperations_RequireSuccessfulInitialization()
    {
        var storage = new CapturingStorage
        {
            NextReadException = new IOException("Expected recovery failure.")
        };
        var sut = CreateTestSystem(storage: storage);

        await Assert.ThrowsAsync<IOException>(() => sut.Lifecycle.OnStart(TestContext.Current.CancellationToken));

        var writeException = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.Manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask());
        var deleteException = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.Manager.DeleteStateAsync(TestContext.Current.CancellationToken).AsTask());

        Assert.Contains("fenced", writeException.Message, StringComparison.Ordinal);
        Assert.Contains("fenced", deleteException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StateManager_WriteOperations_ObserveShutdown()
    {
        var sut = CreateTestSystem();
        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        await sut.Lifecycle.OnStop(TestContext.Current.CancellationToken);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sut.Manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sut.Manager.DeleteStateAsync(TestContext.Current.CancellationToken).AsTask());
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

        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
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

        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
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

        await initial.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        dictionary.Add("key", 7);
        await initial.Manager.WriteStateAsync(CancellationToken.None);

        storage.IsCompactionRequested = true;
        var format = new TrackingJournalFormat(SessionPool);
        var compacting = CreateTestSystem(storage: storage, journalFormat: format);

        await compacting.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        await compacting.Manager.WriteStateAsync(CancellationToken.None);

        Assert.Contains(format.Writers, writer => writer.BeganEntryIds.Any(id => id >= 8));
        Assert.Single(storage.Replaces);

        var recovered = CreateTestSystem(storage: storage);
        var recoveredDictionary = new DurableDictionary<string, int>("retired", recovered.Manager, CreateDictionaryCodec<string, int>());
        await recovered.Lifecycle.OnStart(TestContext.Current.CancellationToken);

        Assert.Equal(7, recoveredDictionary["key"]);
    }

    [Fact]
    public async Task StateManager_DirectWrites_UseFormatOwnedCurrentSegmentWriter()
    {
        var storage = new CapturingStorage();
        var format = new TrackingJournalFormat(SessionPool);
        var sut = CreateTestSystem(storage: storage, journalFormat: format);
        var dictionary = new DurableDictionary<string, int>("dict", sut.Manager, CreateDictionaryCodec<string, int>());

        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);
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

        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);
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

        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);
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

        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);
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

        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);
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

        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        dictionary.Add("key", 1);
        await sut.Manager.WriteStateAsync(CancellationToken.None);

        var replacement = Assert.Single(storage.Replaces);
        Assert.Empty(storage.Appends);
        AssertContainsRuntimeAndApplicationEntries(ReadBinaryEntries(replacement));
    }

    [Fact]
    public async Task StateManager_StorageOperationBytesMetric_RecordsWritePayloadSizes()
    {
        var observedBytes = new ConcurrentBag<MetricMeasurement<long>>();
        using var listener = CreateMetricListener("orleans-journaling-storage-operation-bytes", observedBytes);
        var storage = new CapturingStorage { IsCompactionRequested = true };
        var sut = CreateTestSystem(storage: storage);
        var value = new DurableValue<int>("value", sut.Manager, CreateValueCodec<int>());

        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        value.Value = 42;
        await sut.Manager.WriteStateAsync(CancellationToken.None);

        var replacement = Assert.Single(storage.Replaces);
        Assert.Empty(storage.Appends);
        Assert.Contains(observedBytes, measurement => measurement.Operation == "replace" && measurement.Status == "ok" && measurement.Value == replacement.Length);
    }

    [Fact]
    public void JournalingInstruments_StorageOperationBytesMetric_SkipsZeroByteAndFailedOperations()
    {
        var observedBytes = new ConcurrentBag<MetricMeasurement<long>>();
        using var listener = CreateMetricListener("orleans-journaling-storage-operation-bytes", observedBytes);
        var operation = $"test-{Guid.NewGuid():N}";
        var instruments = JournalingInstruments.CreateForDirectConstruction();

        instruments.OnStorageOperation(operation, TimeSpan.Zero, bytes: 0, succeeded: true);
        instruments.OnStorageOperation(operation, TimeSpan.Zero, bytes: 1, succeeded: false);

        Assert.DoesNotContain(observedBytes, measurement => measurement.Operation == operation);
    }

    [Fact]
    public async Task StateManager_StorageOperationQueueDurationMetric_RecordsWaitBehindPreviousAppend()
    {
        var observedDurations = new ConcurrentBag<MetricMeasurement<double>>();
        using var listener = CreateMetricListener("orleans-journaling-storage-operation-queue-duration", observedDurations);
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var storage = new BlockingAppendStorage();
        var sut = CreateTestSystem(storage: storage, provider: timeProvider);
        var state = new AlwaysWritingState();
        sut.Manager.RegisterState("state", state);

        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        var firstWrite = sut.Manager.WriteStateAsync(CancellationToken.None).AsTask();
        await storage.FirstAppendStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var secondWrite = sut.Manager.WriteStateAsync(CancellationToken.None).AsTask();
        timeProvider.Advance(TimeSpan.FromMilliseconds(250));
        storage.AllowFirstAppend.SetResult();

        await Task.WhenAll(firstWrite, secondWrite).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(2, storage.Appends.Count);
        Assert.Contains(observedDurations, measurement => measurement.Operation == "append" && measurement.Status == "ok" && measurement.Value >= 250);
    }

    [Fact]
    public async Task StateManager_DeleteState_ReallocatesApplicationStreamsAboveInternalRange()
    {
        var storage = new CapturingStorage();
        var sut = CreateTestSystem(storage: storage);
        var dictionary = new DurableDictionary<string, int>("dict", sut.Manager, CreateDictionaryCodec<string, int>());

        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);
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
        await recovered.Lifecycle.OnStart(TestContext.Current.CancellationToken);

        Assert.Single(recoveredDictionary);
        Assert.Equal(2, recoveredDictionary["after"]);
    }

    [Fact]
    public async Task StateManager_DeleteState_ClearsDurableValueDirtyFlag()
    {
        var storage = new CapturingStorage();
        var sut = CreateTestSystem(storage: storage);
        var value = new DurableValue<int>("value", sut.Manager, CreateValueCodec<int>());

        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);
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

        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        dictionary.Add("key", 1);
        await sut.Manager.WriteStateAsync(CancellationToken.None);

        Assert.Empty(storage.Appends);
        Assert.Single(storage.Replaces);
        Assert.Equal(0, sut.Manager.PendingWriteByteCount);

        var recovered = CreateTestSystem(storage: storage, journalFormat: new TrackingJournalFormat(SessionPool));
        var recoveredDictionary = new DurableDictionary<string, int>("dict", recovered.Manager, CreateDictionaryCodec<string, int>());
        await recovered.Lifecycle.OnStart(TestContext.Current.CancellationToken);

        Assert.Equal(1, recoveredDictionary["key"]);
    }

    [Fact]
    public async Task StateManager_SnapshotFailure_FencesMixedPendingMutations()
    {
        var storage = new CapturingStorage();
        var sut = CreateTestSystem(storage: storage);
        var dictionary = new DurableDictionary<string, int>("dict", sut.Manager, CreateDictionaryCodec<string, int>());
        var value = new DurableValue<int>("value", sut.Manager, CreateValueCodec<int>());
        var notifications = new AlwaysWritingState();
        sut.Manager.RegisterState("notifications", notifications);

        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        dictionary.Add("persisted", 1);
        value.Value = 1;
        await sut.Manager.WriteStateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, notifications.WriteCompletedCount);
        var baselineRecoverableBytes = storage.RecoverableBytes;
        var notificationCountBeforeFailure = notifications.WriteCompletedCount;
        storage.OperationLog.Clear();

        storage.IsCompactionRequested = true;
        dictionary.Add("pending", 2);
        value.Value = 2;
        var pendingBytes = sut.Manager.PendingWriteByteCount;
        var expected = new IOException("Expected replace failure.");
        storage.NextReplaceException = expected;

        var exception = await Assert.ThrowsAsync<IOException>(
            () => sut.Manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask());

        Assert.Same(expected, exception);
        Assert.Equal(["replace-failed"], storage.OperationLog);
        Assert.Equal(baselineRecoverableBytes, storage.RecoverableBytes);
        var failedAttemptBytes = Assert.Single(storage.FailedReplaceAttempts);
        Assert.NotEmpty(failedAttemptBytes);
        Assert.Single(storage.Appends);
        Assert.Empty(storage.Replaces);
        Assert.Equal(1, notifications.WriteCompletedCount);
        Assert.Equal(notificationCountBeforeFailure, notifications.WriteCompletedCount);
        Assert.Equal(pendingBytes, sut.Manager.PendingWriteByteCount);
        Assert.Equal(2, dictionary["pending"]);
        Assert.Equal(2, value.Value);

        var fenced = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.Manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask());
        Assert.Same(expected, fenced.InnerException);
        Assert.Empty(storage.Replaces);
        Assert.Equal(1, storage.ReplaceAttemptCount);
        Assert.Equal(notificationCountBeforeFailure, notifications.WriteCompletedCount);

        var recovered = CreateTestSystem(storage: storage);
        var recoveredDictionary = new DurableDictionary<string, int>("dict", recovered.Manager, CreateDictionaryCodec<string, int>());
        var recoveredValue = new DurableValue<int>("value", recovered.Manager, CreateValueCodec<int>());
        await recovered.Lifecycle.OnStart(TestContext.Current.CancellationToken);

        Assert.Equal(1, recoveredDictionary["persisted"]);
        Assert.False(recoveredDictionary.ContainsKey("pending"));
        Assert.Equal(1, recoveredValue.Value);
    }

    [Fact]
    public async Task StateManager_DirectWriteFailure_AbortsEntryBeforeMutation()
    {
        var storage = new CapturingStorage();
        var format = new TrackingJournalFormat(SessionPool);
        var sut = CreateTestSystem(storage: storage, journalFormat: format);
        var dictionary = new DurableDictionary<int, int>("dict", sut.Manager, new ThrowingDictionarySetCodec<int, int>());

        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);
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

        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        codec.ThrowOnSet = true;
        Assert.Throws<InvalidOperationException>(() => dictionary.Add(1, 1));

        codec.ThrowOnSet = false;
        dictionary.Add(1, 1);
        await sut.Manager.WriteStateAsync(CancellationToken.None);

        var recovered = CreateTestSystem(storage: storage, journalFormat: new TrackingJournalFormat(SessionPool));
        var recoveredDictionary = new DurableDictionary<int, int>("dict", recovered.Manager, CreateDictionaryCodec<int, int>());
        await recovered.Lifecycle.OnStart(TestContext.Current.CancellationToken);

        Assert.Single(recoveredDictionary);
        Assert.Equal(1, recoveredDictionary[1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StateManager_WriteFailurePreservesInterleavedStateAndFencesAllCallers(bool conflict)
    {
        var storage = new CapturingStorage();
        var sut = CreateTestSystem(storage: storage);
        var dictionary = new DurableDictionary<string, int>("dict", sut.Manager, CreateDictionaryCodec<string, int>());
        var value = new DurableValue<int>("value", sut.Manager, CreateValueCodec<int>());
        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        dictionary.Add("persisted", 1);
        value.Value = 1;
        await sut.Manager.WriteStateAsync(TestContext.Current.CancellationToken);
        storage.ResetReadConsumeCount();

        Exception expected = conflict
            ? new InconsistentStateException("Expected storage conflict.")
            : new IOException("Expected storage failure.");
        storage.NextAppendException = expected;
        storage.BlockNextAppend = true;
        value.Value = 2;
        var firstWrite = sut.Manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        await storage.BlockedAppendStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        dictionary.Add("interleaved", 3);
        var queuedWrite = sut.Manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        var queuedDelete = sut.Manager.DeleteStateAsync(TestContext.Current.CancellationToken).AsTask();
        var queuedInitialize = sut.Manager.InitializeAsync(TestContext.Current.CancellationToken).AsTask();
        storage.ReleaseAppend.SetResult();

        foreach (var task in new[] { firstWrite, queuedWrite, queuedDelete, queuedInitialize })
        {
            var exception = await Record.ExceptionAsync(
                () => task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.Same(expected, exception);
        }

        Assert.Equal(2, value.Value);
        Assert.Equal(3, dictionary["interleaved"]);
        Assert.Equal(0, storage.ReadConsumeCount);
        Assert.Single(storage.Appends);
        Assert.Equal(0, storage.DeleteCount);

        var lateWrite = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.Manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask());
        Assert.Same(expected, lateWrite.InnerException);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.Manager.DeleteStateAsync(TestContext.Current.CancellationToken).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.Manager.InitializeAsync(TestContext.Current.CancellationToken).AsTask());
        Assert.Throws<InvalidOperationException>(() => sut.Manager.RegisterState("late", new AlwaysWritingState()));

        await sut.Manager.DisposeAsync();
        var recovered = CreateTestSystem(storage: storage);
        var recoveredValue = new DurableValue<int>("value", recovered.Manager, CreateValueCodec<int>());
        var recoveredDictionary = new DurableDictionary<string, int>("dict", recovered.Manager, CreateDictionaryCodec<string, int>());
        await recovered.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        Assert.Equal(1, recoveredValue.Value);
        Assert.Single(recoveredDictionary);
        Assert.Equal(1, recoveredDictionary["persisted"]);
    }

    [Theory]
    [InlineData("initialize")]
    [InlineData("append")]
    [InlineData("replace")]
    [InlineData("delete")]
    public async Task StateManager_FailureDeactivatesOwningGrain(string operation)
    {
        var expected = new IOException("Expected journal operation failure.");
        var storage = new CapturingStorage();
        var storageProvider = Substitute.For<IJournalStorageProvider>();
        var context = Substitute.For<IGrainContext>();
        var grainId = GrainId.Create("test-grain", "failing-journal");
        context.GrainId.Returns(grainId);
        storageProvider.CreateStorage(JournalId.FromGrainId(grainId)).Returns(storage);
        var shared = new JournaledStateManagerShared(
            ServiceProvider.GetRequiredService<ILogger<JournaledStateManager>>(),
            Options.Create(ManagerOptions), TimeProvider.System, ServiceProvider);
        await using var manager = new JournaledStateManager(shared, storageProvider, context);
        var value = new DurableValue<int>("value", manager, CreateValueCodec<int>());
        if (operation == "initialize")
        {
            storage.NextReadException = expected;
        }
        else
        {
            await manager.InitializeAsync(TestContext.Current.CancellationToken);
            value.Value = 42;
            storage.NextAppendException = expected;
            storage.NextReplaceException = expected;
            storage.NextDeleteException = expected;
            storage.IsCompactionRequested = operation == "replace";
        }

        var failedOperation = operation switch
        {
            "initialize" => manager.InitializeAsync(TestContext.Current.CancellationToken),
            "delete" => manager.DeleteStateAsync(TestContext.Current.CancellationToken),
            _ => manager.WriteStateAsync(TestContext.Current.CancellationToken)
        };
        var exception = await Assert.ThrowsAsync<IOException>(() => failedOperation.AsTask());
        Assert.Same(expected, exception);
        context.Received(1).Deactivate(
            Arg.Is<DeactivationReason>(reason => reason.ReasonCode == DeactivationReasonCode.ApplicationError && ReferenceEquals(reason.Exception, expected)),
            Arg.Any<CancellationToken>());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.InitializeAsync(TestContext.Current.CancellationToken).AsTask());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StateManager_FactoryRecreationRecoversActualCommitOutcome(bool committed)
    {
        var storage = new CapturingStorage();
        var storageProvider = Substitute.For<IJournalStorageProvider>();
        var journalId = new JournalId("standalone/fail-closed");
        storageProvider.CreateStorage(journalId).Returns(storage);
        var shared = new JournaledStateManagerShared(
            ServiceProvider.GetRequiredService<ILogger<JournaledStateManager>>(),
            Options.Create(ManagerOptions), TimeProvider.System, ServiceProvider);
        var factory = new JournaledStateManagerFactory(shared, storageProvider);
        var manager = factory.Create(journalId);
        var value = new DurableValue<int>("value", manager, CreateValueCodec<int>());
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        value.Value = 1;
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        value.Value = 2;
        var expected = new IOException("Commit acknowledgement failed.");
        if (committed)
        {
            storage.NextPostAppendException = expected;
        }
        else
        {
            storage.NextAppendException = expected;
        }

        var exception = await Assert.ThrowsAsync<IOException>(
            () => manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask());
        Assert.Same(expected, exception);
        Assert.Equal(2, value.Value);
        await manager.DisposeAsync();

        await using var recovered = factory.Create(journalId);
        var recoveredValue = new DurableValue<int>("value", recovered, CreateValueCodec<int>());
        await recovered.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.Equal(committed ? 2 : 1, recoveredValue.Value);
        recoveredValue.Value = 3;
        await recovered.WriteStateAsync(TestContext.Current.CancellationToken);
        storageProvider.Received(2).CreateStorage(journalId);
    }

    [Fact]
    public async Task StateManager_CancelledWriteWaitPreservesCommit()
    {
        var storage = new CapturingStorage { BlockNextAppend = true };
        var sut = CreateTestSystem(storage: storage);
        var value = new DurableValue<int>("value", sut.Manager, CreateValueCodec<int>());
        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        value.Value = 42;
        using var cancellation = new CancellationTokenSource();
        var write = sut.Manager.WriteStateAsync(cancellation.Token).AsTask();
        await storage.AppendEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
        storage.ReleaseAppend.SetResult();
        await sut.Manager.WriteStateAsync(TestContext.Current.CancellationToken);

        var recovered = CreateTestSystem(storage: storage);
        var recoveredValue = new DurableValue<int>("value", recovered.Manager, CreateValueCodec<int>());
        await recovered.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        Assert.Equal(42, recoveredValue.Value);
    }

    [Fact]
    public async Task StateManager_ObserverPreparationParticipatesInAtomicCommit()
    {
        var storage = new CapturingStorage();
        var sut = CreateTestSystem(storage: storage);
        var value = new DurableValue<int>("value", sut.Manager, CreateValueCodec<int>());
        var observer = new RecordingStateObserver(() => value.Value = 42);
        sut.Manager.RegisterObserver(observer);

        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        await sut.Manager.WriteStateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["Preparing", "Started", "Completed"], observer.WriteCalls);
        var recovered = CreateTestSystem(storage: storage);
        var recoveredValue = new DurableValue<int>("value", recovered.Manager, CreateValueCodec<int>());
        await recovered.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        Assert.Equal(42, recoveredValue.Value);
    }

    [Fact]
    public async Task StateManager_RegisterObserverAfterInitializationIsRejected()
    {
        var sut = CreateTestSystem();
        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);

        var exception = Assert.Throws<NotSupportedException>(
            () => sut.Manager.RegisterObserver(new RecordingStateObserver()));

        Assert.Contains("after initialization", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StateManager_FailedWriteNotifiesFaultAndFreshManagerRestoresState()
    {
        var storage = new CapturingStorage();
        var sut = CreateTestSystem(storage: storage);
        var value = new DurableValue<int>("value", sut.Manager, CreateValueCodec<int>());
        var observer = new RecordingStateObserver();
        sut.Manager.RegisterObserver(observer);
        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        value.Value = 1;
        await sut.Manager.WriteStateAsync(TestContext.Current.CancellationToken);

        storage.NextAppendException = new InconsistentStateException("Expected write conflict.");
        value.Value = 2;
        await Assert.ThrowsAsync<InconsistentStateException>(
            () => sut.Manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(2, observer.WriteStartedCount);
        Assert.Equal(1, observer.WriteCompletedCount);
        Assert.Equal(1, observer.RecoveryCompletedCount);
        Assert.IsType<InconsistentStateException>(Assert.Single(observer.Faults));
        Assert.Equal(2, value.Value);
        await sut.Manager.DisposeAsync();
        await using var recovered = CreateTestSystem(storage: storage).Manager;
        var recoveredValue = new DurableValue<int>("value", recovered, CreateValueCodec<int>());
        var recoveredObserver = new RecordingStateObserver();
        recovered.RegisterObserver(recoveredObserver);
        await recovered.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, recoveredValue.Value);
        Assert.Equal(1, recoveredObserver.RecoveryCompletedCount);
        Assert.Empty(recoveredObserver.Faults);
    }

    [Fact]
    public async Task StateManager_NoOpWritePairsObserverBoundary()
    {
        var storage = new CapturingStorage();
        var sut = CreateTestSystem(storage: storage);
        var observer = new RecordingStateObserver();
        sut.Manager.RegisterObserver(observer);
        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        await sut.Manager.WriteStateAsync(TestContext.Current.CancellationToken);
        observer.WriteCalls.Clear();
        storage.OperationLog.Clear();

        await sut.Manager.WriteStateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["Preparing", "Started", "Completed"], observer.WriteCalls);
        Assert.Empty(storage.OperationLog);
    }

    [Fact]
    public async Task StateManager_FinalizationRunsAfterEveryPreparationAndBeforeCapture()
    {
        var storage = new CapturingStorage();
        var sut = CreateTestSystem(storage: storage);
        var value = new DurableValue<int>("value", sut.Manager, CreateValueCodec<int>());
        var finalizer = new FinalizingStateObserver(() => Assert.Equal(42, value.Value));
        sut.Manager.RegisterObserver(finalizer);
        sut.Manager.RegisterObserver(new RecordingStateObserver(() => value.Value = 42));

        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        await sut.Manager.WriteStateAsync(TestContext.Current.CancellationToken);

        Assert.True(finalizer.Finalized);
        var recovered = CreateTestSystem(storage: storage);
        var recoveredValue = new DurableValue<int>("value", recovered.Manager, CreateValueCodec<int>());
        await recovered.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        Assert.Equal(42, recoveredValue.Value);
    }

    [Fact]
    public async Task StateManager_RecoveryObserverRunsOnceAfterAllStates()
    {
        var storage = new CapturingStorage();
        var initial = CreateTestSystem(storage: storage);
        var initialFirst = new DurableValue<int>("first", initial.Manager, CreateValueCodec<int>());
        var initialSecond = new DurableValue<int>("second", initial.Manager, CreateValueCodec<int>());
        await initial.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        initialFirst.Value = 1;
        initialSecond.Value = 2;
        await initial.Manager.WriteStateAsync(TestContext.Current.CancellationToken);

        var sut = CreateTestSystem(storage: storage);
        var first = new DurableValue<int>("first", sut.Manager, CreateValueCodec<int>());
        var second = new DurableValue<int>("second", sut.Manager, CreateValueCodec<int>());
        var observer = new RecoveryStateObserver(() =>
        {
            Assert.Equal(1, first.Value);
            Assert.Equal(2, second.Value);
        });
        sut.Manager.RegisterObserver(observer);

        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);

        Assert.Equal(1, observer.RecoveryStartedCount);
        Assert.Equal(1, observer.RecoveryCompletedCount);
    }

    [Fact]
    public async Task StateManager_DeleteNotifiesObserverAfterSuccessfulDeletion()
    {
        var sut = CreateTestSystem();
        var observer = new RecordingStateObserver();
        sut.Manager.RegisterObserver(observer);
        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);

        await sut.Manager.DeleteStateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, observer.DeleteCompletedCount);
    }

    [Fact]
    public async Task StateManager_RegisterObserverRejectsDuplicates()
    {
        var sut = CreateTestSystem();
        await using var manager = sut.Manager;
        var observer = new RecordingStateObserver();
        manager.RegisterObserver(observer);

        var exception = Assert.Throws<InvalidOperationException>(() => manager.RegisterObserver(observer));
        Assert.Contains("already registered", exception.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentNullException>(() => manager.RegisterObserver(null!));

        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, observer.RecoveryCompletedCount);
        Assert.Equal(["Preparing", "Started", "Completed"], observer.WriteCalls);
    }

    [Fact]
    public void StateManager_RegisterObserverDefaultImplementationRejectsNull()
    {
        IJournaledStateManager manager = new LegacyStateManager();
        Assert.Throws<ArgumentNullException>("observer", () => manager.RegisterObserver(null!));
    }

    [Fact]
    public void StateManager_RegisterObserverDefaultImplementationPreservesCompatibility()
    {
        IJournaledStateManager manager = new LegacyStateManager();
        Assert.Throws<NotSupportedException>(() => manager.RegisterObserver(new RecordingStateObserver()));
    }

    [Fact]
    public async Task StateManager_RegisterObserverDuringInitializationIsRejected()
    {
        var storage = new MutableReadStorage(blockedReadNumber: 1, new byte[0]);
        var sut = CreateTestSystem(storage: storage);
        await using var manager = sut.Manager;
        var observer = new RecordingStateObserver();
        manager.RegisterObserver(observer);
        var initialization = manager.InitializeAsync(TestContext.Current.CancellationToken).AsTask();
        await storage.BlockedReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        try
        {
            Assert.Equal(1, observer.RecoveryStartedCount);
            Assert.Equal(0, observer.RecoveryCompletedCount);
            Assert.Throws<NotSupportedException>(() => manager.RegisterObserver(new RecordingStateObserver()));
        }
        finally
        {
            storage.AllowBlockedRead.SetResult();
        }

        await initialization;
        Assert.Equal(1, observer.RecoveryCompletedCount);
    }

    [Theory]
    [InlineData("Write")]
    [InlineData("Delete")]
    public async Task StateManager_ObserverRequestRejectionPreservesPendingState(string operation)
    {
        var storage = new CapturingStorage();
        var sut = CreateTestSystem(storage: storage);
        await using var manager = sut.Manager;
        var value = new DurableValue<int>("value", manager, CreateValueCodec<int>());
        var expected = new InvalidOperationException("Expected request rejection.");
        var reject = false;
        var observer = new BoundaryStateObserver(callback =>
        {
            if (reject && callback == operation + "Requested")
            {
                throw expected;
            }
        });
        manager.RegisterObserver(observer);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        value.Value = 1;
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        var persisted = storage.RecoverableBytes;
        value.Value = 2;
        observer.Calls.Clear();
        storage.OperationLog.Clear();
        reject = true;

        var request = RequestOperation(manager, operation, TestContext.Current.CancellationToken);
        Assert.True(request.IsCompleted);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => request.AsTask());

        Assert.Same(expected, exception);
        Assert.Equal([operation + "Requested"], observer.Calls);
        Assert.Equal(2, value.Value);
        Assert.Equal(persisted, storage.RecoverableBytes);
        Assert.Empty(storage.OperationLog);

        reject = false;
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        await using var recovered = CreateTestSystem(storage: storage).Manager;
        var recoveredValue = new DurableValue<int>("value", recovered, CreateValueCodec<int>());
        await recovered.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, recoveredValue.Value);
    }

    [Theory]
    [InlineData("WritePreparing")]
    [InlineData("WriteFinalizing")]
    [InlineData("DeletePreparing")]
    public async Task StateManager_ObserverPreparationRejectionPrecedesCapture(string rejectedCallback)
    {
        var storage = new CapturingStorage();
        var sut = CreateTestSystem(storage: storage);
        await using var manager = sut.Manager;
        var value = new DurableValue<int>("value", manager, CreateValueCodec<int>());
        var expected = new InvalidOperationException("Expected preparation rejection.");
        var reject = false;
        var observer = new BoundaryStateObserver(prepare: async (callback, cancellationToken) =>
        {
            if (reject && callback == rejectedCallback)
            {
                await Task.Yield();
                throw expected;
            }
        });
        manager.RegisterObserver(observer);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        value.Value = 1;
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        var persisted = storage.RecoverableBytes;
        value.Value = 2;
        var pendingBytes = manager.PendingWriteByteCount;
        observer.Calls.Clear();
        storage.OperationLog.Clear();
        reject = true;
        var operation = rejectedCallback == "DeletePreparing" ? "Delete" : "Write";

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RequestOperation(manager, operation, TestContext.Current.CancellationToken).AsTask());

        Assert.Same(expected, exception);
        Assert.Equal(rejectedCallback == "WriteFinalizing"
            ? ["WriteRequested", "WritePreparing", "WriteFinalizing", "Faulted"]
            : new[] { operation + "Requested", rejectedCallback, "Faulted" }, observer.Calls);
        Assert.Equal(2, value.Value);
        Assert.Equal(pendingBytes, manager.PendingWriteByteCount);
        Assert.Equal(persisted, storage.RecoverableBytes);
        Assert.Empty(storage.OperationLog);

        Assert.Same(expected, Assert.Single(observer.Faults));
        var lateWrite = await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask());
        Assert.Same(expected, lateWrite.InnerException);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.DeleteStateAsync(TestContext.Current.CancellationToken).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.InitializeAsync(TestContext.Current.CancellationToken).AsTask());
        await manager.DisposeAsync();
        await using var recovered = CreateTestSystem(storage: storage).Manager;
        var recoveredValue = new DurableValue<int>("value", recovered, CreateValueCodec<int>());
        await recovered.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, recoveredValue.Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StateManager_AsyncObserverPhasesCompleteBeforeCapture(bool snapshot)
    {
        var storage = new CapturingStorage { IsCompactionRequested = snapshot };
        var sut = CreateTestSystem(storage: storage);
        await using var manager = sut.Manager;
        var value = new DurableValue<int>("value", manager, CreateValueCodec<int>());
        var preparing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowPreparation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finalizing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowFinalization = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observer = new BoundaryStateObserver(prepare: async (callback, cancellationToken) =>
        {
            if (callback == "WritePreparing")
            {
                preparing.SetResult();
                await allowPreparation.Task.WaitAsync(cancellationToken);
                value.Value = 42;
            }
            else if (callback == "WriteFinalizing")
            {
                finalizing.SetResult();
                await allowFinalization.Task.WaitAsync(cancellationToken);
                value.Value = 43;
            }
        });
        manager.RegisterObserver(observer);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        observer.Calls.Clear();

        var write = manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        await preparing.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Empty(storage.OperationLog);
        Assert.Equal(["WriteRequested", "WritePreparing"], observer.Calls);
        Assert.Throws<NotSupportedException>(() => manager.RegisterObserver(new RecordingStateObserver()));
        allowPreparation.SetResult();
        await finalizing.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Empty(storage.OperationLog);
        Assert.Equal(["WriteRequested", "WritePreparing", "WriteFinalizing"], observer.Calls);
        allowFinalization.SetResult();
        await write.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(["WriteRequested", "WritePreparing", "WriteFinalizing", "WriteStarted", "WriteCompleted"], observer.Calls);
        Assert.Equal([snapshot ? "replace" : "append"], storage.OperationLog);
        await using var recovered = CreateTestSystem(storage: storage).Manager;
        var recoveredValue = new DurableValue<int>("value", recovered, CreateValueCodec<int>());
        await recovered.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.Equal(43, recoveredValue.Value);
    }

    [Theory]
    [InlineData("Write")]
    [InlineData("Delete")]
    public async Task StateManager_CanceledRequestPrecedesObserverGuards(string operation)
    {
        var sut = CreateTestSystem();
        await using var manager = sut.Manager;
        var observer = new BoundaryStateObserver();
        manager.RegisterObserver(observer);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        observer.Calls.Clear();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => RequestOperation(manager, operation, cancellation.Token).AsTask());

        Assert.Empty(observer.Calls);
    }

    [Theory]
    [InlineData("Write")]
    [InlineData("Delete")]
    public async Task StateManager_CancelingWaitPreservesObserverOperation(string operation)
    {
        var storage = new CapturingStorage();
        var sut = CreateTestSystem(storage: storage);
        await using var manager = sut.Manager;
        var value = new DurableValue<int>("value", manager, CreateValueCodec<int>());
        var preparing = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowPreparation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observer = new BoundaryStateObserver(
            callback =>
            {
                if (callback == operation + "Completed")
                {
                    completed.SetResult();
                }
            },
            async (callback, cancellationToken) =>
            {
                if (callback == operation + "Preparing")
                {
                    preparing.SetResult(cancellationToken);
                    await allowPreparation.Task.WaitAsync(cancellationToken);
                }
            });
        manager.RegisterObserver(observer);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        value.Value = 42;
        using var cancellation = new CancellationTokenSource();

        var request = RequestOperation(manager, operation, cancellation.Token).AsTask();
        var preparationToken = await preparing.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.False(preparationToken.IsCancellationRequested);
        Assert.Empty(storage.OperationLog);
        allowPreparation.SetResult();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal([operation == "Write" ? "append" : "delete"], storage.OperationLog);
        Assert.Empty(observer.Faults);
        await using var recovered = CreateTestSystem(storage: storage).Manager;
        var recoveredValue = new DurableValue<int>("value", recovered, CreateValueCodec<int>());
        await recovered.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.Equal(operation == "Write" ? 42 : 0, recoveredValue.Value);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task StateManager_IdleShutdownCompletesWithoutFaultNotification(bool dispose, bool write)
    {
        var logger = new ObserverLogger();
        var storage = new CapturingStorage();
        var provider = Substitute.For<IJournalStorageProvider>();
        var context = Substitute.For<IGrainContext>();
        var grainId = GrainId.Create("test-grain", "idle-observer-shutdown");
        context.GrainId.Returns(grainId);
        provider.CreateStorage(JournalId.FromGrainId(grainId)).Returns(storage);
        var shared = new JournaledStateManagerShared(logger, Options.Create(ManagerOptions), TimeProvider.System, ServiceProvider);
        var scheduler = new ConcurrentExclusiveSchedulerPair(TaskScheduler.Default, 1);
        try
        {
            await Task.Factory.StartNew(async () =>
            {
                await using var manager = new JournaledStateManager(shared, provider, context);
                var value = new DurableValue<int>("value", manager, CreateValueCodec<int>());
                var observer = new BoundaryStateObserver();
                manager.RegisterObserver(observer);
                await manager.InitializeAsync(TestContext.Current.CancellationToken);
                if (write)
                {
                    value.Value = 42;
                    await manager.WriteStateAsync(TestContext.Current.CancellationToken);
                }

                // The exclusive scheduler resumes this caller after the work loop has suspended waiting for work.
                Assert.Same(scheduler.ExclusiveScheduler, TaskScheduler.Current);
                var callsBeforeShutdown = observer.Calls.ToArray();
                if (dispose)
                {
                    await manager.DisposeAsync();
                }
                else
                {
                    await ((ILifecycleObserver)manager).OnStop(TestContext.Current.CancellationToken);
                }

                Assert.Empty(observer.Faults);
                Assert.Equal(callsBeforeShutdown, observer.Calls);
                Assert.Empty(logger.Entries);
                Assert.Equal(write ? 42 : 0, value.Value);
                context.DidNotReceive().Deactivate(Arg.Any<DeactivationReason>(), Arg.Any<CancellationToken>());
            }, TestContext.Current.CancellationToken, TaskCreationOptions.DenyChildAttach, scheduler.ExclusiveScheduler)
                .Unwrap().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        finally
        {
            scheduler.Complete();
            await scheduler.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
    }

    [Theory]
    [InlineData("WritePreparing")]
    [InlineData("WriteFinalizing")]
    [InlineData("DeletePreparing")]
    public async Task StateManager_ShutdownCancelsObserverPreparation(string phase)
    {
        var logger = new ObserverLogger();
        var storage = new CapturingStorage();
        var shared = new JournaledStateManagerShared(logger, Options.Create(ManagerOptions), TimeProvider.System, ServiceProvider);
        await using var manager = new JournaledStateManager(shared, storage);
        var preparing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var notificationFailure = new InvalidOperationException("Expected shutdown notification failure.");
        var observer = new BoundaryStateObserver(prepare: async (callback, cancellationToken) =>
        {
            if (callback == phase)
            {
                preparing.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
        }, faulted: _ => throw notificationFailure);
        Task[] pending = [];
        bool[]? completedDuringNotification = null;
        var remainingObserver = new BoundaryStateObserver(faulted: _ =>
            completedDuringNotification = pending.Select(task => task.IsCompleted).ToArray());
        manager.RegisterObserver(observer);
        manager.RegisterObserver(remainingObserver);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        observer.Calls.Clear();

        var operation = phase == "DeletePreparing" ? "Delete" : "Write";
        var request = RequestOperation(manager, operation, TestContext.Current.CancellationToken).AsTask();
        await preparing.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        pending =
        [
            request,
            manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask(),
            manager.DeleteStateAsync(TestContext.Current.CancellationToken).AsTask()
        ];
        await manager.DisposeAsync();
        var failure = Assert.IsAssignableFrom<OperationCanceledException>(Assert.Single(observer.Faults));
        foreach (var task in pending)
        {
            Assert.Same(failure, await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task));
        }

        Assert.NotNull(completedDuringNotification);
        Assert.Equal([false, false, false], completedDuringNotification);
        Assert.Same(failure, Assert.Single(remainingObserver.Faults));
        Assert.Equal(phase == "WriteFinalizing"
            ? ["WriteRequested", "WritePreparing", "WriteFinalizing", "WriteRequested", "DeleteRequested", "Faulted"]
            : new[] { operation + "Requested", phase, "WriteRequested", "DeleteRequested", "Faulted" }, observer.Calls);
        Assert.Empty(storage.OperationLog);
        var log = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, log.Level);
        Assert.Same(notificationFailure, log.Exception);
        Assert.Equal("Journaled state observer callback OnFaulted failed.", log.Message);
    }

    [Fact]
    public async Task StateManager_ShutdownDuringPersistenceNotifiesTerminalCancellation()
    {
        var storage = new CapturingStorage { BlockNextAppend = true };
        var sut = CreateTestSystem(storage: storage);
        await using var manager = sut.Manager;
        var value = new DurableValue<int>("value", manager, CreateValueCodec<int>());
        var observer = new BoundaryStateObserver();
        manager.RegisterObserver(observer);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        value.Value = 42;
        observer.Calls.Clear();

        var write = manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        await storage.BlockedAppendStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await manager.DisposeAsync();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);

        Assert.Same(exception, Assert.Single(observer.Faults));
        Assert.Equal(["WriteRequested", "WritePreparing", "WriteFinalizing", "WriteStarted", "Faulted"], observer.Calls);
        Assert.Empty(storage.Appends);
        Assert.Equal(42, value.Value);
    }

    [Fact]
    public async Task StateManager_ObserverNotificationFailuresAreLoggedAndRemainingObserversRun()
    {
        var logger = new ObserverLogger();
        var shared = new JournaledStateManagerShared(logger, Options.Create(ManagerOptions), TimeProvider.System, ServiceProvider);
        var storage = new CapturingStorage();
        await using var manager = new JournaledStateManager(shared, storage);
        var value = new DurableValue<int>("value", manager, CreateValueCodec<int>());
        var expected = new InvalidOperationException("Expected notification failure.");
        manager.RegisterObserver(new BoundaryStateObserver(callback =>
        {
            if (callback.EndsWith("Started", StringComparison.Ordinal) || callback.EndsWith("Completed", StringComparison.Ordinal))
            {
                throw expected;
            }
        }));
        var observer = new RecordingStateObserver();
        manager.RegisterObserver(observer);

        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        value.Value = 42;
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(42, value.Value);
        await manager.DeleteStateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, value.Value);
        Assert.Equal(["Preparing", "Started", "Completed"], observer.WriteCalls);
        Assert.Equal(1, observer.RecoveryStartedCount);
        Assert.Equal(1, observer.RecoveryCompletedCount);
        Assert.Equal(1, observer.DeleteCompletedCount);
        Assert.Equal(new[]
        {
            "OnRecoveryStarted", "OnRecoveryCompleted", "OnWriteStarted", "OnWriteCompleted",
            "OnDeleteCompleted"
        }.Select(callback => $"Journaled state observer callback {callback} failed."), logger.Entries.Select(entry => entry.Message));
        Assert.All(logger.Entries, entry =>
        {
            Assert.Equal(LogLevel.Error, entry.Level);
            Assert.Same(expected, entry.Exception);
        });
    }

    [Fact]
    public async Task StateManager_InitializationOrdersObserverAndStateCallbacks()
    {
        var sut = CreateTestSystem();
        await using var manager = sut.Manager;
        var events = new List<string>();
        manager.RegisterState("first", new RecoveryCallbackState("first", events));
        manager.RegisterState("second", new RecoveryCallbackState("second", events));
        manager.RegisterObserver(new BoundaryStateObserver(events.Add));

        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        events.Add("Initialized");

        Assert.Equal(new[]
        {
            "RecoveryStarted", "first reset", "second reset", "first recovered", "second recovered",
            "RecoveryCompleted", "Initialized"
        }, events);
    }

    [Theory]
    [InlineData("WritePreparing")]
    [InlineData("WriteFinalizing")]
    [InlineData("DeletePreparing")]
    public async Task StateManager_FaultNotificationPrecedesAdmittedAndQueuedFailures(string phase)
    {
        var expected = new IOException("Expected admitted preparation failure.");
        var notificationFailure = new InvalidOperationException("Expected fault notification failure.");
        var deactivationFailure = new ApplicationException("Expected deactivation notification failure.");
        var logger = new ObserverLogger();
        var storage = new CapturingStorage();
        var provider = Substitute.For<IJournalStorageProvider>();
        var context = Substitute.For<IGrainContext>();
        var grainId = GrainId.Create("test-grain", "observer-fault");
        context.GrainId.Returns(grainId);
        provider.CreateStorage(JournalId.FromGrainId(grainId)).Returns(storage);
        context.When(c => c.Deactivate(Arg.Any<DeactivationReason>(), Arg.Any<CancellationToken>()))
            .Do(_ => throw deactivationFailure);
        var shared = new JournaledStateManagerShared(logger, Options.Create(ManagerOptions), TimeProvider.System, ServiceProvider);
        await using var manager = new JournaledStateManager(shared, provider, context);
        var value = new DurableValue<int>("value", manager, CreateValueCodec<int>());
        var preparing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var throwing = new BoundaryStateObserver(prepare: async (callback, cancellationToken) =>
        {
            if (callback == phase)
            {
                preparing.SetResult();
                await allowFailure.Task.WaitAsync(cancellationToken);
                throw expected;
            }
        }, faulted: _ => throw notificationFailure);
        Task[] pending = [];
        bool[]? completedDuringNotification = null;
        Task? fencedAdmission = null;
        var observer = new BoundaryStateObserver(faulted: _ =>
        {
            completedDuringNotification = pending.Select(task => task.IsCompleted).ToArray();
            fencedAdmission = manager.InitializeAsync(TestContext.Current.CancellationToken).AsTask();
        });
        manager.RegisterObserver(throwing);
        manager.RegisterObserver(observer);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        value.Value = 42;
        var operation = phase == "DeletePreparing" ? "Delete" : "Write";
        var first = RequestOperation(manager, operation, TestContext.Current.CancellationToken).AsTask();
        await preparing.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        pending =
        [
            first,
            manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask(),
            manager.DeleteStateAsync(TestContext.Current.CancellationToken).AsTask(),
            manager.InitializeAsync(TestContext.Current.CancellationToken).AsTask()
        ];
        allowFailure.SetResult();

        foreach (var task in pending)
        {
            var exception = await Assert.ThrowsAsync<IOException>(
                () => task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.Same(expected, exception);
        }

        Assert.NotNull(completedDuringNotification);
        Assert.Equal([false, false, false, false], completedDuringNotification);
        Assert.NotNull(fencedAdmission);
        var fenced = await Assert.ThrowsAsync<InvalidOperationException>(() => fencedAdmission);
        Assert.Same(expected, fenced.InnerException);
        Assert.Same(expected, Assert.Single(throwing.Faults));
        Assert.Same(expected, Assert.Single(observer.Faults));
        Assert.Equal(42, value.Value);
        Assert.Empty(storage.OperationLog);
        Assert.DoesNotContain("WriteStarted", throwing.Calls);
        Assert.DoesNotContain("WriteCompleted", throwing.Calls);
        Assert.DoesNotContain("DeleteCompleted", throwing.Calls);
        Assert.Equal(1, observer.Calls.Count(call => call == "RecoveryCompleted"));
        context.Received(1).Deactivate(
            Arg.Is<DeactivationReason>(reason => ReferenceEquals(reason.Exception, expected)), Arg.Any<CancellationToken>());
        Assert.Collection(logger.Entries,
            entry =>
            {
                Assert.Equal(LogLevel.Error, entry.Level);
                Assert.Same(notificationFailure, entry.Exception);
                Assert.Equal("Journaled state observer callback OnFaulted failed.", entry.Message);
            },
            entry =>
            {
                Assert.Equal(LogLevel.Error, entry.Level);
                Assert.Same(expected, entry.Exception);
            });

        var later = await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask());
        Assert.Same(expected, later.InnerException);
        await manager.DisposeAsync();
        Assert.Same(expected, Assert.Single(observer.Faults));
        Assert.Single(throwing.Faults);
    }

    [Fact]
    public async Task StateManager_DefaultFaultNotificationPreservesOperationFailure()
    {
        await using var manager = CreateTestSystem().Manager;
        var expected = new IOException("Expected finalization failure.");
        manager.RegisterObserver(new FinalizingStateObserver(() => throw expected));
        await manager.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Same(expected, await Assert.ThrowsAsync<IOException>(
            () => manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask()));
        var fenced = await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.InitializeAsync(TestContext.Current.CancellationToken).AsTask());
        Assert.Same(expected, fenced.InnerException);
    }

    [Fact]
    public async Task StateManager_InitializationFailureNotifiesFaultOnceBeforeQueuedCallers()
    {
        var storage = new MutableReadStorage(blockedReadNumber: 1, new byte[0]);
        var sut = CreateTestSystem(storage: storage);
        await using var manager = sut.Manager;
        var expected = new IOException("Expected state recovery callback failure.");
        var state = new RecoveryCallbackState("state", []) { RecoveryException = expected };
        manager.RegisterState("state", state);
        Task[] pending = [];
        bool[]? completedDuringNotification = null;
        var observer = new BoundaryStateObserver(faulted: _ =>
            completedDuringNotification = pending.Select(task => task.IsCompleted).ToArray());
        manager.RegisterObserver(observer);
        var first = manager.InitializeAsync(TestContext.Current.CancellationToken).AsTask();
        await storage.BlockedReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        pending = [first, manager.InitializeAsync(TestContext.Current.CancellationToken).AsTask()];
        storage.AllowBlockedRead.SetResult();

        foreach (var task in pending)
        {
            Assert.Same(expected, await Assert.ThrowsAsync<IOException>(() => task));
        }

        Assert.NotNull(completedDuringNotification);
        Assert.Equal([false, false], completedDuringNotification);
        Assert.Equal(["RecoveryStarted", "Faulted"], observer.Calls);
        Assert.Same(expected, Assert.Single(observer.Faults));
        var later = await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.InitializeAsync(TestContext.Current.CancellationToken).AsTask());
        Assert.Same(expected, later.InnerException);
        Assert.Single(observer.Faults);
        await manager.DisposeAsync();

        await using var recovered = CreateTestSystem(storage: storage).Manager;
        recovered.RegisterState("state", new RecoveryCallbackState("state", []));
        var recoveredObserver = new BoundaryStateObserver();
        recovered.RegisterObserver(recoveredObserver);
        await recovered.InitializeAsync(TestContext.Current.CancellationToken);
        await recovered.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["RecoveryStarted", "RecoveryCompleted"], recoveredObserver.Calls);
    }

    [Fact]
    public async Task StateManager_QueuedWritesWaitForPreparationAndSynchronousFinalizationCapture()
    {
        var storage = new CapturingStorage();
        var sut = CreateTestSystem(storage: storage);
        await using var manager = sut.Manager;
        var effects = new DurableValue<int>("effects", manager, CreateValueCodec<int>());
        var completion = new DurableValue<int>("completion", manager, CreateValueCodec<int>());
        var events = new ConcurrentQueue<string>();
        var preparing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowPreparation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var preparationCount = 0;
        var prepared = false;
        var stagedEffects = 0;
        var stagedCompletion = 0;
        var applied = false;
        manager.RegisterState("capture", new CaptureCallbackState(() => events.Enqueue("Capture")));
        manager.RegisterObserver(new BoundaryStateObserver(
            callback =>
            {
                if (callback is "WriteStarted" or "WriteCompleted")
                {
                    events.Enqueue(callback);
                }
            },
            (callback, cancellationToken) =>
            {
                if (callback == "WritePreparing")
                {
                    events.Enqueue("FirstPreparing");
                }
                else if (callback == "WriteFinalizing")
                {
                    Assert.True(prepared);
                    events.Enqueue("FirstFinalizing");
                    if (!applied)
                    {
                        effects.Value = stagedEffects;
                        completion.Value = stagedCompletion;
                        applied = true;
                    }
                }

                return default;
            }));
        manager.RegisterObserver(new BoundaryStateObserver(prepare: async (callback, cancellationToken) =>
        {
            if (callback == "WritePreparing")
            {
                events.Enqueue("SecondPreparing");
                if (++preparationCount == 1)
                {
                    preparing.SetResult();
                    await allowPreparation.Task.WaitAsync(cancellationToken);
                    stagedEffects = 42;
                    stagedCompletion = 1;
                    prepared = true;
                }
            }
            else if (callback == "WriteFinalizing")
            {
                events.Enqueue("SecondFinalizing");
            }
        }));
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        var first = manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        await preparing.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var second = manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        var third = manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();

        Assert.Equal(["FirstPreparing", "SecondPreparing"], events);
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        Assert.False(third.IsCompleted);
        Assert.Empty(storage.OperationLog);
        Assert.Equal(0, effects.Value);
        Assert.Equal(0, completion.Value);
        allowPreparation.SetResult();
        await Task.WhenAll(first, second, third).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        string[] boundary = ["FirstPreparing", "SecondPreparing", "FirstFinalizing", "SecondFinalizing", "WriteStarted", "Capture", "WriteCompleted"];
        Assert.Equal(boundary.Concat(boundary), events);
        Assert.Single(storage.Appends);
        await using var recovered = CreateTestSystem(storage: storage).Manager;
        var recoveredEffects = new DurableValue<int>("effects", recovered, CreateValueCodec<int>());
        var recoveredCompletion = new DurableValue<int>("completion", recovered, CreateValueCodec<int>());
        await recovered.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.Equal(42, recoveredEffects.Value);
        Assert.Equal(1, recoveredCompletion.Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StateManager_StandaloneFaultObserverPreservesActualCommitOutcome(bool committed)
    {
        var storage = new CapturingStorage();
        var provider = Substitute.For<IJournalStorageProvider>();
        var journalId = new JournalId("observer-fault-standalone");
        provider.CreateStorage(journalId).Returns(storage);
        var shared = new JournaledStateManagerShared(
            LoggerFactory.CreateLogger<JournaledStateManager>(), Options.Create(ManagerOptions), TimeProvider.System, ServiceProvider);
        var factory = new JournaledStateManagerFactory(shared, provider);
        await using var manager = factory.Create(journalId);
        var value = new DurableValue<int>("value", manager, CreateValueCodec<int>());
        var expected = new IOException("Expected acknowledgement failure.");
        var observer = new RecordingStateObserver();
        manager.RegisterObserver(observer);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        value.Value = 1;
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        value.Value = 2;
        if (committed)
        {
            storage.NextPostAppendException = expected;
        }
        else
        {
            storage.NextAppendException = expected;
        }

        Assert.Same(expected, await Assert.ThrowsAsync<IOException>(
            () => manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask()));
        Assert.Same(expected, Assert.Single(observer.Faults));
        Assert.Equal(1, observer.WriteCompletedCount);
        Assert.Equal(1, observer.RecoveryCompletedCount);
        await manager.DisposeAsync();

        await using var recovered = factory.Create(journalId);
        var recoveredValue = new DurableValue<int>("value", recovered, CreateValueCodec<int>());
        var recoveredObserver = new RecordingStateObserver();
        recovered.RegisterObserver(recoveredObserver);
        await recovered.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.Equal(committed ? 2 : 1, recoveredValue.Value);
        Assert.Equal(1, recoveredObserver.RecoveryCompletedCount);
        Assert.Empty(recoveredObserver.Faults);
        provider.Received(2).CreateStorage(journalId);
    }

    [Fact]
    public async Task StateManager_LoggedStartNotificationPreservesTerminalStorageFailure()
    {
        var logger = new ObserverLogger();
        var expected = new IOException("Expected storage failure.");
        var notificationFailure = new InvalidOperationException("Expected start notification failure.");
        var storage = new CapturingStorage { NextAppendException = expected };
        var shared = new JournaledStateManagerShared(logger, Options.Create(ManagerOptions), TimeProvider.System, ServiceProvider);
        await using var manager = new JournaledStateManager(shared, storage);
        var value = new DurableValue<int>("value", manager, CreateValueCodec<int>());
        var observer = new BoundaryStateObserver(callback =>
        {
            if (callback == "WriteStarted")
            {
                throw notificationFailure;
            }
        });
        manager.RegisterObserver(observer);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        value.Value = 42;

        Assert.Same(expected, await Assert.ThrowsAsync<IOException>(
            () => manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask()));

        Assert.Same(expected, Assert.Single(observer.Faults));
        Assert.DoesNotContain("WriteCompleted", observer.Calls);
        Assert.Collection(logger.Entries,
            entry =>
            {
                Assert.Same(notificationFailure, entry.Exception);
                Assert.Equal("Journaled state observer callback OnWriteStarted failed.", entry.Message);
            },
            entry => Assert.Same(expected, entry.Exception));
    }

    private static ValueTask RequestOperation(IJournaledStateManager manager, string operation, CancellationToken cancellationToken) =>
        operation switch
        {
            "Write" => manager.WriteStateAsync(cancellationToken),
            "Delete" => manager.DeleteStateAsync(cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

    [Fact]
    public async Task StateManager_WriteStateAsync_CoalescesQueuedWrites()
    {
        var storage = new BlockingAppendStorage();
        var sut = CreateTestSystem(storage: storage);
        var state = new AlwaysWritingState();
        sut.Manager.RegisterState("state", state);
        var observer = new BoundaryStateObserver();
        sut.Manager.RegisterObserver(observer);
        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);

        var first = sut.Manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        await storage.FirstAppendStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var second = sut.Manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        var third = sut.Manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();

        storage.AllowFirstAppend.SetResult();
        await Task.WhenAll(first, second, third)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(2, state.AppendEntriesCount);
        Assert.Equal(2, storage.Appends.Count);
        Assert.Equal(3, observer.Calls.Count(callback => callback == "WriteRequested"));
        Assert.Equal(2, observer.Calls.Count(callback => callback == "WritePreparing"));
        Assert.Equal(2, observer.Calls.Count(callback => callback == "WriteFinalizing"));
        Assert.Equal(2, observer.Calls.Count(callback => callback == "WriteStarted"));
        Assert.Equal(2, observer.Calls.Count(callback => callback == "WriteCompleted"));
    }

    [Fact]
    public async Task StateManager_DeleteStateAsync_CoalescesQueuedDeletes()
    {
        var storage = new BlockingDeleteStorage();
        var sut = CreateTestSystem(storage: storage);
        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);

        var first = sut.Manager.DeleteStateAsync(TestContext.Current.CancellationToken).AsTask();
        await storage.FirstDeleteStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var second = sut.Manager.DeleteStateAsync(TestContext.Current.CancellationToken).AsTask();
        var third = sut.Manager.DeleteStateAsync(TestContext.Current.CancellationToken).AsTask();

        storage.AllowFirstDelete.SetResult();
        await Task.WhenAll(first, second, third)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(2, storage.DeleteCount);
    }

    [Fact]
    public async Task StateManager_WriteStateAsync_DoesNotObserveActiveEntry()
    {
        var storage = new CapturingStorage();
        var format = new TrackingJournalFormat(SessionPool);
        var sut = CreateTestSystem(storage: storage, journalFormat: format);
        var state = new ManualDirectWriteState();
        sut.Manager.RegisterState("manual", state);

        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);

        Task writeTask = Task.CompletedTask;
        using var writeStarted = new ManualResetEventSlim();
        var entry = state.BeginEntry();
        try
        {
            entry.Writer.Write(new byte[] { 1, 2, 3 });
            writeTask = Task.Run(async () =>
            {
                writeStarted.Set();
                await sut.Manager.WriteStateAsync(TestContext.Current.CancellationToken);
            }, TestContext.Current.CancellationToken);
            Assert.True(writeStarted.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

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

        await sut.Manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
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

        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);

        var writeTask = StartSnapshotAndCommitActiveEntry();

        await writeTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var replacement = Assert.Single(storage.Replaces);
        Assert.DoesNotContain(ReadBinaryEntries(replacement), entry => entry.StreamId.Value >= 8);
        Assert.Empty(storage.Appends);

        storage.IsCompactionRequested = false;
        await sut.Manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Single(storage.Appends);
        var append = storage.Appends[0];
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
        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);

        dictionary.Add("key1", 1);
        dictionary.Add("key2", 2);
        list.Add("item1");
        list.Add("item2");

        await sut.Manager.WriteStateAsync(CancellationToken.None);

        // Act - Create new manager with same storage
        var sut2 = CreateTestSystem(storage: sut.Storage);
        var recoveredDict = new DurableDictionary<string, int>("dict1", sut2.Manager, new OrleansBinaryDurableDictionaryCommandCodec<string, int>(CodecProvider.GetCodec<string>(), CodecProvider.GetCodec<int>(), SessionPool));
        var recoveredList = new DurableList<string>("list1", sut2.Manager, new OrleansBinaryDurableListCommandCodec<string>(CodecProvider.GetCodec<string>(), SessionPool));
        await sut2.Lifecycle.OnStart(TestContext.Current.CancellationToken);

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

        await initial.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        value.Value = 42;
        await initial.Manager.WriteStateAsync(CancellationToken.None);

        var format = new TrackingJournalFormat(SessionPool);
        var recovered = CreateTestSystem(storage: storage, journalFormat: format);
        var recoveredValue = new DurableValue<int>("value", recovered.Manager, CreateValueCodec<int>());

        await recovered.Lifecycle.OnStart(TestContext.Current.CancellationToken);

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

        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        next.Value = 99;
        await sut.Manager.WriteStateAsync(CancellationToken.None);

        Assert.Equal(42, existing.Value);
        var entries = ReadBinaryEntries(storage.Appends[^1]);
        Assert.Contains(entries, entry => entry.StreamId.Value == recoveredStreamId.Value + 1);
    }

    [Fact]
    public async Task StateManager_Recovery_RebindsStateAboveUnknownStreamIds()
    {
        var storage = new CapturingStorage { IsCompactionRequested = true };
        var unknownStreamId = new JournalStreamId(8);
        var unknownPayload = new byte[] { 1, 2, 3 };
        using (var segment = new OrleansBinaryJournalBufferWriter())
        {
            using (var entry = segment.CreateJournalStreamWriter(unknownStreamId).BeginEntry())
            {
                entry.Writer.Write(unknownPayload);
                entry.Commit();
            }

            using var committed = segment.GetBuffer();
            await storage.AppendAsync(committed.AsReadOnlySequence(), CancellationToken.None);
        }

        var sut = CreateTestSystem(storage: storage);
        var value = new DurableValue<int>("value", sut.Manager, CreateValueCodec<int>());
        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        value.Value = 42;

        await sut.Manager.WriteStateAsync(TestContext.Current.CancellationToken);

        var entries = ReadBinaryEntries(Assert.Single(storage.Replaces));
        Assert.Contains(entries, entry => entry.StreamId == unknownStreamId && entry.Payload.SequenceEqual(unknownPayload));
        Assert.Contains(entries, entry => entry.StreamId.Value == unknownStreamId.Value + 1);
    }

    [Fact]
    public async Task StateManager_Recovery_ReadsConcatenatedJournalData()
    {
        var storage = new CapturingStorage();
        var initial = CreateTestSystem(storage: storage);
        var value = new DurableValue<int>("value", initial.Manager, CreateValueCodec<int>());

        await initial.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        value.Value = 1;
        await initial.Manager.WriteStateAsync(CancellationToken.None);
        value.Value = 2;
        await initial.Manager.WriteStateAsync(CancellationToken.None);

        storage.ConcatenateReads = true;
        var recovered = CreateTestSystem(storage: storage);
        var recoveredValue = new DurableValue<int>("value", recovered.Manager, CreateValueCodec<int>());

        await recovered.Lifecycle.OnStart(TestContext.Current.CancellationToken);

        Assert.Equal(2, recoveredValue.Value);
        Assert.Equal(1, storage.ReadConsumeCount);
    }

    [Fact]
    public async Task StateManager_Recovery_BuffersEntriesSplitAcrossStorageChunks()
    {
        var storage = new CapturingStorage();
        var initial = CreateTestSystem(storage: storage);
        var value = new DurableValue<int>("value", initial.Manager, CreateValueCodec<int>());

        await initial.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        value.Value = 1;
        await initial.Manager.WriteStateAsync(CancellationToken.None);
        value.Value = 2;
        await initial.Manager.WriteStateAsync(CancellationToken.None);

        var persistedBytes = storage.Appends.SelectMany(static segment => segment).ToArray();
        var splitStorage = new ChunkedReadStorage(persistedBytes, chunkSize: 1);
        var recovered = CreateTestSystem(storage: splitStorage);
        var recoveredValue = new DurableValue<int>("value", recovered.Manager, CreateValueCodec<int>());

        await recovered.Lifecycle.OnStart(TestContext.Current.CancellationToken);

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
                () => sut.Lifecycle.OnStart(TestContext.Current.CancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
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
    public async Task StateManager_FreshRecovery_ReplaysFixedStorage()
    {
        var validBytes = CreatePersistedValueBytes("value", 42);
        var storage = new MutableReadStorage([.. validBytes, 1, 2, 3], validBytes);
        var sut = CreateTestSystem(storage: storage);
        var value = new DurableValue<int>("value", sut.Manager, CreateValueCodec<int>());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.Lifecycle.OnStart(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.Manager.InitializeAsync(TestContext.Current.CancellationToken).AsTask());
        await sut.Manager.DisposeAsync();
        sut = CreateTestSystem(storage: storage);
        value = new DurableValue<int>("value", sut.Manager, CreateValueCodec<int>());
        await sut.Manager.InitializeAsync(TestContext.Current.CancellationToken).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await sut.Manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(42, value.Value);
        await sut.Lifecycle.OnStop(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StateManager_FreshRecovery_PreservesUnknownStreamOnce()
    {
        var validBytes = CreateUnknownStreamBytes(new JournalStreamId(99), [1, 2, 3]);
        var storage = new MutableReadStorage([.. validBytes, 1, 2, 3], validBytes) { IsCompactionRequested = true };
        var sut = CreateTestSystem(storage: storage);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.Lifecycle.OnStart(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.Manager.InitializeAsync(TestContext.Current.CancellationToken).AsTask());
        await sut.Manager.DisposeAsync();
        sut = CreateTestSystem(storage: storage);
        await sut.Manager.InitializeAsync(TestContext.Current.CancellationToken).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await sut.Manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var replacement = Assert.Single(storage.Replaces);
        var preserved = Assert.Single(ReadBinaryEntries(replacement), entry => entry.StreamId.Value == 99);
        Assert.Equal([1, 2, 3], preserved.Payload);
        await sut.Lifecycle.OnStop(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StateManager_FreshRecovery_RemovesStaleRetiredPlaceholder()
    {
        var storage = new MutableReadStorage([.. CreateNamedUnknownStreamBytes("stale", new JournalStreamId(8), [1, 2, 3]), 1, 2, 3], []);
        var sut = CreateTestSystem(storage: storage);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.Lifecycle.OnStart(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.Manager.InitializeAsync(TestContext.Current.CancellationToken).AsTask());
        await sut.Manager.DisposeAsync();
        sut = CreateTestSystem(storage: storage);
        await sut.Manager.InitializeAsync(TestContext.Current.CancellationToken).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await sut.Manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.False(sut.Manager.TryGetState("stale", out _));
        await sut.Lifecycle.OnStop(TestContext.Current.CancellationToken);
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
                () => sut.Lifecycle.OnStart(TestContext.Current.CancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
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
        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);

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
        await sut2.Lifecycle.OnStart(TestContext.Current.CancellationToken);

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
        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);

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
        await sut2.Lifecycle.OnStart(TestContext.Current.CancellationToken);

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
        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);

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
        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);

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
        await sut2.Lifecycle.OnStart(TestContext.Current.CancellationToken);

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

        await sut1.Lifecycle.OnStart(TestContext.Current.CancellationToken);

        dictToKeep1.Add("a", 1);
        dictToRetire2.Add("b", 1);

        await sut1.Manager.WriteStateAsync(CancellationToken.None);

        // -------------- STEP 2 --------------

        // This time, we only register the dictionary we want to keep, this marks dictToRetire as retired.
        var sut2 = CreateTestSystem(storage, timeProvider);
        var dictToKeep2 = CreateTestState(DictToKeepKey, sut2.Manager);

        await sut2.Lifecycle.OnStart(TestContext.Current.CancellationToken);

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

        await sut3.Lifecycle.OnStart(TestContext.Current.CancellationToken);

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
        await sut3Recovered.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        Assert.Equal(2, dictToRetire3Recovered["b"]);

        // -------------- STEP 4 --------------

        // Because of re-registration is step 3 (to test it was not purged), this means dictToRetire has been removed from the tracker.
        // Again as in step 2, we only register the dictionary we want to keep, this marks dictToRetire as retired.
        var sut4 = CreateTestSystem(storage, timeProvider);
        var dictToKeep4 = CreateTestState(DictToKeepKey, sut4.Manager);

        await sut4.Lifecycle.OnStart(TestContext.Current.CancellationToken);

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

        await sut5.Lifecycle.OnStart(TestContext.Current.CancellationToken);
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

    [Fact]
    public async Task WorkLoop_BlockedAppendFencesQueuedDeleteUntilAppendCompletes()
    {
        var storage = new CapturingStorage { BlockNextAppend = true };
        var sut = CreateTestSystem(storage: storage);
        var value = new DurableValue<string>("value", sut.Manager, CreateValueCodec<string>());

        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        value.Value = "before-delete";
        var append = sut.Manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        await storage.AppendEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var deleteEntered = storage.DeleteEntered.Task;
        var delete = sut.Manager.DeleteStateAsync(TestContext.Current.CancellationToken).AsTask();

        Assert.Equal(["append"], storage.OperationLog);
        Assert.False(deleteEntered.IsCompleted);
        Assert.Equal(0, storage.DeleteCount);

        storage.ReleaseAppend.SetResult();
        await Task.WhenAll(append, delete).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(["append", "delete"], storage.OperationLog);
        Assert.False(storage.DeleteEnteredWhileAppendInProgress);
        Assert.Single(storage.Appends);
        Assert.Equal(1, storage.DeleteCount);
    }

    [Fact]
    public async Task WorkLoop_AppendDeleteReplacePersistsPostDeleteStateAndFreshManagerRecoversIt()
    {
        var storage = new CapturingStorage();
        var sut = CreateTestSystem(storage: storage);
        var value = new DurableValue<string>("value", sut.Manager, CreateValueCodec<string>());

        await sut.Lifecycle.OnStart(TestContext.Current.CancellationToken);
        value.Value = "before-delete";
        await sut.Manager.WriteStateAsync(TestContext.Current.CancellationToken);

        await sut.Manager.DeleteStateAsync(TestContext.Current.CancellationToken);
        value.Value = "after-delete";
        storage.IsCompactionRequested = true;
        await sut.Manager.WriteStateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["append", "delete", "replace"], storage.OperationLog);
        var appendBytes = Assert.Single(storage.Appends);
        var replacementBytes = Assert.Single(storage.Replaces);

        var appendReplay = ReplayValueCommands(appendBytes);
        Assert.Equal("before-delete", appendReplay.Value);
        Assert.Equal(["before-delete"], appendReplay.AppliedValues);

        var replacementReplay = ReplayValueCommands(replacementBytes);
        Assert.Equal("after-delete", replacementReplay.Value);
        Assert.Equal(["after-delete"], replacementReplay.AppliedValues);
        Assert.DoesNotContain("before-delete", replacementReplay.AppliedValues);
        Assert.Equal(replacementBytes, storage.RecoverableBytes);

        var recoveryCodec = new TrackingValueCodec<string>(CreateValueCodec<string>());
        var recovered = CreateTestSystem(storage: storage);
        var recoveredValue = new DurableValue<string>("value", recovered.Manager, recoveryCodec);
        await recovered.Lifecycle.OnStart(TestContext.Current.CancellationToken);

        Assert.Equal("after-delete", recoveredValue.Value);
        Assert.Equal(["after-delete"], recoveryCodec.AppliedValues);
        Assert.DoesNotContain("before-delete", recoveryCodec.AppliedValues);
    }

    [Fact]
    public async Task RecoverAsync_UnsupportedLegacyRecordRecoversInNewManagerAfterRepair()
    {
        var unsupportedBytes = CreateUnsupportedLegacyCommandVersionRecord(streamId: 128, commandVersion: 1);
        var validBytes = CreatePersistedStringValueBytes("value", "recovered");
        var storage = new MutableReadStorage(blockedReadNumber: 2, unsupportedBytes, validBytes);
        var codec = new TrackingValueCodec<string>(CreateValueCodec<string>());
        var sut = CreateTestSystem(storage: storage);
        var value = new DurableValue<string>("value", sut.Manager, codec);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.Lifecycle.OnStart(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        Assert.Equal(
            "Failed to recover journaling state using journal format key 'orleans-binary'. " +
            "The configured write journal format key is 'orleans-binary'.",
            exception.Message);
        var inner = Assert.IsType<NotSupportedException>(exception.InnerException);
        Assert.Equal("Unsupported legacy binary journal command format version at byte offset 0: 1.", inner.Message);
        Assert.Null(value.Value);
        Assert.Empty(codec.AppliedValues);
        Assert.Equal(unsupportedBytes, storage.Bytes);
        Assert.Empty(storage.Replaces);
        Assert.Empty(storage.OperationLog);

        await sut.Manager.DisposeAsync();
        sut = CreateTestSystem(storage: storage);
        value = new DurableValue<string>("value", sut.Manager, codec);
        var initialization = sut.Manager.InitializeAsync(TestContext.Current.CancellationToken).AsTask();
        await storage.BlockedReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        storage.AllowBlockedRead.SetResult();
        await initialization.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal("recovered", value.Value);
        Assert.Equal(["recovered"], codec.AppliedValues);
        Assert.Equal(validBytes, storage.Bytes);
        Assert.Empty(storage.Replaces);
        Assert.Empty(storage.OperationLog);
        await sut.Lifecycle.OnStop(TestContext.Current.CancellationToken);
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

    private byte[] CreatePersistedStringValueBytes(string name, string value)
    {
        using var segment = new OrleansBinaryJournalBufferWriter();
        AppendDirectorySet(segment, name, new JournalStreamId(8));
        CreateValueCodec<string>().WriteSet(value, segment.CreateJournalStreamWriter(new JournalStreamId(8)));

        using var committed = segment.GetBuffer();
        return committed.ToArray();
    }

    private static byte[] CreateUnsupportedLegacyCommandVersionRecord(ulong streamId, byte commandVersion)
    {
        using var writer = new ArcBufferWriter();
        var serializerWriter = Writer.Create(writer, session: null!);
        serializerWriter.WriteVarUInt32(checked((uint)(GetVarUInt64ByteCount(streamId) + 1)));
        serializerWriter.WriteVarUInt64(streamId);
        serializerWriter.WriteByte(commandVersion);
        serializerWriter.Commit();
        using var buffer = writer.PeekSlice(writer.Length);
        return buffer.ToArray();
    }

    private static int GetVarUInt64ByteCount(ulong value)
    {
        var result = 1;
        while (value >= 128)
        {
            value >>= 7;
            result++;
        }

        return result;
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

    private (string? Value, IReadOnlyList<string> AppliedValues) ReplayValueCommands(ReadOnlySpan<byte> bytes)
    {
        var entries = ReadBinaryEntries(bytes);
        var streamId = Assert.Single(entries.Where(static entry => entry.StreamId.Value >= 8).Select(static entry => entry.StreamId).Distinct());
        var codec = new TrackingValueCodec<string>(CreateValueCodec<string>());
        var state = new RecordingValueState<string>(codec);
        var context = JournalTestReplayContext.Create(OrleansBinaryJournalFormat.JournalFormatKey, (streamId, state));

        foreach (var entry in entries.Where(entry => entry.StreamId == streamId))
        {
            ((IJournaledState)state).ReplayEntry(
                new JournalEntry(OrleansBinaryJournalFormat.JournalFormatKey, CodecTestHelpers.ReadBuffer(entry.Payload)),
                context);
        }

        return (state.Value, codec.AppliedValues);
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

    private readonly record struct MetricMeasurement<T>(T Value, string? Operation, string? Status);

    private static MeterListener CreateMetricListener<T>(string instrumentName, ConcurrentBag<MetricMeasurement<T>> measurements)
        where T : struct
    {
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == "Microsoft.Orleans" && instrument.Name == instrumentName)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };

        listener.SetMeasurementEventCallback<T>((_, measurement, tags, _) =>
            measurements.Add(new(measurement, GetTag(tags, "operation"), GetTag(tags, "status"))));
        listener.Start();
        return listener;
    }

    private static string? GetTag(ReadOnlySpan<KeyValuePair<string, object?>> tags, string name)
    {
        foreach (var tag in tags)
        {
            if (tag.Key == name)
            {
                return tag.Value?.ToString();
            }
        }

        return null;
    }

    private sealed class BlockingAppendStorage : IJournalStorage
    {
        private int _appendCount;

        public TaskCompletionSource FirstAppendStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowFirstAppend { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<byte[]> Appends { get; } = [];

        public bool IsCompactionRequested => false;

        public ValueTask ReadAsync(IJournalStorageConsumer consumer, CancellationToken cancellationToken)
        {
            consumer.Complete(metadata: null);
            return default;
        }

        public ValueTask ReplaceAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken) => default;

        public async ValueTask AppendAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _appendCount) == 1)
            {
                FirstAppendStarted.SetResult();
                await AllowFirstAppend.Task.WaitAsync(cancellationToken);
            }

            Appends.Add(value.ToArray());
        }

        public ValueTask DeleteAsync(CancellationToken cancellationToken) => default;
    }

    private sealed class BlockingDeleteStorage : IJournalStorage
    {
        public TaskCompletionSource FirstDeleteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowFirstDelete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int DeleteCount { get; private set; }

        public bool IsCompactionRequested => false;

        public ValueTask ReadAsync(IJournalStorageConsumer consumer, CancellationToken cancellationToken)
        {
            consumer.Complete(metadata: null);
            return default;
        }

        public ValueTask ReplaceAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken) => default;

        public ValueTask AppendAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken) => default;

        public async ValueTask DeleteAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeleteCount++;
            if (DeleteCount == 1)
            {
                FirstDeleteStarted.SetResult();
                await AllowFirstDelete.Task.WaitAsync(cancellationToken);
            }
        }
    }

    private sealed class BoundaryStateObserver(
        Action<string>? notify = null,
        Func<string, CancellationToken, ValueTask>? prepare = null,
        Action<Exception>? faulted = null) : IJournaledStateObserver
    {
        public ConcurrentQueue<string> Calls { get; } = new();
        public ConcurrentQueue<Exception> Faults { get; } = new();

        public void OnFaulted(Exception exception)
        {
            Faults.Enqueue(exception);
            Notify("Faulted");
            faulted?.Invoke(exception);
        }

        private void Notify(string callback)
        {
            Calls.Enqueue(callback);
            notify?.Invoke(callback);
        }

        private ValueTask Prepare(string callback, CancellationToken cancellationToken)
        {
            Calls.Enqueue(callback);
            return prepare?.Invoke(callback, cancellationToken) ?? default;
        }

        public void OnWriteRequested() => Notify("WriteRequested");
        public void OnDeleteRequested() => Notify("DeleteRequested");
        public void OnWriteStarted() => Notify("WriteStarted");
        public void OnWriteCompleted() => Notify("WriteCompleted");
        public void OnDeleteCompleted() => Notify("DeleteCompleted");
        public void OnRecoveryStarted() => Notify("RecoveryStarted");
        public void OnRecoveryCompleted() => Notify("RecoveryCompleted");
        public ValueTask OnWritePreparingAsync(CancellationToken cancellationToken) => Prepare("WritePreparing", cancellationToken);
        public ValueTask OnWriteFinalizingAsync(CancellationToken cancellationToken) => Prepare("WriteFinalizing", cancellationToken);
        public ValueTask OnDeletePreparingAsync(CancellationToken cancellationToken) => Prepare("DeletePreparing", cancellationToken);
    }

    private sealed class ObserverLogger : ILogger<JournaledStateManager>
    {
        public ConcurrentQueue<(LogLevel Level, Exception? Exception, string Message)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue((logLevel, exception, formatter(state, exception)));
    }

    private sealed class RecoveryCallbackState(string name, List<string> events) : IJournaledState
    {
        public void Reset(JournalStreamWriter writer) => events.Add(name + " reset");
        public Exception? RecoveryException { get; init; }
        public void OnRecoveryCompleted()
        {
            events.Add(name + " recovered");
            if (RecoveryException is { } exception)
            {
                throw exception;
            }
        }
        public void ReplayEntry(JournalEntry entry, JournalReplayContext context) { }
        public void AppendEntries(JournalStreamWriter writer) { }
        public void AppendSnapshot(JournalStreamWriter writer) { }
        public IJournaledState DeepCopy() => throw new NotSupportedException();
    }

    private sealed class CaptureCallbackState(Action capture) : IJournaledState
    {
        public void Reset(JournalStreamWriter writer) { }
        public void ReplayEntry(JournalEntry entry, JournalReplayContext context) { }
        public void AppendEntries(JournalStreamWriter writer) => capture();
        public void AppendSnapshot(JournalStreamWriter writer) => capture();
        public IJournaledState DeepCopy() => throw new NotSupportedException();
    }

    private sealed class LegacyStateManager : IJournaledStateManager
    {
        public ValueTask InitializeAsync(CancellationToken cancellationToken) => default;
        public void RegisterState(string name, IJournaledState state) { }
        public bool TryGetState(string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IJournaledState? state)
        {
            state = null;
            return false;
        }

        public ValueTask WriteStateAsync(CancellationToken cancellationToken) => default;
        public ValueTask DeleteStateAsync(CancellationToken cancellationToken) => default;
    }

    private sealed class RecordingStateObserver(Action? prepare = null) : IJournaledStateObserver
    {
        public List<string> WriteCalls { get; } = [];
        public int WriteStartedCount { get; private set; }
        public int WriteCompletedCount { get; private set; }
        public int RecoveryCompletedCount { get; private set; }
        public ConcurrentQueue<Exception> Faults { get; } = new();
        public void OnFaulted(Exception exception) => Faults.Enqueue(exception);
        public int DeleteCompletedCount { get; private set; }
        public int RecoveryStartedCount { get; private set; }

        public ValueTask OnWritePreparingAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteCalls.Add("Preparing");
            prepare?.Invoke();
            return default;
        }

        public void OnWriteStarted()
        {
            WriteCalls.Add("Started");
            WriteStartedCount++;
        }

        public void OnWriteCompleted()
        {
            WriteCalls.Add("Completed");
            WriteCompletedCount++;
        }

        public void OnRecoveryCompleted() => RecoveryCompletedCount++;
        public void OnRecoveryStarted() => RecoveryStartedCount++;
        public void OnDeleteCompleted() => DeleteCompletedCount++;
    }

    private sealed class FinalizingStateObserver(Action finalize) : IJournaledStateObserver
    {
        public bool Finalized { get; private set; }

        public ValueTask OnWriteFinalizingAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            finalize();
            Finalized = true;
            return default;
        }

        public void OnWriteStarted() { }
        public void OnWriteCompleted() { }
        public void OnRecoveryCompleted() { }
    }

    private sealed class RecoveryStateObserver(Action recovered) : IJournaledStateObserver
    {
        public int RecoveryStartedCount { get; private set; }
        public int RecoveryCompletedCount { get; private set; }

        public void OnWriteStarted() { }
        public void OnWriteCompleted() { }
        public void OnRecoveryStarted() => RecoveryStartedCount++;
        public void OnRecoveryCompleted()
        {
            recovered();
            RecoveryCompletedCount++;
        }
    }

    private sealed class CapturingStorage : IJournalStorage
    {
        private readonly object _lock = new();
        private readonly List<byte[]> _segments = [];
        private int _activeAppends;

        public List<byte[]> Appends { get; } = [];

        public List<byte[]> Replaces { get; } = [];

        public List<byte[]> FailedReplaceAttempts { get; } = [];

        public List<string> OperationLog { get; } = [];

        public byte[] RecoverableBytes
        {
            get
            {
                lock (_lock)
                {
                    return _segments.SelectMany(static segment => segment).ToArray();
                }
            }
        }

        public bool BlockNextAppend { get; set; }

        public TaskCompletionSource AppendEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource BlockedAppendStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseAppend { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource DeleteEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool DeleteEnteredWhileAppendInProgress { get; private set; }

        public bool BlockNextReplace { get; set; }

        public TaskCompletionSource ReplaceEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseReplace { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool ConcatenateReads { get; set; }

        public int DeleteCount { get; private set; }

        public Exception? NextAppendException { get; set; }

        public Exception? NextPostAppendException { get; set; }

        public Exception? NextDeleteException { get; set; }

        public Exception? NextReadException { get; set; }

        public Exception? NextReplaceException { get; set; }

        public int ReplaceAttemptCount { get; private set; }

        public int ReadConsumeCount { get; private set; }

        public void ResetReadConsumeCount()
        {
            lock (_lock)
            {
                ReadConsumeCount = 0;
            }
        }

        public bool IsCompactionRequested { get; set; }

        public bool DelayReplace { get; set; }

        public TaskCompletionSource ReplaceStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowReplace { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask ReadAsync(IJournalStorageConsumer consumer, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(consumer);
            if (NextReadException is { } exception)
            {
                NextReadException = null;
                throw exception;
            }

            byte[][] segments;
            lock (_lock)
            {
                segments = _segments.ToArray();
            }

            if (ConcatenateReads)
            {
                var totalLength = 0;
                foreach (var segment in segments)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    totalLength += segment.Length;
                }

                if (totalLength > 0)
                {
                    var concatenated = new byte[totalLength];
                    var offset = 0;
                    foreach (var segment in segments)
                    {
                        segment.CopyTo(concatenated.AsSpan(offset));
                        offset += segment.Length;
                    }

                    lock (_lock)
                    {
                        ReadConsumeCount++;
                    }

                    consumer.Read(concatenated, metadata: null, complete: true);
                }
                else
                {
                    consumer.Complete(metadata: null);
                }

                return;
            }

            consumer.Read(GetSegments(), metadata: null, complete: true);

            IEnumerable<ReadOnlyMemory<byte>> GetSegments()
            {
                foreach (var segment in segments)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    lock (_lock)
                    {
                        ReadConsumeCount++;
                    }

                    yield return segment;
                }
            }
        }

        public async ValueTask ReplaceAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Exception? exceptionToThrow;
            lock (_lock)
            {
                ReplaceAttemptCount++;
                exceptionToThrow = NextReplaceException;
                if (exceptionToThrow is not null)
                {
                    NextReplaceException = null;
                    FailedReplaceAttempts.Add(value.ToArray());
                    OperationLog.Add("replace-failed");
                }
                else
                {
                    OperationLog.Add("replace");
                }
            }

            if (exceptionToThrow is not null)
            {
                ExceptionDispatchInfo.Throw(exceptionToThrow);
            }

            ReplaceEntered.TrySetResult();
            if (BlockNextReplace)
            {
                BlockNextReplace = false;
                await ReleaseReplace.Task.WaitAsync(cancellationToken);
            }

            if (DelayReplace)
            {
                ReplaceStarted.SetResult();
                await AllowReplace.Task.WaitAsync(cancellationToken);
            }

            var bytes = value.ToArray();
            lock (_lock)
            {
                Replaces.Add(bytes);
                _segments.Clear();
                _segments.Add(bytes);
            }
        }

        public async ValueTask AppendAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _activeAppends);
            try
            {
                Exception? exceptionToThrow;
                lock (_lock)
                {
                    exceptionToThrow = NextAppendException;
                    if (exceptionToThrow is not null)
                    {
                        NextAppendException = null;
                        OperationLog.Add("append-failed");
                    }
                    else
                    {
                        OperationLog.Add("append");
                    }
                }

                AppendEntered.TrySetResult();
                if (BlockNextAppend)
                {
                    BlockNextAppend = false;
                    BlockedAppendStarted.SetResult();
                    await ReleaseAppend.Task.WaitAsync(cancellationToken);
                }

                if (exceptionToThrow is not null)
                {
                    ExceptionDispatchInfo.Throw(exceptionToThrow);
                }

                var bytes = value.ToArray();
                lock (_lock)
                {
                    Appends.Add(bytes);
                    _segments.Add(bytes);
                }

                if (NextPostAppendException is { } postAppendException)
                {
                    NextPostAppendException = null;
                    throw postAppendException;
                }
            }
            finally
            {
                Interlocked.Decrement(ref _activeAppends);
            }
        }

        public ValueTask DeleteAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (NextDeleteException is { } exception)
            {
                NextDeleteException = null;
                throw exception;
            }

            lock (_lock)
            {
                DeleteEnteredWhileAppendInProgress = Volatile.Read(ref _activeAppends) > 0;
                OperationLog.Add("delete");
                DeleteCount++;
                _segments.Clear();
            }

            DeleteEntered.TrySetResult();
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

    private sealed class MutableReadStorage : IJournalStorage
    {
        private readonly object _lock = new();
        private readonly Queue<byte[]> _readSnapshots = [];
        private readonly int _blockedReadNumber;
        private byte[] _bytes;
        private int _readCount;

        public MutableReadStorage(params byte[][] readSnapshots) : this(blockedReadNumber: 0, readSnapshots)
        {
        }

        public MutableReadStorage(int blockedReadNumber, params byte[][] readSnapshots)
        {
            // Recovery retry tests model storage being repaired after a failed read.
            // The sequence makes that repair deterministic instead of racing the manager's retry.
            if (readSnapshots.Length == 0)
            {
                throw new ArgumentException("At least one read snapshot is required.", nameof(readSnapshots));
            }

            foreach (var readSnapshot in readSnapshots)
            {
                ArgumentNullException.ThrowIfNull(readSnapshot);
                _readSnapshots.Enqueue(readSnapshot.ToArray());
            }

            _blockedReadNumber = blockedReadNumber;
            _bytes = readSnapshots[^1].ToArray();
        }

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
                    _readSnapshots.Clear();
                    _bytes = value.ToArray();
                }
            }
        }

        public List<byte[]> Replaces { get; } = [];

        public List<string> OperationLog { get; } = [];

        public bool IsCompactionRequested { get; set; }

        public TaskCompletionSource BlockedReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowBlockedRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask ReadAsync(IJournalStorageConsumer consumer, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(consumer);
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _readCount) == _blockedReadNumber)
            {
                BlockedReadStarted.SetResult();
                await AllowBlockedRead.Task.WaitAsync(cancellationToken);
            }

            byte[] snapshot;
            lock (_lock)
            {
                if (_readSnapshots.TryDequeue(out var readSnapshot))
                {
                    _bytes = readSnapshot;
                    snapshot = readSnapshot.ToArray();
                }
                else
                {
                    snapshot = _bytes.ToArray();
                }
            }

            consumer.Read(snapshot, metadata: null, complete: true);
        }

        public ValueTask ReplaceAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = value.ToArray();
            OperationLog.Add("replace");
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
            OperationLog.Add("append");
            lock (_lock)
            {
                _bytes = [.. _bytes, .. appendBytes];
            }

            return default;
        }

        public ValueTask DeleteAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OperationLog.Add("delete");
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

        public int WriteCompletedCount { get; private set; }

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

        public void OnWriteCompleted() => WriteCompletedCount++;

        public IJournaledState DeepCopy() => throw new NotSupportedException();
    }

    private sealed class TrackingValueCodec<T>(IDurableValueCommandCodec<T> inner) : IDurableValueCommandCodec<T>
    {
        public List<T> AppliedValues { get; } = [];

        public void WriteSet(T value, JournalStreamWriter writer) => inner.WriteSet(value, writer);

        public void Apply(JournalBufferReader input, IDurableValueCommandHandler<T> consumer) =>
            inner.Apply(input, new TrackingHandler(this, consumer));

        private sealed class TrackingHandler(TrackingValueCodec<T> owner, IDurableValueCommandHandler<T> inner) : IDurableValueCommandHandler<T>
        {
            public void ApplySet(T value)
            {
                owner.AppliedValues.Add(value);
                inner.ApplySet(value);
            }
        }
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
