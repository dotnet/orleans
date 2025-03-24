using Microsoft.Extensions.Options;
using Orleans.Clustering.AzureStorage;
using Orleans.Configuration;
using TestExtensions;

namespace UnitTests.AzureInfra
{
    public static class AzureStorageClusteringOptionsExtensions
    {
        public static void ConfigureTestDefaults(this OptionsBuilder<AzureStorageClusteringOptions> optionsBuilder)
            => optionsBuilder.Configure(options => options.ConfigureTestDefaults());

        public static void ConfigureTestDefaults(this AzureStorageClusteringOptions options)
        {
            if (TestDefaultConfiguration.UseAadAuthentication)
            {
                options.ConfigureTableServiceClient(TestDefaultConfiguration.TableEndpoint, TestDefaultConfiguration.TokenCredential);
            }
            else
            {
                options.ConfigureTableServiceClient(TestDefaultConfiguration.DataConnectionString);
            }
        }
    }
}
