using Orleans;
using Orleans.Runtime.MembershipService;
using Orleans.SqlUtils.StorageProvider;
using Orleans.Storage;
using System;

namespace UnitTests.StorageTests.SQLAdapter.temp
{
    /// <summary>
    /// Sets up relational database. 
    /// </summary>
    public class SqlServerFixture: IDisposable
    {               
        public TestEnvironmentInvariant Invariants { get; } = new TestEnvironmentInvariant();

        public SqlServerFixture()
        {
            //Call before tests using this fixture will be run.            
        }


        public void Dispose()
        {
            //Call after tests using this fixture have been run.            
        }


        /// <summary>
        /// Returns a correct implementation of the persistence provider according to environment variables.
        /// This could be SQL Server or MySQL or some other backend. Either requested by parameter
        /// </summary>
        public IStorageProvider GetPersistenceStorage()
        {
            //Make sure the environment invariants hold before trying to give a functioning SUT instantiation.
            Invariants.EnsureSqlServerPersistenceEnvironment();

            //TODO: This doesn't function yet. Uncomment null to check skipped tests if they aren't defined
            //for the the given environment or if the feature hasn't been implemented yet.
            //return null;
            return new SqlStorageProvider();
        }


        /// <summary>
        /// Returns a correct implementation of the persistence provider according to environment variables.
        /// This could be SQL Server or MySQL or some other backend. Either requested by parameter
        /// </summary>
        public IMembershipTable GetMembershipTable()
        {
            //Make sure the environment invariants hold before trying to give a functioning SUT instantiation.
            Invariants.EnsureSqlServerMembershipEnvironment();

            //TODO: Not plugged in yet, for illustrative purposes.
            return new SqlMembershipTable();
        }
    }
}
