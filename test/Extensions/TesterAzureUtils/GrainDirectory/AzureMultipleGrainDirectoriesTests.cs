using Orleans.TestingHost;
using TestExtensions;
using Tester.AzureUtils;
using UnitTests.Grains.Directories;

namespace Tester.Directories
{
    /// <summary>
    /// Tests for custom grain directory functionality using Azure Table Storage as the directory backend.
    /// </summary>
    [TestCategory("AzureStorage")]
    public class AzureMultipleGrainDirectoriesTests : MultipleGrainDirectoriesTests
    {
        public class SiloConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder siloBuilder)
            {
                siloBuilder.AddAzureTableGrainDirectory(
                    CustomDirectoryGrain.DIRECTORY,
                    options => options.TableServiceClient = AzureStorageOperationOptionsExtensions.GetTableServiceClient());
            }
        }

        protected override void CheckPreconditionsOrThrow() => TestUtils.CheckForAzureStorage();

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            EnsurePreconditionsMet();

            base.ConfigureTestCluster(builder);
            builder.AddSiloBuilderConfigurator<SiloConfigurator>();
        }
    }
}
