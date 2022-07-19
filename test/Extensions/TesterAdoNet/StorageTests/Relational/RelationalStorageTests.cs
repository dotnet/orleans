using Orleans;
using Orleans.Runtime;
using Orleans.Storage;
using System.Threading.Tasks;
using UnitTests.StorageTests.Relational.TestDataSets;
using Xunit;


namespace UnitTests.StorageTests.Relational
{
    /// <summary>
    /// The relational back-ends share quite a lot of functionality. This shared functionality
    /// is collected here, but concrete implementations can provide and implement storage specific
    /// actions.
    /// </summary>
    public abstract class RelationalStorageTests
    {
        private IStorageSerializationPicker JsonPicker { get; } = new DefaultRelationalStoragePicker(
           new[] { new OrleansStorageDefaultJsonDeserializer(new Newtonsoft.Json.JsonSerializerSettings(), AdoNetGrainStorage.JsonFormatSerializerTag)
        }, new[] { new OrleansStorageDefaultJsonSerializer(new Newtonsoft.Json.JsonSerializerSettings(), AdoNetGrainStorage.JsonFormatSerializerTag) });

        private IStorageSerializationPicker JsonStreamingPicker { get; } = new DefaultRelationalStoragePicker(
           new[] { new OrleansStorageDefaultJsonDeserializer(new Newtonsoft.Json.JsonSerializerSettings(), AdoNetGrainStorage.JsonFormatSerializerTag)
        }, new[] { new OrleansStorageDefaultJsonSerializer(new Newtonsoft.Json.JsonSerializerSettings(), AdoNetGrainStorage.JsonFormatSerializerTag) });

        private IStorageSerializationPicker XmlPicker { get; } = new DefaultRelationalStoragePicker(
            new[] { new OrleansStorageDefaultXmlDeserializer(AdoNetGrainStorage.XmlFormatSerializerTag) },
            new[] { new OrleansStorageDefaultXmlSerializer(AdoNetGrainStorage.XmlFormatSerializerTag) });

        private IStorageSerializationPicker XmlStreamingPicker { get; } = new DefaultRelationalStoragePicker(
            new[] { new OrleansStorageDefaultXmlDeserializer(AdoNetGrainStorage.XmlFormatSerializerTag) },
            new[] { new OrleansStorageDefaultXmlSerializer(AdoNetGrainStorage.XmlFormatSerializerTag) });

        private IStorageHasherPicker ConstantHasher { get; } = new StorageHasherPicker(new[] { new ConstantHasher() });

        /// <summary>
        /// The tests and assertions common across all back-ends are here.
        /// </summary>
        internal CommonStorageTests PersistenceStorageTests { get; }

        /// <summary>
        /// The tests and assertions common across all back-ends are here.
        /// </summary>
        protected CommonFixture Fixture { get; }

        /// <summary>
        /// The fixture holds cached data such as the underlying connection to the storage.
        /// </summary>
        public string GrainCountQuery { get; } = "SELECT COUNT(*) AS Count FROM Storage WHERE GrainTypeString = N'{0}';";


        public RelationalStorageTests(string adoNetInvariant, CommonFixture fixture)
        {
            Fixture = fixture;
            var persistenceStorage = fixture.GetStorageProvider(adoNetInvariant).GetAwaiter().GetResult();
            if(persistenceStorage != null)
            {
                PersistenceStorageTests = new CommonStorageTests(persistenceStorage);
            }
        }

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
            ((AdoNetGrainStorage)PersistenceStorageTests.Storage).StorageSerializationPicker = JsonPicker;
            return PersistenceStorageTests.Store_WriteRead(grainType, grainId, grainState);
        }

        internal Task Relational_Json_WriteReadStreaming(string grainType, GrainId grainId, GrainState<TestStateGeneric1<string>> grainState)
        {
            ((AdoNetGrainStorage)PersistenceStorageTests.Storage).StorageSerializationPicker = JsonStreamingPicker;
            return PersistenceStorageTests.Store_WriteRead(grainType, grainId, grainState);
        }

        internal Task Relational_Xml_WriteRead(string grainType, GrainId grainId, GrainState<TestStateGeneric1<string>> grainState)
        {
            ((AdoNetGrainStorage)PersistenceStorageTests.Storage).StorageSerializationPicker = XmlPicker;
            return PersistenceStorageTests.Store_WriteRead(grainType, grainId, grainState);
        }

        internal Task Relational_Xml_WriteReadStreaming(string grainType, GrainId grainId, GrainState<TestStateGeneric1<string>> grainState)
        {
            ((AdoNetGrainStorage)PersistenceStorageTests.Storage).StorageSerializationPicker = XmlStreamingPicker;
            return PersistenceStorageTests.Store_WriteRead(grainType, grainId, grainState);
        }

        internal async Task Relational_ChangeStorageFormatFromBinaryToJsonInMemory_WriteRead(string grainType, GrainId grainId, GrainState<TestState1> grainState)
        {
            //Use the default binary serializer and deserializer. Now the data in the storage is in binary format.
            var initialVersion = grainState.ETag;
            await PersistenceStorageTests.Store_WriteRead(grainType, grainId, grainState);
            var firstVersion = grainState.ETag;
            Assert.NotEqual(initialVersion, firstVersion);

            //Change the serializer and deserializer to a JSON one. The real world situation might be more complicated that the data
            //might not be in memory upon first read but the previous serializer format would need to used to retrieve data and the
            //new one to write and after that the new one used to both read and write data.
            //Change both the serializer and deserializer and do writing and reading once more just to be sure.
            ((AdoNetGrainStorage)PersistenceStorageTests.Storage).StorageSerializationPicker = JsonPicker;
            await PersistenceStorageTests.Store_WriteRead(grainType, grainId, grainState);
            var secondVersion = grainState.ETag;
            Assert.NotEqual(firstVersion, secondVersion);
        }
    }
}
