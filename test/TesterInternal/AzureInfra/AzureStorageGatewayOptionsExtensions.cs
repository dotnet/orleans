using Microsoft.Extensions.Options;
using Orleans.Clustering.AzureStorage;
using TestExtensions;

namespace UnitTests.AzureInfra
{
    public static class AzureStorageGatewayOptionsExtensions
    {
        public static void ConfigureTestDefaults(this OptionsBuilder<AzureStorageGatewayOptions> optionsBuilder)
            => optionsBuilder.Configure(options => options.ConfigureTestDefaults());

        public static void ConfigureTestDefaults(this AzureStorageGatewayOptions options)
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
