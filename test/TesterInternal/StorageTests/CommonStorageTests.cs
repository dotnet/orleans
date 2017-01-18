using Orleans;
using Orleans.Runtime;
using Orleans.Storage;
using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using UnitTests.StorageTests.Relational.TestDataSets;
using Xunit;

namespace UnitTests.StorageTests.Relational
{
    /// <summary>
    /// The storage tests with assertions that should hold for any back-end.
    /// </summary>
    /// <remarks>This is not in an inheritance hierarchy to allow for cleaner separation for any framework
    /// code and even testing the tests. The tests use unique news (Guids) so the storage doesn't need to be
    /// cleaned until all the storage tests have been run.</remarks>
    internal class CommonStorageTests
    {
        private readonly IInternalGrainFactory grainFactory;

        /// <summary>
        /// The storage provider under test.
        /// </summary>
        public IStorageProvider Storage { get; }


        /// <summary>
        /// The default constructor.
        /// </summary>
        /// <param name="grainFactory"></param>
        /// <param name="storage"></param>
        public CommonStorageTests(IInternalGrainFactory grainFactory, IStorageProvider storage)
        {
            this.grainFactory = grainFactory;
            Storage = storage;
        }

        internal async Task PersistenceStorage_WriteReadWriteReadStatesInParallel(string prefix = nameof(this.PersistenceStorage_WriteReadWriteReadStatesInParallel), int countOfGrains = 100)
        {
            //As data is written and read the Version numbers (ETags) are as checked for correctness (they change).
            //Additionally the Store_WriteRead tests does its validation.
            var grainTypeName = GrainTypeGenerator.GetGrainType<Guid>();
            int StartOfRange = 33900;
            int CountOfRange = countOfGrains;
            string grainIdTemplate = $"{prefix}-" + "{0}";

            //The purpose of this Task.Run is to ensure the storage provider will be tested from
            //multiple threads concurrently, as would happen in running system also.
            var tasks = Enumerable.Range(StartOfRange, CountOfRange).Select(i => Task.Run(async () =>
            {
                //Since the version is NULL, storage provider tries to insert this data
                //as new state. If there is already data with this class, the writing fails
                //and the storage provider throws. Essentially it means either this range
                //is ill chosen or the test failed due another problem.
                var grainId = string.Format(grainIdTemplate, i);
                var grainData = this.GetTestReferenceAndState(i, null);

                //A sanity checker that the first version really has null as its state. Then it is stored
                //to the database and a new version is acquired.
                var firstVersion = grainData.Item2.ETag;
                Assert.Equal(firstVersion, null);

                //This loop writes the state consecutive times to the database to make sure its
                //version is updated appropriately.
                await Store_WriteRead(grainTypeName, grainData.Item1, grainData.Item2);
                for(int k = 0; k < 10; ++k)
                {
                    var secondVersion = grainData.Item2.ETag;
                    Assert.NotEqual(firstVersion, secondVersion);
                    await Store_WriteRead(grainTypeName, grainData.Item1, grainData.Item2);

                    var thirdVersion = grainData.Item2.ETag;
                    Assert.NotEqual(firstVersion, secondVersion);
                    Assert.NotEqual(secondVersion, thirdVersion);
                }
            }));
            await Task.WhenAll(tasks);
        }

        /// <summary>
        /// Writes to storage and tries to re-write the same state with NULL as ETag, as if the grain was just created.
        /// </summary>
        /// <returns>The <see cref="InconsistentStateException"/> thrown by the provider. This can be further inspected
        /// by the storage specific asserts.</returns>
        internal async Task<InconsistentStateException> PersistenceStorage_WriteDuplicateFailsWithInconsistentStateException()
        {
            //A grain with a random ID will be arranged to the database. Then its state is set to null to simulate the fact
            //it is like a second activation after a one that has succeeded to write.
            string grainTypeName = GrainTypeGenerator.GetGrainType<Guid>();
            var inconsistentState = this.GetTestReferenceAndState(RandomUtilities.GetRandom<long>(), null);
            var grainReference = inconsistentState.Item1;
            var grainState = inconsistentState.Item2;

            await Store_WriteRead(grainTypeName, inconsistentState.Item1, inconsistentState.Item2);
            grainState.ETag = null;
            var exception = await Record.ExceptionAsync(() => Store_WriteRead(grainTypeName, grainReference, grainState));

            Assert.NotNull(exception);
            Assert.IsType<InconsistentStateException>(exception);

            return (InconsistentStateException)exception;
        }



        /// <summary>
        /// Writes a known inconsistent state to the storage and asserts an exception will be thrown.
        /// </summary>
        /// <returns>The <see cref="InconsistentStateException"/> thrown by the provider. This can be further inspected
        /// by the storage specific asserts.</returns>
        internal async Task<InconsistentStateException> PersistenceStorage_WriteInconsistentFailsWithInconsistentStateException()
        {
            //Some version not expected to be in the storage for this type and ID.
            var inconsistentStateVersion = RandomUtilities.GetRandom<int>().ToString(CultureInfo.InvariantCulture);

            var inconsistentState = this.GetTestReferenceAndState(RandomUtilities.GetRandom<long>(), inconsistentStateVersion);
            string grainTypeName = GrainTypeGenerator.GetGrainType<Guid>();
            var exception = await Record.ExceptionAsync(() => Store_WriteRead(grainTypeName, inconsistentState.Item1, inconsistentState.Item2));

            Assert.NotNull(exception);
            Assert.IsType<InconsistentStateException>(exception);

            return (InconsistentStateException)exception;
        }


        /// <summary>
        /// Writes a known inconsistent state to the storage and asserts an exception will be thrown.
        /// </summary>
        /// <returns></returns>
        internal async Task PersistenceStorage_Relational_WriteReadIdCyrillic()
        {
            var grainTypeName = GrainTypeGenerator.GetGrainType<Guid>();
            var grainReference = this.GetTestReferenceAndState(0, null);
            var grainState = grainReference.Item2;
            await Storage.WriteStateAsync(grainTypeName, grainReference.Item1, grainState);
            var storedGrainState = new GrainState<TestState1> { State = new TestState1() };
            await Storage.ReadStateAsync(grainTypeName, grainReference.Item1, storedGrainState);

            Assert.Equal(grainState.ETag, storedGrainState.ETag);
            Assert.Equal(grainState.State, storedGrainState.State);
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
            await Storage.WriteStateAsync(grainTypeName, grainReference, grainState);
            var storedGrainState = new GrainState<T> { State = new T() };
            await Storage.ReadStateAsync(grainTypeName, grainReference, storedGrainState);

            Assert.Equal(grainState.ETag, storedGrainState.ETag);
            Assert.Equal(grainState.State, storedGrainState.State);
        }


        /// <summary>
        /// Writes to storage, clears and reads back and asserts both the version and the state.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="grainReference">The grain reference as would be given by Orleans.</param>
        /// <param name="grainState">The grain state the grain would hold and Orleans pass.</param>
        /// <returns></returns>
        internal async Task Store_WriteClearRead<T>(string grainTypeName, GrainReference grainReference, GrainState<T> grainState) where T : new()
        {
            //A legal situation for clearing has to be arranged by writing a state to the storage before
            //clearing it. Writing and clearing both change the ETag, so they should differ.
            await Storage.WriteStateAsync(grainTypeName, grainReference, grainState);
            string writtenStateVersion = grainState.ETag;

            await Storage.ClearStateAsync(grainTypeName, grainReference, grainState);
            string clearedStateVersion = grainState.ETag;

            var storedGrainState = new GrainState<T> { State = new T() };
            await Storage.ReadStateAsync(grainTypeName, grainReference, storedGrainState);

            Assert.NotEqual(writtenStateVersion, clearedStateVersion);
            Assert.Equal(storedGrainState.State, Activator.CreateInstance<T>());
        }


        /// <summary>
        /// Creates a new grain and a grain reference pair.
        /// </summary>
        /// <param name="grainId">The grain ID.</param>
        /// <param name="version">The initial version of the state.</param>
        /// <returns>A grain reference and a state pair.</returns>
        internal Tuple<GrainReference, GrainState<TestState1>> GetTestReferenceAndState(long grainId, string version)
        {
            return Tuple.Create(GrainReference.FromGrainId(GrainId.GetGrainId(UniqueKey.NewKey(grainId, UniqueKey.Category.Grain))), new GrainState<TestState1> { State = new TestState1(), ETag = version });
        }

        /// <summary>
        /// Creates a new grain and a grain reference pair.
        /// </summary>
        /// <param name="grainId">The grain ID.</param>
        /// <param name="version">The initial version of the state.</param>
        /// <returns>A grain reference and a state pair.</returns>
        internal Tuple<GrainReference, GrainState<TestState1>> GetTestReferenceAndState(string grainId, string version)
        {
            return Tuple.Create(GrainReference.FromGrainId(GrainId.FromParsableString(GrainId.GetGrainId(RandomUtilities.NormalGrainTypeCode, grainId).ToParsableString())), new GrainState<TestState1> { State = new TestState1(), ETag = version });
        }
    }
}
