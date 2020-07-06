using System;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using Orleans.Tests.SqlUtils;
using UnitTests.StorageTests.Relational.TestDataSets;
using Xunit;

namespace UnitTests.StorageTests.Relational
{
    /// <summary>
    /// Persistence tests for MySQL.
    /// </summary>
    /// <remarks>
    /// To duplicate these tests to any back-end, not just for relational, copy and paste this
    /// class, optionally remove <see cref="RelationalStorageTests"/> inheritance and implement a
    /// provider and environment setup as done in <see cref="CommonFixture"/> and how it delegates it.
    /// </remarks>
    [TestCategory("MySql"), TestCategory("Persistence")]
    public class MySqlStorageTests : RelationalStorageTests, IClassFixture<CommonFixture>
    {
        /// <summary>
        /// The storage invariant, storage ID, or ADO.NET invariant for this test set.
        /// </summary>
        private const string AdoNetInvariant = AdoNetInvariants.InvariantNameMySql;

        public MySqlStorageTests(CommonFixture commonFixture) : base(AdoNetInvariant, commonFixture)
        {
            //XUnit.NET will automatically call this constructor before every test method run.
            Skip.If(PersistenceStorageTests == null, $"Persistence storage not available for MySql.");
        }

        [SkippableFact]
        [TestCategory("Functional")]
        public async Task PersistenceStorage_WriteDuplicateFailsWithInconsistentStateException()
        {
            await Relational_WriteDuplicateFailsWithInconsistentStateException();
        }

        [SkippableFact]
        [TestCategory("Functional")]
        public async Task StorageDataSetGeneric_HashCollisionTests()
        {
            await Relational_HashCollisionTests();
        }

        [SkippableFact]
        [TestCategory("Functional")]
        public async Task WriteInconsistentFailsWithIncosistentStateException()
        {
            await Relational_WriteInconsistentFailsWithIncosistentStateException();
        }

        [SkippableFact]
        [TestCategory("Functional")]
        public async Task WriteRead100StatesInParallel()
        {
            await Relational_WriteReadWriteRead100StatesInParallel();
        }

        [SkippableFact]
        [TestCategory("Functional")]
        public async Task WriteReadCyrillic()
        {
            await PersistenceStorageTests.PersistenceStorage_Relational_WriteReadIdCyrillic();
        }

        [SkippableTheory, ClassData(typeof(StorageDataSetPlain<long>))]
        [TestCategory("Functional")]
        internal async Task ChangeStorageFormatFromBinaryToJson_WriteRead(string grainType, Func<IInternalGrainFactory, GrainReference> getGrainReference, GrainState<TestState1> grainState)
        {
            await this.Relational_ChangeStorageFormatFromBinaryToJsonInMemory_WriteRead(grainType, getGrainReference(this.Fixture.InternalGrainFactory), grainState);
        }

        [SkippableTheory, ClassData(typeof(StorageDataSet2CyrillicIdsAndGrainNames<string>))]
        [TestCategory("Functional")]
        internal async Task DataSet2_Cyrillic_WriteClearRead(string grainType, Func<IInternalGrainFactory, GrainReference> getGrain, GrainState<TestStateGeneric1<string>> grainState)
        {
            await this.PersistenceStorageTests.Store_WriteClearRead(grainType, getGrain(this.Fixture.InternalGrainFactory), grainState);
        }

        [SkippableTheory, ClassData(typeof(StorageDataSetPlain<long>))]
        [TestCategory("Functional")]
        internal async Task PersistenceStorage_StorageDataSetPlain_IntegerKey_WriteClearRead(string grainType, Func<IInternalGrainFactory, GrainReference> getGrainReference, GrainState<TestState1> grainState)
        {
            await this.PersistenceStorageTests.Store_WriteClearRead(grainType, getGrainReference(this.Fixture.InternalGrainFactory), grainState);
        }

        [SkippableTheory, ClassData(typeof(StorageDataSetGeneric<Guid, string>))]
        [TestCategory("Functional")]
        internal async Task StorageDataSetGeneric_GuidKey_Generic_WriteClearRead(string grainType, Func<IInternalGrainFactory, GrainReference> getGrainReference, GrainState<TestStateGeneric1<string>> grainState)
        {
            await this.PersistenceStorageTests.Store_WriteClearRead(grainType, getGrainReference(this.Fixture.InternalGrainFactory), grainState);
        }

        [SkippableTheory, ClassData(typeof(StorageDataSetGeneric<long, string>))]
        [TestCategory("Functional")]
        internal async Task StorageDataSetGeneric_IntegerKey_Generic_WriteClearRead(string grainType, Func<IInternalGrainFactory, GrainReference> getGrainReference, GrainState<TestStateGeneric1<string>> grainState)
        {
            await this.PersistenceStorageTests.Store_WriteClearRead(grainType, getGrainReference(this.Fixture.InternalGrainFactory), grainState);
        }

        [SkippableTheory, ClassData(typeof(StorageDataSetGeneric<string, string>))]
        [TestCategory("Functional")]
        internal async Task StorageDataSetGeneric_Json_WriteRead(string grainType, Func<IInternalGrainFactory, GrainReference> getGrainReference, GrainState<TestStateGeneric1<string>> grainState)
        {
            await this.Relational_Json_WriteRead(grainType, getGrainReference(this.Fixture.InternalGrainFactory), grainState);
        }

        [SkippableTheory, ClassData(typeof(StorageDataSetGeneric<string, string>))]
        [TestCategory("Functional")]
        internal async Task StorageDataSetGeneric_StringKey_Generic_WriteClearRead(string grainType, Func<IInternalGrainFactory, GrainReference> getGrainReference, GrainState<TestStateGeneric1<string>> grainState)
        {
            await this.PersistenceStorageTests.Store_WriteClearRead(grainType, getGrainReference(this.Fixture.InternalGrainFactory), grainState);
        }

        [SkippableTheory, ClassData(typeof(StorageDataSetGeneric<string, string>))]
        [TestCategory("Functional")]
        internal async Task StorageDataSetGeneric_Xml_WriteRead(string grainType, Func<IInternalGrainFactory, GrainReference> getGrainReference, GrainState<TestStateGeneric1<string>> grainState)
        {
            await this.Relational_Xml_WriteRead(grainType, getGrainReference(this.Fixture.InternalGrainFactory), grainState);
        }

        [SkippableTheory, ClassData(typeof(StorageDataSetGenericHuge<string, string>))]
        [TestCategory("Functional")]
        internal async Task StorageDataSetGenericHuge_Json_WriteReadStreaming(string grainType, Func<IInternalGrainFactory, GrainReference> getGrainReference, GrainState<TestStateGeneric1<string>> grainState)
        {
            await this.Relational_Json_WriteReadStreaming(grainType, getGrainReference(this.Fixture.InternalGrainFactory), grainState);
        }

        [SkippableTheory, ClassData(typeof(StorageDataSetGenericHuge<string, string>))]
        [TestCategory("Functional")]
        internal async Task StorageDataSetGenericHuge_Xml_WriteReadStreaming(string grainType, Func<IInternalGrainFactory, GrainReference> getGrainReference, GrainState<TestStateGeneric1<string>> grainState)
        {
            await this.Relational_Xml_WriteReadStreaming(grainType, getGrainReference(this.Fixture.InternalGrainFactory), grainState);
        }

        [SkippableTheory, ClassData(typeof(StorageDataSetPlain<Guid>))]
        [TestCategory("Functional")]
        internal async Task StorageDataSetPlain_GuidKey_WriteClearRead(string grainType, Func<IInternalGrainFactory, GrainReference> getGrainReference, GrainState<TestState1> grainState)
        {
            await this.PersistenceStorageTests.Store_WriteClearRead(grainType, getGrainReference(this.Fixture.InternalGrainFactory), grainState);
        }

        [SkippableTheory, ClassData(typeof(StorageDataSetPlain<string>))]
        [TestCategory("Functional")]
        internal async Task StorageDataSetPlain_StringKey_WriteClearRead(string grainType, Func<IInternalGrainFactory, GrainReference> getGrainReference, GrainState<TestState1> grainState)
        {
            await this.PersistenceStorageTests.Store_WriteClearRead(grainType, getGrainReference(this.Fixture.InternalGrainFactory), grainState);
        }
    }
}