using Orleans.Configuration;
using Tester.AzureUtils;
using TestExtensions;

namespace ServiceBus.Tests
{
    public static class EventHubConfigurationExtensions
    {
        public static EventHubOptions ConfigureTestDefaults(this EventHubOptions options, string eventHubName, string consumerGroup)
        {
            options.ConfigureEventHubConnection(TestDefaultConfiguration.EventHubConnectionString, eventHubName, consumerGroup);

            return options;
        }

        public static AzureTableStreamCheckpointerOptions ConfigureTestDefaults(this AzureTableStreamCheckpointerOptions options)
        {
            options.TableServiceClient = AzureStorageOperationOptionsExtensions.GetTableServiceClient();
            return options;
        }
    }
}
