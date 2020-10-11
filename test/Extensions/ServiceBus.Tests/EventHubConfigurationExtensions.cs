using Azure.Identity;
using Orleans.Configuration;
using TestExtensions;

namespace ServiceBus.Tests
{
    public static class EventHubConfigurationExtensions
    {
        public static EventHubOptions ConfigureTestDefaults(this EventHubOptions options)
        {
            if (TestDefaultConfiguration.UseAadAuthentication)
            {
                options.FullyQualifiedNamespace = TestDefaultConfiguration.EventHubFullyQualifiedNamespace;
                options.TokenCredential = new DefaultAzureCredential();
            }
            else
            {
                options.ConnectionString = TestDefaultConfiguration.EventHubConnectionString;
            }

            return options;
        }
    }
}
