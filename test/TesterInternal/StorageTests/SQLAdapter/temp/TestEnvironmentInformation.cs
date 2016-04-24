namespace UnitTests.StorageTests.SQLAdapter.temp
{
    /// <summary>
    /// Enumeration of the supported test environment configurations.
    /// </summary>
    public enum SupportedTestConfigurations
    {
        None                        = 0,
        Development                 = 1,
        Jenkins                     = 2,
        VisualStudioTeamServices    = 3
    }


    /// <summary>
    /// Gathers information to be utilized in variables from path and elsewhere.
    /// </summary>
    public sealed class TestEnvironmentInformation
    {
        public SupportedTestConfigurations Environment { get; private set; }

        public bool HasSqlServer { get; private set; }

        public bool HasMySql { get; private set; }

        public bool HasAzureStorageEmulator { get; private set; }


        public TestEnvironmentInformation()
        {
            //This is hardcoded here, but in reality would be read from, e.g.,
            //environment variables and elsewhere.
            Environment = SupportedTestConfigurations.Development;
            HasSqlServer = true;
            HasMySql = true;
            HasAzureStorageEmulator = true;
        }
    }
}
