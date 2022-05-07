using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using Orleans.Storage;
using UnitTests.StorageTests.Relational.TestDataSets;
using Xunit;

namespace UnitTests.StorageTests.Relational
{
    /// <summary>
    /// The storage tests with assertions that should hold for any back-end.
    /// </summary>
    /// <remarks>
    /// This is not in an inheritance hierarchy to allow for cleaner separation for any framework
    /// code and even testing the tests. The tests use unique news (Guids) so the storage doesn't
    /// need to be cleaned until all the storage tests have been run.
    /// </remarks>
    internal class CommonStorageTests
    {
        private readonly IInternalGrainFactory _grainFactory;

        /// <summary>
        /// The default constructor.
        /// </summary>
        /// <param name="grainFactory"></param>
        /// <param name="storage"></param>
        public CommonStorageTests(IInternalGrainFactory grainFactory, IGrainStorage storage)
        {
            _grainFactory = grainFactory;
            Storage = storage;
        }

        /// <summary>
        /// The storage provider under test.
        /// </summary>
        public IGrainStorage Storage { get; }

        /// <summary>
        /// Creates a new grain and a grain reference pair.
        /// </summary>
        /// <param name="grainId">The grain ID.</param>
        /// <param name="version">The initial version of the state.</param>
        /// <returns>A grain reference and a state pair.</returns>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0022")]
        internal (GrainReference GrainReference, GrainState<TestState1> GrainState)  GetTestReferenceAndState(long grainId, string version)
        {
            var grainReference = (GrainReference)_grainFactory.GetGrain(GrainId.Create("my-grain-type", grainId.ToString("X")));
            var grainState = new GrainState<TestState1> { State = new TestState1(), ETag = version };
            return (grainReference, grainState);
        }

        /// <summary>
        /// Creates a new grain and a grain reference pair.
        /// </summary>
        /// <param name="grainId">The grain ID.</param>
        /// <param name="version">The initial version of the state.</param>
        /// <returns>A grain reference and a state pair.</returns>
        internal (GrainReference GrainReference, GrainState<TestState1> GrainState) GetTestReferenceAndState(string grainId, string version)
        {
            var grainReference = (GrainReference)_grainFactory.GetGrain(GrainId.Create("my-grain-type", grainId));
            var grainState = new GrainState<TestState1> { State = new TestState1(), ETag = version };
            return (grainReference, grainState);
        }

        /// <summary>
        /// Writes a known inconsistent state to the storage and asserts an exception will be thrown.
        /// </summary>
        /// <returns></returns>
        internal async Task PersistenceStorage_Relational_WriteReadIdCyrillic()
        {
            var grainTypeName = GrainTypeGenerator.GetGrainType<Guid>();
            var grainReference = this.GetTestReferenceAndState(0, null);
            var grainState = grainReference.GrainState;
            await Storage.WriteStateAsync(grainTypeName, grainReference.GrainReference, grainState).ConfigureAwait(false);
            var storedGrainState = new GrainState<TestState1> { State = new TestState1() };
            await Storage.ReadStateAsync(grainTypeName, grainReference.GrainReference, storedGrainState).ConfigureAwait(false);

            Assert.Equal(grainState.ETag, storedGrainState.ETag);
            Assert.Equal(grainState.State, storedGrainState.State);
        }

        /// <summary>
        /// Writes to storage and tries to re-write the same state with NULL as ETag, as if the
        /// grain was just created.
        /// </summary>
        /// <returns>
        /// The <see cref="InconsistentStateException"/> thrown by the provider. This can be further
        /// inspected by the storage specific asserts.
        /// </returns>
        internal async Task<InconsistentStateException> PersistenceStorage_WriteDuplicateFailsWithInconsistentStateException()
        {
            //A grain with a random ID will be arranged to the database. Then its state is set to null to simulate the fact
            //it is like a second activation after a one that has succeeded to write.
            string grainTypeName = GrainTypeGenerator.GetGrainType<Guid>();
            var inconsistentState = this.GetTestReferenceAndState(RandomUtilities.GetRandom<long>(), null);
            var grainReference = inconsistentState.GrainReference;
            var grainState = inconsistentState.GrainState;

            await Store_WriteRead(grainTypeName, inconsistentState.GrainReference, inconsistentState.GrainState).ConfigureAwait(false);
            grainState.ETag = null;
            var exception = await Record.ExceptionAsync(() => Store_WriteRead(grainTypeName, grainReference, grainState)).ConfigureAwait(false);

            Assert.NotNull(exception);
            Assert.IsType<InconsistentStateException>(exception);

            return (InconsistentStateException)exception;
        }

        /// <summary>
        /// Writes a known inconsistent state to the storage and asserts an exception will be thrown.
        /// </summary>
        /// <returns>
        /// The <see cref="InconsistentStateException"/> thrown by the provider. This can be further
        /// inspected by the storage specific asserts.
        /// </returns>
        internal async Task<InconsistentStateException> PersistenceStorage_WriteInconsistentFailsWithInconsistentStateException()
        {
            //Some version not expected to be in the storage for this type and ID.
            var inconsistentStateVersion = RandomUtilities.GetRandom<int>().ToString(CultureInfo.InvariantCulture);

            var inconsistentState = this.GetTestReferenceAndState(RandomUtilities.GetRandom<long>(), inconsistentStateVersion);
            string grainTypeName = GrainTypeGenerator.GetGrainType<Guid>();
            var exception = await Record.ExceptionAsync(() => Store_WriteRead(grainTypeName, inconsistentState.GrainReference, inconsistentState.GrainState)).ConfigureAwait(false);

            Assert.NotNull(exception);
            Assert.IsType<InconsistentStateException>(exception);

            return (InconsistentStateException)exception;
        }

        internal async Task PersistenceStorage_WriteReadWriteReadStatesInParallel(string prefix = nameof(this.PersistenceStorage_WriteReadWriteReadStatesInParallel), int countOfGrains = 100)
        {
            //As data is written and read the Version numbers (ETags) are as checked for correctness (they change).
            //Additionally the Store_WriteRead tests does its validation.
            var grainTypeName = GrainTypeGenerator.GetGrainType<Guid>();
            int StartOfRange = 33900;
            int CountOfRange = countOfGrains;

            //Since the version is NULL, storage provider tries to insert this data
            //as new state. If there is already data with this class, the writing fails
            //and the storage provider throws. Essentially it means either this range
            //is ill chosen or the test failed due to another problem.
            var grainStates = Enumerable.Range(StartOfRange, CountOfRange).Select(i => GetTestReferenceAndState($"{prefix}-{Guid.NewGuid():N}-{i}", null)).ToList();

            // Avoid parallelization of the first write to not stress out the system with deadlocks
            // on INSERT
            foreach (var grainData in grainStates)
            {
                //A sanity checker that the first version really has null as its state. Then it is stored
                //to the database and a new version is acquired.
                var firstVersion = grainData.GrainState.ETag;
                Assert.Null(firstVersion);

                await Store_WriteRead(grainTypeName, grainData.GrainReference, grainData.GrainState).ConfigureAwait(false);
                var secondVersion = grainData.GrainState.ETag;
                Assert.NotEqual(firstVersion, secondVersion);
            };

            int MaxNumberOfThreads = Environment.ProcessorCount * 3;
            // The purpose of Parallel.ForEachAsync is to ensure the storage provider will be tested from
            // multiple threads concurrently, as would happen in running system also. Nevertheless
            // limit the degree of parallelization (concurrent threads) to avoid unnecessarily
            // starving and growing the thread pool (which is very slow) if a few threads coupled
            // with parallelization via tasks can force most concurrency scenarios.

            await ParallelForEachAsync(grainStates, new ParallelOptions { MaxDegreeOfParallelism = MaxNumberOfThreads }, async (grainData, ct) =>
            {
                // This loop writes the state consecutive times to the database to make sure its
                // version is updated appropriately.
                for (int k = 0; k < 10; ++k)
                {
                    var versionBefore = grainData.GrainState.ETag;
                    await RetryHelper.RetryOnExceptionAsync(5, RetryOperation.Sigmoid, async () =>
                    {
                        await Store_WriteRead(grainTypeName, grainData.GrainReference, grainData.GrainState);
                        return 0;
                    });

                    var versionAfter = grainData.GrainState.ETag;
                    Assert.NotEqual(versionBefore, versionAfter);
                }
            });
        }

        private static async ValueTask ParallelForEachAsync<TSource>(
            ICollection<TSource> source,
            ParallelOptions options,
            Func<TSource, CancellationToken, ValueTask> func)
        {
#if NET6_0_OR_GREATER
            await Parallel.ForEachAsync(source, options, func);
#else
            var cancellationToken = CancellationToken.None;
            var tcs = new TaskCompletionSource<int>();
            var workers = new List<Task>();
            var channel = Channel.CreateUnbounded<TSource>();
            foreach (var item in source)
            {
                await channel.Writer.WriteAsync(item);
            }

            channel.Writer.Complete();

            for (var i = 0; i < options.MaxDegreeOfParallelism; i++)
            {
                workers.Add(Task.Run(async () =>
                {
                    try
                    {
                        var reader = channel.Reader;
                        await foreach (var item in reader.ReadAllAsync(cancellationToken))
                        {
                            await func(item, cancellationToken);
                        }
                    }
                    catch (Exception ex)
                    {
                        tcs.TrySetException(ex);
                    }
                }));
            }

            await Task.WhenAll(workers);
#endif
        }

        /// <summary>
        /// Writes to storage, clears and reads back and asserts both the version and the state.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="grainTypeName">The type of the grain.</param>
        /// <param name="grainReference">The grain reference as would be given by Orleans.</param>
        /// <param name="grainState">The grain state the grain would hold and Orleans pass.</param>
        /// <returns></returns>
        internal async Task Store_WriteClearRead<T>(string grainTypeName, GrainReference grainReference, GrainState<T> grainState) where T : new()
        {
            //A legal situation for clearing has to be arranged by writing a state to the storage before
            //clearing it. Writing and clearing both change the ETag, so they should differ.
            await Storage.WriteStateAsync(grainTypeName, grainReference, grainState);
            var writtenStateVersion = grainState.ETag;
            var recordExitsAfterWriting = grainState.RecordExists;

            await Storage.ClearStateAsync(grainTypeName, grainReference, grainState).ConfigureAwait(false);
            var clearedStateVersion = grainState.ETag;
            var recordExitsAfterClearing = grainState.RecordExists;

            var storedGrainState = new GrainState<T> { State = new T() };
            await Storage.ReadStateAsync(grainTypeName, grainReference, storedGrainState).ConfigureAwait(false);

            Assert.NotEqual(writtenStateVersion, clearedStateVersion);
            Assert.Equal(storedGrainState.State, Activator.CreateInstance<T>());
            Assert.True(recordExitsAfterWriting);
            Assert.False(recordExitsAfterClearing);
            Assert.True(storedGrainState.RecordExists);
        }

        /// <summary>
        /// Writes to storage, reads back and asserts both the version and the state.
        /// </summary>
        /// <typeparam name="T">The grain state type.</typeparam>
        /// <param name="grainTypeName">The type of the grain.</param>
        /// <param name="grainReference">The grain reference as would be given by Orleans.</param>
        /// <param name="grainState">The grain state the grain would hold and Orleans pass.</param>
        /// <returns></returns>
        internal async Task Store_WriteRead<T>(string grainTypeName, GrainReference grainReference, GrainState<T> grainState) where T : new()
        {
            await Storage.WriteStateAsync(grainTypeName, grainReference, grainState).ConfigureAwait(false);
            var storedGrainState = new GrainState<T> { State = new T() };
            await Storage.ReadStateAsync(grainTypeName, grainReference, storedGrainState).ConfigureAwait(false);

            Assert.Equal(grainState.ETag, storedGrainState.ETag);
            Assert.Equal(grainState.State, storedGrainState.State);
            Assert.True(storedGrainState.RecordExists);
        }
    }
}