using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Storage;
using Orleans.Persistence.TestKit;
using UnitTests.StorageTests.Relational.TestDataSets;
using Xunit;


namespace UnitTests.StorageTests.Relational
{
    /// <summary>
    /// The relational back-ends share quite a lot of functionality. This shared functionality
    /// is collected here, but concrete implementations can provide and implement storage specific
    /// actions.
    /// </summary>
    public abstract class RelationalStorageTests : IAsyncLifetime
    {
        private readonly string _adoNetInvariant;
        private readonly bool _deleteStateOnClear;

        private IStorageHasherPicker ConstantHasher { get; } = new StorageHasherPicker(new[] { new ConstantHasher() });

        /// <summary>
        /// The tests and assertions common across all back-ends are here.
        /// </summary>
        internal CommonStorageTests PersistenceStorageTests { get; private set; } = null!;

        /// <summary>
        /// The tests and assertions common across all back-ends are here.
        /// </summary>
        protected CommonFixture Fixture { get; }

        /// <summary>
        /// The fixture holds cached data such as the underlying connection to the storage.
        /// </summary>
        public string GrainCountQuery { get; } = "SELECT COUNT(*) AS Count FROM Storage WHERE GrainTypeString = N'{0}';";


        public RelationalStorageTests(string adoNetInvariant, CommonFixture fixture, bool deleteStateOnClear = false)
        {
            Fixture = fixture;
            _adoNetInvariant = adoNetInvariant;
            _deleteStateOnClear = deleteStateOnClear;
        }

        public async Task InitializeAsync()
        {
            var persistenceStorage = await Fixture.GetStorageProvider(_adoNetInvariant, _deleteStateOnClear);
            Skip.If(persistenceStorage is null, $"Persistence storage not available for {_adoNetInvariant}.");
            PersistenceStorageTests = new CommonStorageTests(persistenceStorage);
        }

        public Task DisposeAsync() => Task.CompletedTask;

        internal Task Relational_WriteReadWriteRead100StatesInParallel()
        {
            return PersistenceStorageTests.PersistenceStorage_WriteReadWriteReadStatesInParallel(nameof(Relational_WriteReadWriteRead100StatesInParallel));
        }

        internal Task Relational_HashCollisionTests()
        {
            ((AdoNetGrainStorage)PersistenceStorageTests.Storage).HashPicker = ConstantHasher;
            return PersistenceStorageTests.PersistenceStorage_WriteReadWriteReadStatesInParallel(nameof(Relational_HashCollisionTests), 2);
        }

        internal async Task Relational_WriteDuplicateFailsWithInconsistentStateException()
        {
            var exception = await PersistenceStorageTests.PersistenceStorage_WriteDuplicateFailsWithInconsistentStateException();
            CommonStorageUtilities.AssertRelationalInconsistentExceptionMessage(exception.Message);
        }

        internal async Task Relational_WriteInconsistentFailsWithIncosistentStateException()
        {
            var exception = await PersistenceStorageTests.PersistenceStorage_WriteInconsistentFailsWithInconsistentStateException();
            CommonStorageUtilities.AssertRelationalInconsistentExceptionMessage(exception.Message);
        }

        internal Task Relational_Json_WriteRead(string grainType, GrainId grainId, GrainState<TestStateGeneric1<string>> grainState)
        {
            ((AdoNetGrainStorage)PersistenceStorageTests.Storage).Serializer = GetJsonGrainStorageSerializer();
            return PersistenceStorageTests.Store_WriteRead(grainType, grainId, grainState);
        }

        internal Task Relational_Binary_WriteRead(string grainType, GrainId grainId, GrainState<TestStateGeneric1<string>> grainState)
        {
            ((AdoNetGrainStorage)PersistenceStorageTests.Storage).Serializer = GetOrleansGrainStorageSerializer();
            return PersistenceStorageTests.Store_WriteRead(grainType, grainId, grainState);
        }

        internal Task Relational_ModelBasedGeneratedConformance(string providerName)
        {
            var runner = new GrainStorageModelBasedTestRunner(PersistenceStorageTests.Storage, providerName);

            return runner.RunGeneratedConformanceTests();
        }

        private JsonGrainStorageSerializer GetJsonGrainStorageSerializer()
        {
            var serializer = this.Fixture.Services.GetRequiredService<OrleansJsonSerializer>();
            return new JsonGrainStorageSerializer(serializer);
        }

        private OrleansGrainStorageSerializer GetOrleansGrainStorageSerializer()
        {
            var serializer = this.Fixture.Services.GetRequiredService<Serializer>();
            return new OrleansGrainStorageSerializer(serializer);
        }
    }
}
