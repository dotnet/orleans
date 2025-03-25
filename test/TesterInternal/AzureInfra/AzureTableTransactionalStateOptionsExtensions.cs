using Microsoft.Extensions.Options;
using Orleans.Configuration;
using TestExtensions;

namespace TesterInternal.AzureInfra
{
    public static class AzureTableTransactionalStateOptionsExtensions
    {
        public static void ConfigureTestDefaults(this OptionsBuilder<AzureTableTransactionalStateOptions> optionsBuilder)
            => optionsBuilder.Configure(options => options.ConfigureTestDefaults());

        public static void ConfigureTestDefaults(this AzureTableTransactionalStateOptions options)
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
