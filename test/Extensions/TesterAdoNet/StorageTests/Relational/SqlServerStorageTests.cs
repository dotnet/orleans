using Orleans;
using Orleans.Runtime;
using Orleans.Tests.SqlUtils;
using System;
using System.Threading.Tasks;
using UnitTests.StorageTests.Relational.TestDataSets;
using Xunit;

namespace UnitTests.StorageTests.Relational
{
    public struct WriteReadTestResult { public long Count { get; set; } }

    /// <summary>
    /// Persistence tests for SQL Server.
    /// </summary>
    /// <remarks>To duplicate these tests to any back-end, not just for relational, copy and paste this class,
    /// optionally remove <see cref="RelationalStorageTests"/> inheritance and implement a provider and environment
    /// setup as done in <see cref="CommonFixture"/> and how it delegates it.</remarks>
    [TestCategory("AdoNet"), TestCategory("SqlServer"), TestCategory("Persistence")]
    public class SqlServerStorageTests: RelationalStorageTests, IClassFixture<CommonFixture>
    {
        /// <summary>
        /// The storage invariant, storage ID, or ADO.NET invariant for this test set.
        /// </summary>
        private const string AdoNetInvariant = AdoNetInvariants.InvariantNameSqlServer;

        public SqlServerStorageTests(CommonFixture commonFixture) : base(AdoNetInvariant, commonFixture)
        {
            //XUnit.NET will automatically call this constructor before every test method run.
            Skip.If(PersistenceStorageTests == null, $"Persistence storage not available for SqlServer.");
        }

        [SkippableFact]
        [TestCategory("Functional")]
        public async Task WriteReadCyrillic()
        {
            await PersistenceStorageTests.PersistenceStorage_Relational_WriteReadIdCyrillic();
        }

        [SkippableFact]
        [TestCategory("Functional")]
        public async Task WriteReadWriteRead100StatesInParallel()
        {
            await Relational_WriteReadWriteRead100StatesInParallel();
        }

        [SkippableFact]
        [TestCategory("Functional")]
        public async Task StorageDataSetGeneric_HashCollisionTests()
        {
            await Relational_HashCollisionTests();
        }

        [SkippableTheory, ClassData(typeof(StorageDataSetPlain<long>))]
        [TestCategory("Functional")]
        internal async Task ChangeStorageFormatFromBinaryToJson_WriteRead(int testNum)
        {
            var (grainType, getGrain, grainState) = StorageDataSetPlain<long>.GetTestData(testNum);
            await this.Relational_ChangeStorageFormatFromBinaryToJsonInMemory_WriteRead(grainType, getGrain, grainState);
        }

        [SkippableFact]
        [TestCategory("Functional")]
        public async Task WriteDuplicateFailsWithInconsistentStateException()
        {
            await Relational_WriteDuplicateFailsWithInconsistentStateException();
        }

        [SkippableFact]
        [TestCategory("Functional")]
        public async Task WriteInconsistentFailsWithIncosistentStateException()
        {
            await Relational_WriteInconsistentFailsWithIncosistentStateException();
        }

        [SkippableTheory, ClassData(typeof(StorageDataSetPlain<long>))]
        [TestCategory("Functional")]
        internal async Task StorageDataSetPlain_IntegerKey_WriteClearRead(int testNum)
        {
            var (grainType, getGrain, grainState) = StorageDataSetPlain<long>.GetTestData(testNum);
            await this.PersistenceStorageTests.Store_WriteClearRead(grainType, getGrain, grainState);
        }

        [SkippableTheory, ClassData(typeof(StorageDataSetPlain<Guid>))]
        [TestCategory("Functional")]
        internal async Task StorageDataSetPlain_GuidKey_WriteClearRead(int testNum)
        {
            var (grainType, getGrain, grainState) = StorageDataSetPlain<Guid>.GetTestData(testNum);
            await this.PersistenceStorageTests.Store_WriteClearRead(grainType, getGrain, grainState);
        }

        [SkippableTheory, ClassData(typeof(StorageDataSetPlain<string>))]
        [TestCategory("Functional")]
        internal async Task PersistenceStorage_StorageDataSetPlain_StringKey_WriteClearRead(int testNum)
        {
            var (grainType, getGrain, grainState) = StorageDataSetPlain<string>.GetTestData(testNum);
            await this.PersistenceStorageTests.Store_WriteClearRead(grainType, getGrain, grainState);
        }

        [SkippableTheory, ClassData(typeof(StorageDataSet2CyrillicIdsAndGrainNames<string>))]
        [TestCategory("Functional")]
        internal async Task DataSet2_Cyrillic_WriteClearRead(int testNum)
        {
            var (grainType, getGrain, grainState) = StorageDataSet2CyrillicIdsAndGrainNames<string>.GetTestData(testNum);
            await this.PersistenceStorageTests.Store_WriteClearRead(grainType, getGrain, grainState);
        }

        [SkippableTheory, ClassData(typeof(StorageDataSetGeneric<long, string>))]
        [TestCategory("Functional")]
        internal async Task StorageDataSetGeneric_IntegerKey_Generic_WriteClearRead(int testNum)
        {
            var (grainType, getGrain, grainState) = StorageDataSetGeneric<long, string>.GetTestData(testNum);
            await this.PersistenceStorageTests.Store_WriteClearRead(grainType, getGrain, grainState);
        }

        [SkippableTheory, ClassData(typeof(StorageDataSetGeneric<Guid, string>))]
        [TestCategory("Functional")]
        internal async Task StorageDataSetGeneric_GuidKey_Generic_WriteClearRead(int testNum)
        {
            var (grainType, getGrain, grainState) = StorageDataSetGeneric<Guid, string>.GetTestData(testNum);
            await this.PersistenceStorageTests.Store_WriteClearRead(grainType, getGrain, grainState);
        }

        [SkippableTheory, ClassData(typeof(StorageDataSetGeneric<string, string>))]
        [TestCategory("Functional")]
        internal async Task StorageDataSetGeneric_StringKey_Generic_WriteClearRead(int testNum)
        {
            var (grainType, getGrain, grainState) = StorageDataSetGeneric<string, string>.GetTestData(testNum);
            await this.PersistenceStorageTests.Store_WriteClearRead(grainType, getGrain, grainState);
        }

        [SkippableTheory, ClassData(typeof(StorageDataSetGeneric<string, string>))]
        [TestCategory("Functional")]
        internal async Task StorageDataSetGeneric_Json_WriteRead(int testNum)
        {
            var (grainType, getGrain, grainState) = StorageDataSetGeneric<string, string>.GetTestData(testNum);
            var grainReference = getGrain;
            await this.Relational_Json_WriteRead(grainType, grainReference, grainState);
        }

        [SkippableTheory, ClassData(typeof(StorageDataSetGenericHuge<string, string>))]
        [TestCategory("Functional")]
        internal async Task StorageDataSetGenericHuge_Json_WriteReadStreaming(int testNum)
        {
            var (grainType, getGrain, grainState) = StorageDataSetGenericHuge<string, string>.GetTestData(testNum);
            await this.Relational_Json_WriteReadStreaming(grainType, getGrain, grainState);
        }

        [SkippableTheory, ClassData(typeof(StorageDataSetGeneric<string, string>))]
        [TestCategory("Functional")]
        internal async Task StorageDataSetGeneric_Xml_WriteRead(int testNum)
        {
            var (grainType, getGrain, grainState) = StorageDataSetGeneric<string, string>.GetTestData(testNum);
            await this.Relational_Xml_WriteRead(grainType, getGrain, grainState);
        }

        [SkippableTheory, ClassData(typeof(StorageDataSetGenericHuge<string, string>))]
        [TestCategory("Functional")]
        internal async Task StorageDataSetGenericHuge_Xml_WriteReadStreaming(int testNum)
        {
            var (grainType, getGrain, grainState) = StorageDataSetGenericHuge<string, string>.GetTestData(testNum);
            await this.Relational_Xml_WriteReadStreaming(grainType, getGrain, grainState);
        }
    }
}