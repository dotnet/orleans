using Orleans.Configuration;
using Tester.AzureUtils;
using TestExtensions;

namespace ServiceBus.Tests
{
    public static class EventHubConfigurationExtensions
    {
        public static EventHubOptions ConfigureTestDefaults(this EventHubOptions options, string eventHubName, string consumerGroup)
        {
            if (TestDefaultConfiguration.UseAadAuthentication)
            {
                options.ConfigureEventHubConnection(TestDefaultConfiguration.EventHubFullyQualifiedNamespace!, eventHubName, consumerGroup, TestDefaultConfiguration.TokenCredential); // AAD test setup supplies the Event Hubs namespace.
            }
            else
            {
                options.ConfigureEventHubConnection(TestDefaultConfiguration.EventHubConnectionString!, eventHubName, consumerGroup); // Connection-string test setup supplies this value.
            }

            return options;
        }

        public static AzureTableStreamCheckpointerOptions ConfigureTestDefaults(this AzureTableStreamCheckpointerOptions options)
        {
            options.TableServiceClient = AzureStorageOperationOptionsExtensions.GetTableServiceClient();
            return options;
        }
    }
}
