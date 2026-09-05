using Orleans.Runtime;
using Orleans.Storage;
using Assert = Orleans.Persistence.TestKit.StorageTestAssertions;
using Record = Orleans.Persistence.TestKit.StorageTestExecution;

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
    protected (GrainId GrainId, GrainState<TestState1> GrainState) GetTestReferenceAndState(long grainId, string? version)
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
    protected (GrainId GrainId, GrainState<TestState1> GrainState) GetTestReferenceAndState(string grainId, string? version)
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
    protected Task Store_WriteRead<T>(string grainTypeName, GrainId grainId, GrainState<T> grainState) where T : new() =>
        Store_WriteRead(grainTypeName, grainId, grainState, CancellationToken.None);

    /// <inheritdoc cref="Store_WriteRead{T}(string, GrainId, GrainState{T})"/>
    /// <param name="cancellationToken">A token which cancels the storage operations.</param>
    /// <exception cref="ArgumentNullException"><paramref name="grainState"/> is <see langword="null"/>.</exception>
    protected async Task Store_WriteRead<T>(
        string grainTypeName,
        GrainId grainId,
        GrainState<T> grainState,
        CancellationToken cancellationToken)
        where T : new()
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(grainState);

        await Storage.WriteStateAsync(grainTypeName, grainId, grainState, cancellationToken).ConfigureAwait(false);
        var storedGrainState = new GrainState<T> { State = new T() };
        await Storage.ReadStateAsync(grainTypeName, grainId, storedGrainState, cancellationToken).ConfigureAwait(false);

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
    protected Task Store_WriteClearRead<T>(string grainTypeName, GrainId grainId, GrainState<T> grainState) where T : new() =>
        Store_WriteClearRead(grainTypeName, grainId, grainState, CancellationToken.None);

    /// <inheritdoc cref="Store_WriteClearRead{T}(string, GrainId, GrainState{T})"/>
    /// <param name="cancellationToken">A token which cancels the storage operations.</param>
    /// <exception cref="ArgumentNullException"><paramref name="grainState"/> is <see langword="null"/>.</exception>
    protected async Task Store_WriteClearRead<T>(
        string grainTypeName,
        GrainId grainId,
        GrainState<T> grainState,
        CancellationToken cancellationToken)
        where T : new()
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(grainState);

        // A legal situation for clearing has to be arranged by writing a state to the storage before clearing it.
        // Writing and clearing both change the ETag, so they should differ.
        await Storage.WriteStateAsync(grainTypeName, grainId, grainState, cancellationToken);
        var writtenStateVersion = grainState.ETag;
        var recordExitsAfterWriting = grainState.RecordExists;
        Assert.True(recordExitsAfterWriting);

        await Storage.ClearStateAsync(grainTypeName, grainId, grainState, cancellationToken).ConfigureAwait(false);
        var clearedStateVersion = grainState.ETag;
        Assert.NotEqual(writtenStateVersion, clearedStateVersion);

        var recordExitsAfterClearing = grainState.RecordExists;
        Assert.False(recordExitsAfterClearing);

        var storedGrainState = new GrainState<T> { State = new T(), ETag = clearedStateVersion };
        await Storage.WriteStateAsync(grainTypeName, grainId, storedGrainState, cancellationToken).ConfigureAwait(false);
        Assert.Equal(storedGrainState.State, Activator.CreateInstance<T>());
        Assert.True(storedGrainState.RecordExists);
    }

    /// <summary>
    /// Tests basic read and write operations.
    /// </summary>
    public virtual Task PersistenceStorage_WriteReadIdCyrillic() =>
        PersistenceStorage_WriteReadIdCyrillicAsync(CancellationToken.None);

    /// <inheritdoc cref="PersistenceStorage_WriteReadIdCyrillic()"/>
    /// <param name="cancellationToken">A token which cancels the storage operations.</param>
    public virtual async Task PersistenceStorage_WriteReadIdCyrillicAsync(CancellationToken cancellationToken)
    {
        const string CyrillicValue = "\u041f\u0440\u0438\u0432\u0435\u0442, \u043c\u0438\u0440";
        var grainTypeName = "TestGrain";
        var grainReference = GetTestReferenceAndState(0, null);
        var grainState = grainReference.GrainState;
        var state = Assert.IsType<TestState1>(grainState.State);
        state.A = CyrillicValue;
        await Storage.WriteStateAsync(grainTypeName, grainReference.GrainId, grainState, cancellationToken).ConfigureAwait(false);
        var storedGrainState = new GrainState<TestState1> { State = new TestState1() };
        await Storage.ReadStateAsync(grainTypeName, grainReference.GrainId, storedGrainState, cancellationToken).ConfigureAwait(false);
        var storedState = Assert.IsType<TestState1>(storedGrainState.State);

        Assert.Equal(grainState.ETag, storedGrainState.ETag);
        Assert.Equal(state, storedState);
        Assert.Equal(CyrillicValue, storedState.A);
    }

    /// <summary>
    /// Writes to storage and tries to re-write the same state with NULL as ETag, as if the grain was just created.
    /// </summary>
    public virtual Task PersistenceStorage_WriteDuplicateFailsWithInconsistentStateException() =>
        PersistenceStorage_WriteDuplicateFailsWithInconsistentStateExceptionAsync(CancellationToken.None);

    /// <inheritdoc cref="PersistenceStorage_WriteDuplicateFailsWithInconsistentStateException()"/>
    /// <param name="cancellationToken">A token which cancels the storage operations.</param>
    public virtual async Task PersistenceStorage_WriteDuplicateFailsWithInconsistentStateExceptionAsync(
        CancellationToken cancellationToken)
    {
        // A grain with a random ID will be arranged to the database. Then its state is set to null to simulate
        // the fact it is like a second activation after one that has succeeded to write.
        string grainTypeName = "TestGrain";
        var (grainId, grainState) = GetTestReferenceAndState(Random.Shared.NextInt64(), null);

        await Store_WriteRead(grainTypeName, grainId, grainState, cancellationToken).ConfigureAwait(false);
        grainState.ETag = null;
        var exception = await Record.ExceptionAsync(
            () => Store_WriteRead(grainTypeName, grainId, grainState, cancellationToken)).ConfigureAwait(false);

        Assert.NotNull(exception);
        Assert.IsAssignableFrom<InconsistentStateException>(exception);
    }

    /// <summary>
    /// Writes a known inconsistent state to the storage and asserts an exception will be thrown.
    /// </summary>
    public virtual Task PersistenceStorage_WriteInconsistentFailsWithInconsistentStateException() =>
        PersistenceStorage_WriteInconsistentFailsWithInconsistentStateExceptionAsync(CancellationToken.None);

    /// <inheritdoc cref="PersistenceStorage_WriteInconsistentFailsWithInconsistentStateException()"/>
    /// <param name="cancellationToken">A token which cancels the storage operations.</param>
    public virtual async Task PersistenceStorage_WriteInconsistentFailsWithInconsistentStateExceptionAsync(
        CancellationToken cancellationToken)
    {
        var grainTypeName = "TestGrain";
        var (grainId, grainState) = GetTestReferenceAndState(Random.Shared.NextInt64(), null);
        grainState.State = new TestState1 { A = "initial", B = 1, C = 2 };
        await Storage.WriteStateAsync(grainTypeName, grainId, grainState, cancellationToken).ConfigureAwait(false);
        var staleETag = grainState.ETag;

        grainState.State = new TestState1 { A = "latest", B = 3, C = 4 };
        await Storage.WriteStateAsync(grainTypeName, grainId, grainState, cancellationToken).ConfigureAwait(false);

        var staleState = new GrainState<TestState1>
        {
            State = new TestState1 { A = "stale", B = 5, C = 6 },
            ETag = staleETag,
            RecordExists = true,
        };

        var exception = await Record.ExceptionAsync(
            () => Storage.WriteStateAsync(grainTypeName, grainId, staleState, cancellationToken)).ConfigureAwait(false);
        Assert.IsAssignableFrom<InconsistentStateException>(exception);

        var readState = new GrainState<TestState1> { State = new TestState1() };
        await Storage.ReadStateAsync(grainTypeName, grainId, readState, cancellationToken).ConfigureAwait(false);
        Assert.True(readState.RecordExists);
        Assert.Equal(grainState.State, readState.State);
        Assert.Equal(grainState.ETag, readState.ETag);
    }

    /// <summary>
    /// Tests parallel writes and reads to ensure the provider handles concurrency correctly.
    /// </summary>
    public virtual Task PersistenceStorage_WriteReadWriteReadStatesInParallel() =>
        PersistenceStorage_WriteReadWriteReadStatesInParallelAsync(CancellationToken.None);

    /// <inheritdoc cref="PersistenceStorage_WriteReadWriteReadStatesInParallel()"/>
    /// <param name="cancellationToken">A token which cancels the storage operations.</param>
    public virtual Task PersistenceStorage_WriteReadWriteReadStatesInParallelAsync(
        CancellationToken cancellationToken)
    {
        return RunPersistenceStorage_WriteReadWriteReadStatesInParallel("Parallel", 100, cancellationToken);
    }

    /// <summary>
    /// Tests parallel writes and reads using the supplied grain ID prefix and grain count.
    /// </summary>
    /// <param name="prefix">Prefix for grain IDs.</param>
    /// <param name="countOfGrains">Number of grains to test with.</param>
    protected Task RunPersistenceStorage_WriteReadWriteReadStatesInParallel(string prefix, int countOfGrains) =>
        RunPersistenceStorage_WriteReadWriteReadStatesInParallel(prefix, countOfGrains, CancellationToken.None);

    /// <inheritdoc cref="RunPersistenceStorage_WriteReadWriteReadStatesInParallel(string, int)"/>
    /// <param name="cancellationToken">A token which cancels the storage operations.</param>
    protected async Task RunPersistenceStorage_WriteReadWriteReadStatesInParallel(
        string prefix,
        int countOfGrains,
        CancellationToken cancellationToken)
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

            await Store_WriteRead(
                grainTypeName,
                grainData.GrainId,
                grainData.GrainState,
                cancellationToken).ConfigureAwait(false);
            var secondVersion = grainData.GrainState.ETag;
            Assert.NotEqual(firstVersion, secondVersion);
        }

        int MaxNumberOfThreads = Environment.ProcessorCount * 3;

        await Parallel.ForEachAsync(
            grainStates,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = MaxNumberOfThreads,
                CancellationToken = cancellationToken,
            },
            async (grainData, ct) =>
        {
            // This loop writes the state consecutive times to the database to make sure its version is updated appropriately.
            for (int k = 0; k < 10; ++k)
            {
                var versionBefore = grainData.GrainState.ETag;
                await Store_WriteRead(grainTypeName, grainData.GrainId, grainData.GrainState, ct);
                var versionAfter = grainData.GrainState.ETag;
                Assert.NotEqual(versionBefore, versionAfter);
            }
        });
    }

    /// <summary>
    /// Tests that reading a non-existent state returns RecordExists=false.
    /// </summary>
    public virtual Task PersistenceStorage_ReadNonExistentState() =>
        PersistenceStorage_ReadNonExistentStateAsync(CancellationToken.None);

    /// <inheritdoc cref="PersistenceStorage_ReadNonExistentState()"/>
    /// <param name="cancellationToken">A token which cancels the storage operations.</param>
    public virtual async Task PersistenceStorage_ReadNonExistentStateAsync(CancellationToken cancellationToken)
    {
        var grainTypeName = "TestGrain";
        var (grainId, grainState) = GetTestReferenceAndState(Random.Shared.NextInt64(), null);
        grainState.State = new TestState1 { A = "stale", B = 1, C = 2 };
        grainState.RecordExists = true;

        await Storage.ReadStateAsync(grainTypeName, grainId, grainState, cancellationToken).ConfigureAwait(false);

        Assert.False(grainState.RecordExists);
        Assert.Null(grainState.ETag);
        Assert.Equal(new TestState1(), grainState.State);
    }

    /// <summary>
    /// Tests write, clear, and write cycle to ensure state can be re-written after clearing.
    /// </summary>
    public virtual Task PersistenceStorage_WriteClearWrite() =>
        PersistenceStorage_WriteClearWriteAsync(CancellationToken.None);

    /// <inheritdoc cref="PersistenceStorage_WriteClearWrite()"/>
    /// <param name="cancellationToken">A token which cancels the storage operations.</param>
    public virtual async Task PersistenceStorage_WriteClearWriteAsync(CancellationToken cancellationToken)
    {
        var grainTypeName = "TestGrain";
        var (grainId, grainState) = GetTestReferenceAndState(Random.Shared.NextInt64(), null);
        var state = Assert.IsType<TestState1>(grainState.State);
        state.A = "First";
        state.B = 1;

        await Storage.WriteStateAsync(grainTypeName, grainId, grainState, cancellationToken).ConfigureAwait(false);
        var firstETag = grainState.ETag;
        Assert.NotNull(firstETag);
        Assert.True(grainState.RecordExists);

        await Storage.ClearStateAsync(grainTypeName, grainId, grainState, cancellationToken).ConfigureAwait(false);
        var clearedETag = grainState.ETag;
        Assert.NotEqual(firstETag, clearedETag);
        Assert.False(grainState.RecordExists);

        grainState.State = new TestState1 { A = "Second", B = 2 };
        await Storage.WriteStateAsync(grainTypeName, grainId, grainState, cancellationToken).ConfigureAwait(false);
        var secondETag = grainState.ETag;
        Assert.NotEqual(clearedETag, secondETag);
        Assert.True(grainState.RecordExists);

        var readState = new GrainState<TestState1> { State = new TestState1() };
        await Storage.ReadStateAsync(grainTypeName, grainId, readState, cancellationToken).ConfigureAwait(false);
        var readStateValue = Assert.IsType<TestState1>(readState.State);
        Assert.Equal("Second", readStateValue.A);
        Assert.Equal(2, readStateValue.B);
    }

    /// <summary>
    /// Tests the full write-clear-read cycle including verification that state is properly initialized after clear.
    /// This test verifies that after clearing, a new write creates a fresh state that can be read back.
    /// </summary>
    public virtual Task PersistenceStorage_WriteClearRead() =>
        PersistenceStorage_WriteClearReadAsync(CancellationToken.None);

    /// <inheritdoc cref="PersistenceStorage_WriteClearRead()"/>
    /// <param name="cancellationToken">A token which cancels the storage operations.</param>
    public virtual async Task PersistenceStorage_WriteClearReadAsync(CancellationToken cancellationToken)
    {
        var grainTypeName = "TestGrain";
        var (grainId, grainState) = GetTestReferenceAndState(Random.Shared.NextInt64(), null);
        var state = Assert.IsType<TestState1>(grainState.State);
        state.A = "Original";
        state.B = 42;
        state.C = 100;

        // Write initial state
        await Storage.WriteStateAsync(grainTypeName, grainId, grainState, cancellationToken);
        var writtenStateVersion = grainState.ETag;
        Assert.True(grainState.RecordExists);

        // Clear the state
        await Storage.ClearStateAsync(grainTypeName, grainId, grainState, cancellationToken).ConfigureAwait(false);
        var clearedStateVersion = grainState.ETag;
        Assert.NotEqual(writtenStateVersion, clearedStateVersion);
        Assert.False(grainState.RecordExists);

        // Write new state after clear
        var newState = new GrainState<TestState1> { State = new TestState1(), ETag = clearedStateVersion };
        await Storage.WriteStateAsync(grainTypeName, grainId, newState, cancellationToken).ConfigureAwait(false);
        Assert.Equal(newState.State, Activator.CreateInstance<TestState1>());
        Assert.True(newState.RecordExists);

        // Read back and verify it's the default state
        var readBackState = new GrainState<TestState1> { State = new TestState1() };
        await Storage.ReadStateAsync(grainTypeName, grainId, readBackState, cancellationToken).ConfigureAwait(false);
        Assert.True(readBackState.RecordExists);
        var readBackStateValue = Assert.IsType<TestState1>(readBackState.State);
        Assert.Null(readBackStateValue.A);
        Assert.Equal(0, readBackStateValue.B);
        Assert.Equal(0L, readBackStateValue.C);
    }

    /// <summary>
    /// Tests storage operations with string-based grain keys.
    /// </summary>
    public virtual Task PersistenceStorage_WriteRead_StringKey() =>
        PersistenceStorage_WriteRead_StringKeyAsync(CancellationToken.None);

    /// <inheritdoc cref="PersistenceStorage_WriteRead_StringKey()"/>
    /// <param name="cancellationToken">A token which cancels the storage operations.</param>
    public virtual async Task PersistenceStorage_WriteRead_StringKeyAsync(CancellationToken cancellationToken)
    {
        var grainTypeName = "TestGrain";
        var stringKey = $"StringKey-{Guid.NewGuid()}";
        var (grainId, grainState) = GetTestReferenceAndState(stringKey, null);
        var state = Assert.IsType<TestState1>(grainState.State);
        state.A = "TestString";
        state.B = 123;

        await Store_WriteRead(grainTypeName, grainId, grainState, cancellationToken).ConfigureAwait(false);

        // Verify we can read it back with the same key
        var readState = new GrainState<TestState1> { State = new TestState1() };
        await Storage.ReadStateAsync(grainTypeName, grainId, readState, cancellationToken).ConfigureAwait(false);
        var readStateValue = Assert.IsType<TestState1>(readState.State);
        Assert.Equal("TestString", readStateValue.A);
        Assert.Equal(123, readStateValue.B);
        Assert.True(readState.RecordExists);
    }

    /// <summary>
    /// Tests storage operations with integer-based grain keys.
    /// </summary>
    public virtual Task PersistenceStorage_WriteRead_IntegerKey() =>
        PersistenceStorage_WriteRead_IntegerKeyAsync(CancellationToken.None);

    /// <inheritdoc cref="PersistenceStorage_WriteRead_IntegerKey()"/>
    /// <param name="cancellationToken">A token which cancels the storage operations.</param>
    public virtual async Task PersistenceStorage_WriteRead_IntegerKeyAsync(CancellationToken cancellationToken)
    {
        var grainTypeName = "TestGrain";
        var integerKey = Random.Shared.NextInt64();
        var (grainId, grainState) = GetTestReferenceAndState(integerKey, null);
        var state = Assert.IsType<TestState1>(grainState.State);
        state.A = "TestInteger";
        state.B = 456;
        state.C = integerKey;

        await Store_WriteRead(grainTypeName, grainId, grainState, cancellationToken).ConfigureAwait(false);

        // Verify we can read it back with the same key
        var readState = new GrainState<TestState1> { State = new TestState1() };
        await Storage.ReadStateAsync(grainTypeName, grainId, readState, cancellationToken).ConfigureAwait(false);
        var readStateValue = Assert.IsType<TestState1>(readState.State);
        Assert.Equal("TestInteger", readStateValue.A);
        Assert.Equal(456, readStateValue.B);
        Assert.Equal(integerKey, readStateValue.C);
        Assert.True(readState.RecordExists);
    }

    /// <summary>
    /// Tests that ETag updates properly on successive writes.
    /// </summary>
    public virtual Task PersistenceStorage_ETagChangesOnWrite() =>
        PersistenceStorage_ETagChangesOnWriteAsync(CancellationToken.None);

    /// <inheritdoc cref="PersistenceStorage_ETagChangesOnWrite()"/>
    /// <param name="cancellationToken">A token which cancels the storage operations.</param>
    public virtual async Task PersistenceStorage_ETagChangesOnWriteAsync(CancellationToken cancellationToken)
    {
        var grainTypeName = "TestGrain";
        var (grainId, grainState) = GetTestReferenceAndState(Random.Shared.NextInt64(), null);
        var state = Assert.IsType<TestState1>(grainState.State);

        // First write
        state.A = "Version1";
        await Storage.WriteStateAsync(grainTypeName, grainId, grainState, cancellationToken).ConfigureAwait(false);
        var etag1 = grainState.ETag;
        Assert.NotNull(etag1);

        // Second write
        state.A = "Version2";
        await Storage.WriteStateAsync(grainTypeName, grainId, grainState, cancellationToken).ConfigureAwait(false);
        var etag2 = grainState.ETag;
        Assert.NotNull(etag2);
        Assert.NotEqual(etag1, etag2);

        // Third write
        state.A = "Version3";
        await Storage.WriteStateAsync(grainTypeName, grainId, grainState, cancellationToken).ConfigureAwait(false);
        var etag3 = grainState.ETag;
        Assert.NotNull(etag3);
        Assert.NotEqual(etag2, etag3);
        Assert.NotEqual(etag1, etag3);
    }

    /// <summary>
    /// Tests that calling Clear before any write works correctly.
    /// Verifies that RecordExists is false and State is not null after clearing non-existent state.
    /// </summary>
    public virtual Task PersistenceStorage_ClearBeforeWrite() =>
        PersistenceStorage_ClearBeforeWriteAsync(CancellationToken.None);

    /// <inheritdoc cref="PersistenceStorage_ClearBeforeWrite()"/>
    /// <param name="cancellationToken">A token which cancels the storage operations.</param>
    public virtual async Task PersistenceStorage_ClearBeforeWriteAsync(CancellationToken cancellationToken)
    {
        var grainTypeName = "TestGrain";
        var (grainId, grainState) = GetTestReferenceAndState(Random.Shared.NextInt64(), null);
        grainState.State = new TestState1 { A = "stale", B = 1, C = 2 };
        grainState.RecordExists = true;

        await Storage.ClearStateAsync(grainTypeName, grainId, grainState, cancellationToken).ConfigureAwait(false);

        Assert.False(grainState.RecordExists);
        Assert.Equal(new TestState1(), grainState.State);
    }

    /// <summary>
    /// Tests that State property is never null after Clear operation.
    /// Verifies the State object remains initialized even after clearing.
    /// </summary>
    public virtual Task PersistenceStorage_ClearStateDoesNotNullifyState() =>
        PersistenceStorage_ClearStateDoesNotNullifyStateAsync(CancellationToken.None);

    /// <inheritdoc cref="PersistenceStorage_ClearStateDoesNotNullifyState()"/>
    /// <param name="cancellationToken">A token which cancels the storage operations.</param>
    public virtual async Task PersistenceStorage_ClearStateDoesNotNullifyStateAsync(
        CancellationToken cancellationToken)
    {
        var grainTypeName = "TestGrain";
        var (grainId, grainState) = GetTestReferenceAndState(Random.Shared.NextInt64(), null);
        var state = Assert.IsType<TestState1>(grainState.State);
        state.A = "TestData";
        state.B = 100;

        // Write and then clear
        await Storage.WriteStateAsync(grainTypeName, grainId, grainState, cancellationToken).ConfigureAwait(false);
        Assert.True(grainState.RecordExists);
        Assert.NotNull(grainState.State);

        await Storage.ClearStateAsync(grainTypeName, grainId, grainState, cancellationToken).ConfigureAwait(false);

        // State object should not be null even after clear
        Assert.NotNull(grainState.State);
        Assert.False(grainState.RecordExists);
    }

    /// <summary>
    /// Tests that ETag changes after Clear operation.
    /// Some providers may set ETag to null on clear, others may update it - both are acceptable.
    /// </summary>
    public virtual Task PersistenceStorage_ClearUpdatesETag() =>
        PersistenceStorage_ClearUpdatesETagAsync(CancellationToken.None);

    /// <inheritdoc cref="PersistenceStorage_ClearUpdatesETag()"/>
    /// <param name="cancellationToken">A token which cancels the storage operations.</param>
    public virtual async Task PersistenceStorage_ClearUpdatesETagAsync(CancellationToken cancellationToken)
    {
        var grainTypeName = "TestGrain";
        var (grainId, grainState) = GetTestReferenceAndState(Random.Shared.NextInt64(), null);
        var state = Assert.IsType<TestState1>(grainState.State);
        state.A = "TestData";

        // Write state
        await Storage.WriteStateAsync(grainTypeName, grainId, grainState, cancellationToken).ConfigureAwait(false);
        var writeETag = grainState.ETag;
        Assert.NotNull(writeETag);

        // Clear state
        await Storage.ClearStateAsync(grainTypeName, grainId, grainState, cancellationToken).ConfigureAwait(false);
        var clearETag = grainState.ETag;

        // ETag should have changed (either to null or a new value)
        Assert.NotEqual(writeETag, clearETag);
        Assert.False(grainState.RecordExists);
    }

    /// <summary>
    /// Tests reading state after it has been cleared.
    /// Verifies that reading a cleared state returns RecordExists=false and State is not null.
    /// </summary>
    public virtual Task PersistenceStorage_ReadAfterClear() =>
        PersistenceStorage_ReadAfterClearAsync(CancellationToken.None);

    /// <inheritdoc cref="PersistenceStorage_ReadAfterClear()"/>
    /// <param name="cancellationToken">A token which cancels the storage operations.</param>
    public virtual async Task PersistenceStorage_ReadAfterClearAsync(CancellationToken cancellationToken)
    {
        var grainTypeName = "TestGrain";
        var (grainId, grainState) = GetTestReferenceAndState(Random.Shared.NextInt64(), null);
        var state = Assert.IsType<TestState1>(grainState.State);
        state.A = "OriginalData";
        state.B = 42;

        // Write, clear, then read
        await Storage.WriteStateAsync(grainTypeName, grainId, grainState, cancellationToken).ConfigureAwait(false);
        await Storage.ClearStateAsync(grainTypeName, grainId, grainState, cancellationToken).ConfigureAwait(false);

        // Read the cleared state
        var readState = new GrainState<TestState1> { State = new TestState1() };
        await Storage.ReadStateAsync(grainTypeName, grainId, readState, cancellationToken).ConfigureAwait(false);

        // After reading a cleared state
        Assert.False(readState.RecordExists);
        Assert.NotNull(readState.State); // State should still be initialized
    }

    /// <summary>
    /// Tests multiple successive clear operations.
    /// Verifies that clearing an already-cleared state is idempotent.
    /// </summary>
    public virtual Task PersistenceStorage_MultipleClearOperations() =>
        PersistenceStorage_MultipleClearOperationsAsync(CancellationToken.None);

    /// <inheritdoc cref="PersistenceStorage_MultipleClearOperations()"/>
    /// <param name="cancellationToken">A token which cancels the storage operations.</param>
    public virtual async Task PersistenceStorage_MultipleClearOperationsAsync(CancellationToken cancellationToken)
    {
        var grainTypeName = "TestGrain";
        var (grainId, grainState) = GetTestReferenceAndState(Random.Shared.NextInt64(), null);
        var state = Assert.IsType<TestState1>(grainState.State);
        state.A = "TestData";

        // Write state
        await Storage.WriteStateAsync(grainTypeName, grainId, grainState, cancellationToken).ConfigureAwait(false);
        Assert.True(grainState.RecordExists);

        // First clear
        await Storage.ClearStateAsync(grainTypeName, grainId, grainState, cancellationToken).ConfigureAwait(false);
        var firstClearETag = grainState.ETag;
        Assert.False(grainState.RecordExists);

        // Second clear - should be idempotent
        await Storage.ClearStateAsync(grainTypeName, grainId, grainState, cancellationToken).ConfigureAwait(false);
        Assert.False(grainState.RecordExists);
        Assert.NotNull(grainState.State);
    }

    /// <summary>
    /// Tests that State property remains initialized (not null) after reading non-existent state.
    /// </summary>
    public virtual Task PersistenceStorage_ReadNonExistentStateHasNonNullState() =>
        PersistenceStorage_ReadNonExistentStateHasNonNullStateAsync(CancellationToken.None);

    /// <inheritdoc cref="PersistenceStorage_ReadNonExistentStateHasNonNullState()"/>
    /// <param name="cancellationToken">A token which cancels the storage operations.</param>
    public virtual async Task PersistenceStorage_ReadNonExistentStateHasNonNullStateAsync(
        CancellationToken cancellationToken)
    {
        var grainTypeName = "TestGrain";
        var (grainId, grainState) = GetTestReferenceAndState(Random.Shared.NextInt64(), null);

        // Read state that was never written
        await Storage.ReadStateAsync(grainTypeName, grainId, grainState, cancellationToken).ConfigureAwait(false);

        // State should be initialized even though nothing was written
        Assert.False(grainState.RecordExists);
        Assert.NotNull(grainState.State);
        Assert.Null(grainState.ETag);
    }

    /// <summary>
    /// Tests that state names identify independent records even when the grain ID is the same.
    /// </summary>
    public virtual Task PersistenceStorage_StateNamesUseIndependentRecords() =>
        PersistenceStorage_StateNamesUseIndependentRecordsAsync(CancellationToken.None);

    /// <inheritdoc cref="PersistenceStorage_StateNamesUseIndependentRecords()"/>
    /// <param name="cancellationToken">A token which cancels the storage operations.</param>
    public virtual async Task PersistenceStorage_StateNamesUseIndependentRecordsAsync(
        CancellationToken cancellationToken)
    {
        var grainId = GrainId.Create("test-grain", Guid.NewGuid().ToString("N"));
        var first = new GrainState<TestState1> { State = new TestState1 { A = "first", B = 1, C = 2 } };
        var second = new GrainState<TestState1> { State = new TestState1 { A = "second", B = 3, C = 4 } };

        await Storage.WriteStateAsync("first-state", grainId, first, cancellationToken).ConfigureAwait(false);
        await Storage.WriteStateAsync("second-state", grainId, second, cancellationToken).ConfigureAwait(false);

        var firstRead = new GrainState<TestState1> { State = new TestState1() };
        var secondRead = new GrainState<TestState1> { State = new TestState1() };
        await Storage.ReadStateAsync("first-state", grainId, firstRead, cancellationToken).ConfigureAwait(false);
        await Storage.ReadStateAsync("second-state", grainId, secondRead, cancellationToken).ConfigureAwait(false);

        Assert.Equal(first.State, firstRead.State);
        Assert.Equal(second.State, secondRead.State);
        Assert.NotEqual(firstRead.State, secondRead.State);
    }

    /// <summary>
    /// Tests that clearing state with a stale ETag throws <see cref="InconsistentStateException"/>
    /// and preserves the latest stored state.
    /// </summary>
    public virtual Task PersistenceStorage_ClearInconsistentFailsWithInconsistentStateException() =>
        PersistenceStorage_ClearInconsistentFailsWithInconsistentStateExceptionAsync(CancellationToken.None);

    /// <inheritdoc cref="PersistenceStorage_ClearInconsistentFailsWithInconsistentStateException()"/>
    /// <param name="cancellationToken">A token which cancels the storage operations.</param>
    public virtual async Task PersistenceStorage_ClearInconsistentFailsWithInconsistentStateExceptionAsync(
        CancellationToken cancellationToken)
    {
        var grainTypeName = "TestGrain";
        var (grainId, grainState) = GetTestReferenceAndState(Random.Shared.NextInt64(), null);
        grainState.State = new TestState1 { A = "initial", B = 1, C = 2 };
        await Storage.WriteStateAsync(grainTypeName, grainId, grainState, cancellationToken).ConfigureAwait(false);
        var staleETag = grainState.ETag;

        grainState.State = new TestState1 { A = "latest", B = 3, C = 4 };
        await Storage.WriteStateAsync(grainTypeName, grainId, grainState, cancellationToken).ConfigureAwait(false);

        var staleState = new GrainState<TestState1>
        {
            State = new TestState1(),
            ETag = staleETag,
            RecordExists = true,
        };

        var exception = await Record.ExceptionAsync(
            () => Storage.ClearStateAsync(grainTypeName, grainId, staleState, cancellationToken)).ConfigureAwait(false);
        Assert.IsAssignableFrom<InconsistentStateException>(exception);

        var readState = new GrainState<TestState1> { State = new TestState1() };
        await Storage.ReadStateAsync(grainTypeName, grainId, readState, cancellationToken).ConfigureAwait(false);
        Assert.True(readState.RecordExists);
        Assert.Equal(grainState.State, readState.State);
        Assert.Equal(grainState.ETag, readState.ETag);

    }

    /// <summary>
    /// Tests write-read-clear-read cycle to verify state transitions.
    /// </summary>
    public virtual Task PersistenceStorage_WriteReadClearReadCycle() =>
        PersistenceStorage_WriteReadClearReadCycleAsync(CancellationToken.None);

    /// <inheritdoc cref="PersistenceStorage_WriteReadClearReadCycle()"/>
    /// <param name="cancellationToken">A token which cancels the storage operations.</param>
    public virtual async Task PersistenceStorage_WriteReadClearReadCycleAsync(CancellationToken cancellationToken)
    {
        var grainTypeName = "TestGrain";
        var (grainId, grainState) = GetTestReferenceAndState(Random.Shared.NextInt64(), null);
        var state = Assert.IsType<TestState1>(grainState.State);
        state.A = "InitialValue";
        state.B = 99;

        // Write
        await Storage.WriteStateAsync(grainTypeName, grainId, grainState, cancellationToken).ConfigureAwait(false);
        var writeETag = grainState.ETag;
        Assert.True(grainState.RecordExists);

        // Read back
        var readState1 = new GrainState<TestState1> { State = new TestState1() };
        await Storage.ReadStateAsync(grainTypeName, grainId, readState1, cancellationToken).ConfigureAwait(false);
        Assert.True(readState1.RecordExists);
        var readStateValue = Assert.IsType<TestState1>(readState1.State);
        Assert.Equal("InitialValue", readStateValue.A);
        Assert.Equal(99, readStateValue.B);
        Assert.Equal(writeETag, readState1.ETag);

        // Clear
        await Storage.ClearStateAsync(grainTypeName, grainId, grainState, cancellationToken).ConfigureAwait(false);
        Assert.False(grainState.RecordExists);

        // Read after clear
        var readState2 = new GrainState<TestState1> { State = new TestState1() };
        await Storage.ReadStateAsync(grainTypeName, grainId, readState2, cancellationToken).ConfigureAwait(false);
        Assert.False(readState2.RecordExists);
        Assert.NotNull(readState2.State);
    }

    /// <summary>
    /// Tests that updating state with the same values still updates the ETag.
    /// </summary>
    public virtual Task PersistenceStorage_WriteWithSameValuesUpdatesETag() =>
        PersistenceStorage_WriteWithSameValuesUpdatesETagAsync(CancellationToken.None);

    /// <inheritdoc cref="PersistenceStorage_WriteWithSameValuesUpdatesETag()"/>
    /// <param name="cancellationToken">A token which cancels the storage operations.</param>
    public virtual async Task PersistenceStorage_WriteWithSameValuesUpdatesETagAsync(
        CancellationToken cancellationToken)
    {
        var grainTypeName = "TestGrain";
        var (grainId, grainState) = GetTestReferenceAndState(Random.Shared.NextInt64(), null);
        var state = Assert.IsType<TestState1>(grainState.State);
        state.A = "SameValue";
        state.B = 123;

        // First write
        await Storage.WriteStateAsync(grainTypeName, grainId, grainState, cancellationToken).ConfigureAwait(false);
        var etag1 = grainState.ETag;
        Assert.NotNull(etag1);

        // Write again with same values
        state.A = "SameValue";
        state.B = 123;
        await Storage.WriteStateAsync(grainTypeName, grainId, grainState, cancellationToken).ConfigureAwait(false);
        var etag2 = grainState.ETag;
        Assert.NotNull(etag2);

        // ETag should still change even though values are the same
        Assert.NotEqual(etag1, etag2);
    }
}
