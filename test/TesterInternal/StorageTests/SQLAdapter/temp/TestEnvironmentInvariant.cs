using Orleans.SqlUtils;
using Orleans.TestingHost.Utils;
using System;
using System.Threading;
using UnitTests.General;

namespace UnitTests.StorageTests.SQLAdapter.temp
{
    /// <summary>
    /// This enforces the necessary environment invariants hold before starting to run tests.
    /// This servers as a class or object invariant for the test environment.
    /// </summary>
    public class TestEnvironmentInvariant
    {
        //TODO: Note in the following example some tests could be run in the same database too.

        /// <summary>
        /// 
        /// </summary>
        /// <remarks>The creation logic should take the connection string as a parameter. This would allow changing it according to then environment.
        /// It could be retrieved from an environment specific places, like Key Vault.</remarks>
        private Lazy<RelationalStorageForTesting> SqlServerMembershipTestStorage { get; } = new Lazy<RelationalStorageForTesting>(() => RelationalStorageForTesting.SetupInstance(AdoNetInvariants.InvariantNameSqlServer, "OrleansMembershipTests").GetAwaiter().GetResult(), LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>
        /// 
        /// </summary>
        /// <remarks>The creation logic should take the connection string as a parameter. This would allow changing it according to then environment.
        /// It could be retrieved from an environment specific places, like Key Vault.</remarks>
        private Lazy<RelationalStorageForTesting> SqlServerPersistenceTestStorage { get; } = new Lazy<RelationalStorageForTesting>(() => RelationalStorageForTesting.SetupInstance(AdoNetInvariants.InvariantNameSqlServer, "OrleansPersistenceTests").GetAwaiter().GetResult(), LazyThreadSafetyMode.ExecutionAndPublication);


        public TestEnvironmentInformation Environment { get; } = new TestEnvironmentInformation();


        public TestEnvironmentInvariant() { }


        public RelationalStorageForTesting EnsureSqlServerMembershipEnvironment()
        {                                                
            switch(Environment.Environment)
            {
                case(SupportedTestConfigurations.Development):
                {
                    return SqlServerPersistenceTestStorage.Value;
                }
                case(SupportedTestConfigurations.Jenkins):
                case(SupportedTestConfigurations.VisualStudioTeamServices):
                default: { break; }
            }                       

            return null;
        }


        public RelationalStorageForTesting EnsureSqlServerPersistenceEnvironment()
        {
            switch(Environment.Environment)
            {
                case(SupportedTestConfigurations.Development):
                {
                    return SqlServerMembershipTestStorage.Value;
                }
                case (SupportedTestConfigurations.Jenkins):
                case (SupportedTestConfigurations.VisualStudioTeamServices):
                default: { break; }
            }

            return null;
        }


        public bool TryEnsureMySqlEnvironment()
        {
            switch(Environment.Environment)
            {
                case(SupportedTestConfigurations.Development):
                case(SupportedTestConfigurations.Jenkins):
                case(SupportedTestConfigurations.VisualStudioTeamServices):
                default: { break; }
            }

            return true;
        }


        public bool TryEnsureTableStorageEnvironment()
        {                         
            //NOTE: In development environment Table Storage and Blob Storage would
            //share a common setup routine which would be starting the emulator and
            //perhaps cleaning up previous testing instances.
            switch(Environment.Environment)
            {
                case(SupportedTestConfigurations.Development):
                {
                    return StorageEmulator.TryStart();
                }
                case(SupportedTestConfigurations.Jenkins):
                case(SupportedTestConfigurations.VisualStudioTeamServices):
                default: { break; }
            }

            return false;
        }
    }
}
