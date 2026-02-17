using System.Globalization;
using Orleans.Runtime;
using Orleans.Storage;
using Xunit;

namespace Orleans.Persistence.TestKit;

/// <summary>
/// Base class for testing IGrainStorage implementations.
/// </summary>
/// <remarks>
/// This class provides a comprehensive test suite for verifying that an IGrainStorage provider
/// conforms to the expected behavior. Inherit from this class and implement the abstract
/// methods to test your custom storage provider.
/// </remarks>
public abstract class GrainStorageTestRunner
{
    /// <summary>
    /// Initializes a new instance of the <see cref="GrainStorageTestRunner"/> class.
    /// </summary>
    /// <param name="storage">The storage provider to test.</param>
    protected GrainStorageTestRunner(IGrainStorage storage)
    {
        Storage = storage ?? throw new ArgumentNullException(nameof(storage));
    }

    /// <summary>
    /// Gets the storage provider under test.
    /// </summary>
    protected IGrainStorage Storage { get; }

    /// <summary>
    /// Creates a new grain ID and grain state pair for testing.
    /// </summary>
    /// <param name="grainId">The grain ID value.</param>
    /// <param name="version">The initial version of the state.</param>
    /// <returns>A grain ID and grain state pair.</returns>
    protected (GrainId GrainId, GrainState<TestState1> GrainState) GetTestReferenceAndState(long grainId, string version)
    {
        var id = GrainId.Create(GrainType.Create("my-grain-type"), GrainIdKeyExtensions.CreateIntegerKey(grainId));
        var grainState = new GrainState<TestState1> { State = new TestState1(), ETag = version };
        return (id, grainState);
    }

    /// <summary>
    /// Creates a new grain ID and grain state pair for testing.
    /// </summary>
    /// <param name="grainId">The grain ID value.</param>
    /// <param name="version">The initial version of the state.</param>
    /// <returns>A grain ID and grain state pair.</returns>
    protected (GrainId GrainId, GrainState<TestState1> GrainState) GetTestReferenceAndState(string grainId, string version)
    {
        var id = GrainId.Create("my-grain-type", grainId);
        var grainState = new GrainState<TestState1> { State = new TestState1(), ETag = version };
        return (id, grainState);
    }

    /// <summary>
    /// Writes to storage and reads back, asserting both the version and the state.
    /// </summary>
    /// <typeparam name="T">The grain state type.</typeparam>
    /// <param name="grainTypeName">The type of the grain.</param>
    /// <param name="grainId">The grain ID.</param>
    /// <param name="grainState">The grain state to write.</param>
    protected async Task Store_WriteRead<T>(string grainTypeName, GrainId grainId, GrainState<T> grainState) where T : new()
    {
        await Storage.WriteStateAsync(grainTypeName, grainId, grainState).ConfigureAwait(false);
        var storedGrainState = new GrainState<T> { State = new T() };
        await Storage.ReadStateAsync(grainTypeName, grainId, storedGrainState).ConfigureAwait(false);

        Assert.Equal(grainState.ETag, storedGrainState.ETag);
        Assert.Equal(grainState.State, storedGrainState.State);
        Assert.True(storedGrainState.RecordExists);
    }

    /// <summary>
    /// Writes to storage, clears and reads back, asserting both the version and the state.
    /// </summary>
    /// <typeparam name="T">The grain state type.</typeparam>
    /// <param name="grainTypeName">The type of the grain.</param>
    /// <param name="grainId">The grain ID.</param>
    /// <param name="grainState">The grain state to write and clear.</param>
    protected async Task Store_WriteClearRead<T>(string grainTypeName, GrainId grainId, GrainState<T> grainState) where T : new()
    {
        // A legal situation for clearing has to be arranged by writing a state to the storage before clearing it.
        // Writing and clearing both change the ETag, so they should differ.
        await Storage.WriteStateAsync(grainTypeName, grainId, grainState);
        var writtenStateVersion = grainState.ETag;
        var recordExitsAfterWriting = grainState.RecordExists;
        Assert.True(recordExitsAfterWriting);

        await Storage.ClearStateAsync(grainTypeName, grainId, grainState).ConfigureAwait(false);
        var clearedStateVersion = grainState.ETag;
        Assert.NotEqual(writtenStateVersion, clearedStateVersion);

        var recordExitsAfterClearing = grainState.RecordExists;
        Assert.False(recordExitsAfterClearing);

        var storedGrainState = new GrainState<T> { State = new T(), ETag = clearedStateVersion };
        await Storage.WriteStateAsync(grainTypeName, grainId, storedGrainState).ConfigureAwait(false);
        Assert.Equal(storedGrainState.State, Activator.CreateInstance<T>());
        Assert.True(storedGrainState.RecordExists);
    }

    /// <summary>
    /// Tests basic read and write operations.
    /// </summary>
    public virtual async Task PersistenceStorage_WriteReadIdCyrillic()
    {
        var grainTypeName = "TestGrain";
        var grainReference = GetTestReferenceAndState(0, null);
        var grainState = grainReference.GrainState;
        await Storage.WriteStateAsync(grainTypeName, grainReference.GrainId, grainState).ConfigureAwait(false);
        var storedGrainState = new GrainState<TestState1> { State = new TestState1() };
        await Storage.ReadStateAsync(grainTypeName, grainReference.GrainId, storedGrainState).ConfigureAwait(false);

        Assert.Equal(grainState.ETag, storedGrainState.ETag);
        Assert.Equal(grainState.State, storedGrainState.State);
    }

    /// <summary>
    /// Writes to storage and tries to re-write the same state with NULL as ETag, as if the grain was just created.
    /// </summary>
    /// <returns>The InconsistentStateException thrown by the provider.</returns>
    public virtual async Task<InconsistentStateException> PersistenceStorage_WriteDuplicateFailsWithInconsistentStateException()
    {
        // A grain with a random ID will be arranged to the database. Then its state is set to null to simulate
        // the fact it is like a second activation after one that has succeeded to write.
        string grainTypeName = "TestGrain";
        var (grainId, grainState) = GetTestReferenceAndState(Random.Shared.NextInt64(), null);

        await Store_WriteRead(grainTypeName, grainId, grainState).ConfigureAwait(false);
        grainState.ETag = null;
        var exception = await Record.ExceptionAsync(() => Store_WriteRead(grainTypeName, grainId, grainState)).ConfigureAwait(false);

        Assert.NotNull(exception);
        Assert.IsType<InconsistentStateException>(exception);

        return (InconsistentStateException)exception;
    }

    /// <summary>
    /// Writes a known inconsistent state to the storage and asserts an exception will be thrown.
    /// </summary>
    /// <returns>The InconsistentStateException thrown by the provider.</returns>
    public virtual async Task<InconsistentStateException> PersistenceStorage_WriteInconsistentFailsWithInconsistentStateException()
    {
        // Some version not expected to be in the storage for this type and ID.
        var inconsistentStateVersion = Random.Shared.Next().ToString(CultureInfo.InvariantCulture);

        var inconsistentState = GetTestReferenceAndState(Random.Shared.NextInt64(), inconsistentStateVersion);
        string grainTypeName = "TestGrain";
        var exception = await Record.ExceptionAsync(() => Store_WriteRead(grainTypeName, inconsistentState.GrainId, inconsistentState.GrainState)).ConfigureAwait(false);

        Assert.NotNull(exception);
        Assert.IsType<InconsistentStateException>(exception);

        return (InconsistentStateException)exception;
    }

    /// <summary>
    /// Tests parallel writes and reads to ensure the provider handles concurrency correctly.
    /// </summary>
    /// <param name="prefix">Prefix for grain IDs.</param>
    /// <param name="countOfGrains">Number of grains to test with.</param>
    public virtual async Task PersistenceStorage_WriteReadWriteReadStatesInParallel(string prefix = "Parallel", int countOfGrains = 100)
    {
        // As data is written and read the Version numbers (ETags) are checked for correctness (they change).
        // Additionally the Store_WriteRead tests do their validation.
        var grainTypeName = "TestGrain";
        int StartOfRange = 33900;
        int CountOfRange = countOfGrains;

        // Since the version is NULL, storage provider tries to insert this data as new state.
        // If there is already data with this class, the writing fails and the storage provider throws.
        var grainStates = Enumerable.Range(StartOfRange, CountOfRange)
            .Select(i => GetTestReferenceAndState($"{prefix}-{Guid.NewGuid():N}-{i}", null))
            .ToList();

        // Avoid parallelization of the first write to not stress out the system with deadlocks on INSERT
        foreach (var grainData in grainStates)
        {
            // A sanity checker that the first version really has null as its state. Then it is stored
            // to the database and a new version is acquired.
            var firstVersion = grainData.GrainState.ETag;
            Assert.Null(firstVersion);

            await Store_WriteRead(grainTypeName, grainData.GrainId, grainData.GrainState).ConfigureAwait(false);
            var secondVersion = grainData.GrainState.ETag;
            Assert.NotEqual(firstVersion, secondVersion);
        }

        int MaxNumberOfThreads = Environment.ProcessorCount * 3;

        await Parallel.ForEachAsync(grainStates, new ParallelOptions { MaxDegreeOfParallelism = MaxNumberOfThreads }, async (grainData, ct) =>
        {
            // This loop writes the state consecutive times to the database to make sure its version is updated appropriately.
            for (int k = 0; k < 10; ++k)
            {
                var versionBefore = grainData.GrainState.ETag;
                await Store_WriteRead(grainTypeName, grainData.GrainId, grainData.GrainState);
                var versionAfter = grainData.GrainState.ETag;
                Assert.NotEqual(versionBefore, versionAfter);
            }
        });
    }

    /// <summary>
    /// Tests that reading a non-existent state returns RecordExists=false.
    /// </summary>
    public virtual async Task PersistenceStorage_ReadNonExistentState()
    {
        var grainTypeName = "TestGrain";
        var (grainId, grainState) = GetTestReferenceAndState(Random.Shared.NextInt64(), null);

        await Storage.ReadStateAsync(grainTypeName, grainId, grainState).ConfigureAwait(false);

        Assert.False(grainState.RecordExists);
        Assert.Null(grainState.ETag);
    }

    /// <summary>
    /// Tests write, clear, and write cycle to ensure state can be re-written after clearing.
    /// </summary>
    public virtual async Task PersistenceStorage_WriteClearWrite()
    {
        var grainTypeName = "TestGrain";
        var (grainId, grainState) = GetTestReferenceAndState(Random.Shared.NextInt64(), null);
        grainState.State.A = "First";
        grainState.State.B = 1;

        await Storage.WriteStateAsync(grainTypeName, grainId, grainState).ConfigureAwait(false);
        var firstETag = grainState.ETag;
        Assert.NotNull(firstETag);
        Assert.True(grainState.RecordExists);

        await Storage.ClearStateAsync(grainTypeName, grainId, grainState).ConfigureAwait(false);
        var clearedETag = grainState.ETag;
        Assert.NotEqual(firstETag, clearedETag);
        Assert.False(grainState.RecordExists);

        grainState.State = new TestState1 { A = "Second", B = 2 };
        await Storage.WriteStateAsync(grainTypeName, grainId, grainState).ConfigureAwait(false);
        var secondETag = grainState.ETag;
        Assert.NotEqual(clearedETag, secondETag);
        Assert.True(grainState.RecordExists);

        var readState = new GrainState<TestState1> { State = new TestState1() };
        await Storage.ReadStateAsync(grainTypeName, grainId, readState).ConfigureAwait(false);
        Assert.Equal("Second", readState.State.A);
        Assert.Equal(2, readState.State.B);
    }

    /// <summary>
    /// Tests the full write-clear-read cycle including verification that state is properly initialized after clear.
    /// This test verifies that after clearing, a new write creates a fresh state that can be read back.
    /// </summary>
    public virtual async Task PersistenceStorage_WriteClearRead()
    {
        var grainTypeName = "TestGrain";
        var (grainId, grainState) = GetTestReferenceAndState(Random.Shared.NextInt64(), null);
        grainState.State.A = "Original";
        grainState.State.B = 42;
        grainState.State.C = 100;

        // Write initial state
        await Storage.WriteStateAsync(grainTypeName, grainId, grainState);
        var writtenStateVersion = grainState.ETag;
        Assert.True(grainState.RecordExists);

        // Clear the state
        await Storage.ClearStateAsync(grainTypeName, grainId, grainState).ConfigureAwait(false);
        var clearedStateVersion = grainState.ETag;
        Assert.NotEqual(writtenStateVersion, clearedStateVersion);
        Assert.False(grainState.RecordExists);

        // Write new state after clear
        var newState = new GrainState<TestState1> { State = new TestState1(), ETag = clearedStateVersion };
        await Storage.WriteStateAsync(grainTypeName, grainId, newState).ConfigureAwait(false);
        Assert.Equal(newState.State, Activator.CreateInstance<TestState1>());
        Assert.True(newState.RecordExists);

        // Read back and verify it's the default state
        var readBackState = new GrainState<TestState1> { State = new TestState1() };
        await Storage.ReadStateAsync(grainTypeName, grainId, readBackState).ConfigureAwait(false);
        Assert.True(readBackState.RecordExists);
        Assert.Equal(default(string), readBackState.State.A);
        Assert.Equal(0, readBackState.State.B);
        Assert.Equal(0L, readBackState.State.C);
    }

    /// <summary>
    /// Tests storage operations with string-based grain keys.
    /// </summary>
    public virtual async Task PersistenceStorage_WriteRead_StringKey()
    {
        var grainTypeName = "TestGrain";
        var stringKey = $"StringKey-{Guid.NewGuid()}";
        var (grainId, grainState) = GetTestReferenceAndState(stringKey, null);
        grainState.State.A = "TestString";
        grainState.State.B = 123;

        await Store_WriteRead(grainTypeName, grainId, grainState).ConfigureAwait(false);

        // Verify we can read it back with the same key
        var readState = new GrainState<TestState1> { State = new TestState1() };
        await Storage.ReadStateAsync(grainTypeName, grainId, readState).ConfigureAwait(false);
        Assert.Equal("TestString", readState.State.A);
        Assert.Equal(123, readState.State.B);
        Assert.True(readState.RecordExists);
    }

    /// <summary>
    /// Tests storage operations with integer-based grain keys.
    /// </summary>
    public virtual async Task PersistenceStorage_WriteRead_IntegerKey()
    {
        var grainTypeName = "TestGrain";
        var integerKey = Random.Shared.NextInt64();
        var (grainId, grainState) = GetTestReferenceAndState(integerKey, null);
        grainState.State.A = "TestInteger";
        grainState.State.B = 456;
        grainState.State.C = integerKey;

        await Store_WriteRead(grainTypeName, grainId, grainState).ConfigureAwait(false);

        // Verify we can read it back with the same key
        var readState = new GrainState<TestState1> { State = new TestState1() };
        await Storage.ReadStateAsync(grainTypeName, grainId, readState).ConfigureAwait(false);
        Assert.Equal("TestInteger", readState.State.A);
        Assert.Equal(456, readState.State.B);
        Assert.Equal(integerKey, readState.State.C);
        Assert.True(readState.RecordExists);
    }

    /// <summary>
    /// Tests that ETag updates properly on successive writes.
    /// </summary>
    public virtual async Task PersistenceStorage_ETagChangesOnWrite()
    {
        var grainTypeName = "TestGrain";
        var (grainId, grainState) = GetTestReferenceAndState(Random.Shared.NextInt64(), null);
        
        // First write
        grainState.State.A = "Version1";
        await Storage.WriteStateAsync(grainTypeName, grainId, grainState).ConfigureAwait(false);
        var etag1 = grainState.ETag;
        Assert.NotNull(etag1);

        // Second write
        grainState.State.A = "Version2";
        await Storage.WriteStateAsync(grainTypeName, grainId, grainState).ConfigureAwait(false);
        var etag2 = grainState.ETag;
        Assert.NotNull(etag2);
        Assert.NotEqual(etag1, etag2);

        // Third write
        grainState.State.A = "Version3";
        await Storage.WriteStateAsync(grainTypeName, grainId, grainState).ConfigureAwait(false);
        var etag3 = grainState.ETag;
        Assert.NotNull(etag3);
        Assert.NotEqual(etag2, etag3);
        Assert.NotEqual(etag1, etag3);
    }

    /// <summary>
    /// Tests that calling Clear before any write works correctly.
    /// Verifies that RecordExists is false and State is not null after clearing non-existent state.
    /// </summary>
    public virtual async Task PersistenceStorage_ClearBeforeWrite()
    {
        var grainTypeName = "TestGrain";
        var (grainId, grainState) = GetTestReferenceAndState(Random.Shared.NextInt64(), null);

        // Clear state that was never written
        await Storage.ClearStateAsync(grainTypeName, grainId, grainState).ConfigureAwait(false);

        // State should still be initialized (not null), but record shouldn't exist
        Assert.NotNull(grainState.State);
        Assert.False(grainState.RecordExists);
    }

    /// <summary>
    /// Tests that State property is never null after Clear operation.
    /// Verifies the State object remains initialized even after clearing.
    /// </summary>
    public virtual async Task PersistenceStorage_ClearStateDoesNotNullifyState()
    {
        var grainTypeName = "TestGrain";
        var (grainId, grainState) = GetTestReferenceAndState(Random.Shared.NextInt64(), null);
        grainState.State.A = "TestData";
        grainState.State.B = 100;

        // Write and then clear
        await Storage.WriteStateAsync(grainTypeName, grainId, grainState).ConfigureAwait(false);
        Assert.True(grainState.RecordExists);
        Assert.NotNull(grainState.State);

        await Storage.ClearStateAsync(grainTypeName, grainId, grainState).ConfigureAwait(false);
        
        // State object should not be null even after clear
        Assert.NotNull(grainState.State);
        Assert.False(grainState.RecordExists);
    }

    /// <summary>
    /// Tests that ETag changes after Clear operation.
    /// Some providers may set ETag to null on clear, others may update it - both are acceptable.
    /// </summary>
    public virtual async Task PersistenceStorage_ClearUpdatesETag()
    {
        var grainTypeName = "TestGrain";
        var (grainId, grainState) = GetTestReferenceAndState(Random.Shared.NextInt64(), null);
        grainState.State.A = "TestData";

        // Write state
        await Storage.WriteStateAsync(grainTypeName, grainId, grainState).ConfigureAwait(false);
        var writeETag = grainState.ETag;
        Assert.NotNull(writeETag);

        // Clear state
        await Storage.ClearStateAsync(grainTypeName, grainId, grainState).ConfigureAwait(false);
        var clearETag = grainState.ETag;

        // ETag should have changed (either to null or a new value)
        Assert.NotEqual(writeETag, clearETag);
        Assert.False(grainState.RecordExists);
    }

    /// <summary>
    /// Tests reading state after it has been cleared.
    /// Verifies that reading a cleared state returns RecordExists=false and State is not null.
    /// </summary>
    public virtual async Task PersistenceStorage_ReadAfterClear()
    {
        var grainTypeName = "TestGrain";
        var (grainId, grainState) = GetTestReferenceAndState(Random.Shared.NextInt64(), null);
        grainState.State.A = "OriginalData";
        grainState.State.B = 42;

        // Write, clear, then read
        await Storage.WriteStateAsync(grainTypeName, grainId, grainState).ConfigureAwait(false);
        await Storage.ClearStateAsync(grainTypeName, grainId, grainState).ConfigureAwait(false);

        // Read the cleared state
        var readState = new GrainState<TestState1> { State = new TestState1() };
        await Storage.ReadStateAsync(grainTypeName, grainId, readState).ConfigureAwait(false);

        // After reading a cleared state
        Assert.False(readState.RecordExists);
        Assert.NotNull(readState.State); // State should still be initialized
    }

    /// <summary>
    /// Tests multiple successive clear operations.
    /// Verifies that clearing an already-cleared state is idempotent.
    /// </summary>
    public virtual async Task PersistenceStorage_MultipleClearOperations()
    {
        var grainTypeName = "TestGrain";
        var (grainId, grainState) = GetTestReferenceAndState(Random.Shared.NextInt64(), null);
        grainState.State.A = "TestData";

        // Write state
        await Storage.WriteStateAsync(grainTypeName, grainId, grainState).ConfigureAwait(false);
        Assert.True(grainState.RecordExists);

        // First clear
        await Storage.ClearStateAsync(grainTypeName, grainId, grainState).ConfigureAwait(false);
        var firstClearETag = grainState.ETag;
        Assert.False(grainState.RecordExists);

        // Second clear - should be idempotent
        await Storage.ClearStateAsync(grainTypeName, grainId, grainState).ConfigureAwait(false);
        Assert.False(grainState.RecordExists);
        Assert.NotNull(grainState.State);
    }

    /// <summary>
    /// Tests that State property remains initialized (not null) after reading non-existent state.
    /// </summary>
    public virtual async Task PersistenceStorage_ReadNonExistentStateHasNonNullState()
    {
        var grainTypeName = "TestGrain";
        var (grainId, grainState) = GetTestReferenceAndState(Random.Shared.NextInt64(), null);

        // Read state that was never written
        await Storage.ReadStateAsync(grainTypeName, grainId, grainState).ConfigureAwait(false);

        // State should be initialized even though nothing was written
        Assert.False(grainState.RecordExists);
        Assert.NotNull(grainState.State);
        Assert.Null(grainState.ETag);
    }

    /// <summary>
    /// Tests write-read-clear-read cycle to verify state transitions.
    /// </summary>
    public virtual async Task PersistenceStorage_WriteReadClearReadCycle()
    {
        var grainTypeName = "TestGrain";
        var (grainId, grainState) = GetTestReferenceAndState(Random.Shared.NextInt64(), null);
        grainState.State.A = "InitialValue";
        grainState.State.B = 99;

        // Write
        await Storage.WriteStateAsync(grainTypeName, grainId, grainState).ConfigureAwait(false);
        var writeETag = grainState.ETag;
        Assert.True(grainState.RecordExists);

        // Read back
        var readState1 = new GrainState<TestState1> { State = new TestState1() };
        await Storage.ReadStateAsync(grainTypeName, grainId, readState1).ConfigureAwait(false);
        Assert.True(readState1.RecordExists);
        Assert.Equal("InitialValue", readState1.State.A);
        Assert.Equal(99, readState1.State.B);
        Assert.Equal(writeETag, readState1.ETag);

        // Clear
        await Storage.ClearStateAsync(grainTypeName, grainId, grainState).ConfigureAwait(false);
        Assert.False(grainState.RecordExists);

        // Read after clear
        var readState2 = new GrainState<TestState1> { State = new TestState1() };
        await Storage.ReadStateAsync(grainTypeName, grainId, readState2).ConfigureAwait(false);
        Assert.False(readState2.RecordExists);
        Assert.NotNull(readState2.State);
    }

    /// <summary>
    /// Tests that updating state with the same values still updates the ETag.
    /// </summary>
    public virtual async Task PersistenceStorage_WriteWithSameValuesUpdatesETag()
    {
        var grainTypeName = "TestGrain";
        var (grainId, grainState) = GetTestReferenceAndState(Random.Shared.NextInt64(), null);
        grainState.State.A = "SameValue";
        grainState.State.B = 123;

        // First write
        await Storage.WriteStateAsync(grainTypeName, grainId, grainState).ConfigureAwait(false);
        var etag1 = grainState.ETag;
        Assert.NotNull(etag1);

        // Write again with same values
        grainState.State.A = "SameValue";
        grainState.State.B = 123;
        await Storage.WriteStateAsync(grainTypeName, grainId, grainState).ConfigureAwait(false);
        var etag2 = grainState.ETag;
        Assert.NotNull(etag2);

        // ETag should still change even though values are the same
        Assert.NotEqual(etag1, etag2);
    }
}

