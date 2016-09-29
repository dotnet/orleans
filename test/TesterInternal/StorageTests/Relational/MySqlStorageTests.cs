using Orleans;
using Orleans.Runtime;
using Orleans.SqlUtils;
using System;
using System.Threading.Tasks;
using UnitTests.StorageTests.Relational.TestDataSets;
using Xunit;


namespace UnitTests.StorageTests.Relational
{
    /// <summary>
    /// Persistence tests for MySQL.
    /// </summary>
    /// <remarks>To duplicate these tests to any back-end, not just for relational, copy and paste this class,
    /// optionally remove <see cref="RelationalStorageTests"/> inheritance and implement a provider and environment
    /// setup as done in <see cref="CommonFixture"/> and how it delegates it.</remarks>
    public class MySqlStorageTests: RelationalStorageTests, IDisposable, IClassFixture<CommonFixture>
    {
        /// <summary>
        /// The storage invariant, storage ID, or ADO.NET invariant for this test set.
        /// </summary>
        private const string AdoNetInvariant = AdoNetInvariants.InvariantNameMySql;

        /// <summary>
        /// The category of this back-end vendor.
        /// </summary>
        public const string VendorCategory = "MySql";


        public MySqlStorageTests(CommonFixture commonFixture) : base(AdoNetInvariant, commonFixture)
        {
            //XUnit.NET will automatically call this constructor before every test method run.
            Skip.If(PersistenceStorageTests == null, $"Persistence storage not available for {VendorCategory}.");
        }


        public void Dispose()
        {
            //XUnit.NET will automatically call this after every test method run. There is no need to always call this method.
        }

        [SkippableFact]
        [TestCategory("Functional"), TestCategory("Persistence"), TestCategory(VendorCategory)]
        internal async Task WriteReadCyrillic()
        {
            await PersistenceStorageTests.PersistenceStorage_Relational_WriteReadIdCyrillic();
        }


        [SkippableFact]
        [TestCategory("Functional"), TestCategory("Persistence"), TestCategory(VendorCategory)]
        internal async Task WriteRead100StatesInParallel()
        {
            await PersistenceStorageTests.PersistenceStorage_WriteReadWriteRead100StatesInParallel();
        }

        [SkippableFact]
        [TestCategory("Functional"), TestCategory("Persistence"), TestCategory(VendorCategory)]
        internal async Task StorageDataSetGeneric_HashCollisionTests()
        {
            await Relational_HashCollisionTests();
        }


        [SkippableTheory, ClassData(typeof(StorageDataSetPlain<long>))]
        [TestCategory("Functional"), TestCategory("Persistence"), TestCategory(VendorCategory)]
        internal async Task ChangeStorageFormatFromBinaryToJson_WriteRead(string grainType, GrainReference grainReference, GrainState<TestState1> grainState)
        {
            await Relational_ChangeStorageFormatFromBinaryToJsonInMemory_WriteRead(grainType, grainReference, grainState);
        }


        [SkippableFact]
        [TestCategory("Functional"), TestCategory("Persistence"), TestCategory(VendorCategory)]
        internal async Task PersistenceStorage_WriteDuplicateFailsWithInconsistentStateException()
        {
            await Relational_WriteDuplicateFailsWithInconsistentStateException();
        }


        [SkippableFact]
        [TestCategory("Functional"), TestCategory("Persistence"), TestCategory(VendorCategory)]
        internal async Task WriteInconsistentFailsWithIncosistentStateException()
        {
            await Relational_WriteInconsistentFailsWithIncosistentStateException();
        }


        [SkippableTheory, ClassData(typeof(StorageDataSetPlain<long>))]
        [TestCategory("Functional"), TestCategory("Persistence"), TestCategory(VendorCategory)]
        internal async Task PersistenceStorage_StorageDataSetPlain_IntegerKey_WriteClearRead(string grainType, GrainReference grainReference, GrainState<TestState1> grainState)
        {
            await PersistenceStorageTests.Store_WriteClearRead(grainType, grainReference, grainState);
        }


        [SkippableTheory, ClassData(typeof(StorageDataSetPlain<Guid>))]
        [TestCategory("Functional"), TestCategory("Persistence"), TestCategory(VendorCategory)]
        internal async Task StorageDataSetPlain_GuidKey_WriteClearRead(string grainType, GrainReference grainReference, GrainState<TestState1> grainState)
        {
            await PersistenceStorageTests.Store_WriteClearRead(grainType, grainReference, grainState);
        }


        [SkippableTheory, ClassData(typeof(StorageDataSetPlain<string>))]
        [TestCategory("Functional"), TestCategory("Persistence"), TestCategory(VendorCategory)]
        internal async Task StorageDataSetPlain_StringKey_WriteClearRead(string grainType, GrainReference grainReference, GrainState<TestState1> grainState)
        {
            await PersistenceStorageTests.Store_WriteClearRead(grainType, grainReference, grainState);
        }


        [SkippableTheory, ClassData(typeof(StorageDataSet2CyrillicIdsAndGrainNames<string>))]
        [TestCategory("Functional"), TestCategory("Persistence"), TestCategory(VendorCategory)]
        internal async Task DataSet2_Cyrillic_WriteClearRead(string grainType, GrainReference grainReference, GrainState<TestStateGeneric1<string>> grainState)
        {
            await PersistenceStorageTests.Store_WriteClearRead(grainType, grainReference, grainState);
        }


        [SkippableTheory, ClassData(typeof(StorageDataSetGeneric<long, string>))]
        [TestCategory("Functional"), TestCategory("Persistence"), TestCategory(VendorCategory)]
        internal async Task StorageDataSetGeneric_IntegerKey_Generic_WriteClearRead(string grainType, GrainReference grainReference, GrainState<TestStateGeneric1<string>> grainState)
        {
            await PersistenceStorageTests.Store_WriteClearRead(grainType, grainReference, grainState);
        }


        [SkippableTheory, ClassData(typeof(StorageDataSetGeneric<Guid, string>))]
        [TestCategory("Functional"), TestCategory("Persistence"), TestCategory(VendorCategory)]
        internal async Task StorageDataSetGeneric_GuidKey_Generic_WriteClearRead(string grainType, GrainReference grainReference, GrainState<TestStateGeneric1<string>> grainState)
        {
            await PersistenceStorageTests.Store_WriteClearRead(grainType, grainReference, grainState);
        }


        [SkippableTheory, ClassData(typeof(StorageDataSetGeneric<string, string>))]
        [TestCategory("Functional"), TestCategory("Persistence"), TestCategory(VendorCategory)]
        internal async Task StorageDataSetGeneric_StringKey_Generic_WriteClearRead(string grainType, GrainReference grainReference, GrainState<TestStateGeneric1<string>> grainState)
        {
            await PersistenceStorageTests.Store_WriteClearRead(grainType, grainReference, grainState);
        }


        [SkippableTheory, ClassData(typeof(StorageDataSetGeneric<string, string>))]
        [TestCategory("Functional"), TestCategory("Persistence"), TestCategory("MySql")]
        internal async Task StorageDataSetGeneric_Json_WriteRead(string grainType, GrainReference grainReference, GrainState<TestStateGeneric1<string>> grainState)
        {
            await Relational_Json_WriteRead(grainType, grainReference, grainState);
        }


        [SkippableTheory, ClassData(typeof(StorageDataSetGenericHuge<string, string>))]
        [TestCategory("Functional"), TestCategory("Persistence"), TestCategory("MySql")]
        internal async Task StorageDataSetGenericHuge_Json_WriteReadStreaming(string grainType, GrainReference grainReference, GrainState<TestStateGeneric1<string>> grainState)
        {
            await Relational_Json_WriteReadStreaming(grainType, grainReference, grainState);
        }


        [SkippableTheory, ClassData(typeof(StorageDataSetGeneric<string, string>))]
        [TestCategory("Functional"), TestCategory("Persistence"), TestCategory(VendorCategory)]
        internal async Task StorageDataSetGeneric_Xml_WriteRead(string grainType, GrainReference grainReference, GrainState<TestStateGeneric1<string>> grainState)
        {
            await Relational_Xml_WriteRead(grainType, grainReference, grainState);
        }


        [SkippableTheory, ClassData(typeof(StorageDataSetGenericHuge<string, string>))]
        [TestCategory("Functional"), TestCategory("Persistence"), TestCategory(VendorCategory)]
        internal async Task StorageDataSetGenericHuge_Xml_WriteReadStreaming(string grainType, GrainReference grainReference, GrainState<TestStateGeneric1<string>> grainState)
        {
            await Relational_Xml_WriteReadStreaming(grainType, grainReference, grainState);
        }
    }
}