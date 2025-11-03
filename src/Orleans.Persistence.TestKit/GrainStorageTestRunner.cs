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
}
