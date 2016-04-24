using System;
using System.Threading.Tasks;
using Xunit;

namespace UnitTests.StorageTests.SQLAdapter.temp
{
    /// <summary>
    /// Persistence tests for SQL Server.
    /// </summary>
    public class SqlServerPersistenceStorageTests: IDisposable, IClassFixture<SqlServerFixture>
    {
        private CommonPersistenceStorageTests PersistenceStorageTests { get; set; }


        public SqlServerPersistenceStorageTests(SqlServerFixture sqlServerFixture)
        {
            var persistenceStorage = sqlServerFixture.GetPersistenceStorage();
            if(persistenceStorage != null)
            {
                PersistenceStorageTests = new CommonPersistenceStorageTests(persistenceStorage);
            }

            //XUnit.NET will automatically call this constructor before every test method run.
            Skip.If(persistenceStorage == null, $"Persistence storage not defined for SQL Server in {sqlServerFixture.Invariants.Environment.Environment}.");
        }


        public void Dispose()
        {
            //XUnit.NET will automatically call this after every test method run. There is no need to always call this method.            
        }

        //Is there a way to avoid duplicating these theories across backends?
        [Theory]
        [InlineData(3)]
        [InlineData(5)]
        [InlineData(6)]
        [TestCategory("Functional"), TestCategory("Temp_Persistence"), TestCategory("SqlServer")]
        public Task PersistenceStorage_SqlServer_Read(int someValue)
        {
            return PersistenceStorageTests.Store_Read();
        }


        [SkippableFact]
        [TestCategory("Functional"), TestCategory("Temp_Persistence"), TestCategory("SqlServer")]
        public Task Store_WriteRead()
        {
            return PersistenceStorageTests.Store_WriteRead();
        }


        [SkippableFact]
        [TestCategory("Functional"), TestCategory("Temp_Persistence"), TestCategory("SqlServer")]
        public Task Store_Delete()
        {
            return PersistenceStorageTests.Store_Delete();
        }
    }
}
