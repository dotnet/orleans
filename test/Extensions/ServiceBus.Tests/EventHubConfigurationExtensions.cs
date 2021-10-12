using Azure.Identity;
using Orleans.Configuration;
using TestExtensions;

namespace ServiceBus.Tests
{
    public static class EventHubConfigurationExtensions
    {
        public static EventHubOptions ConfigureTestDefaults(this EventHubOptions options, string eventHubName, string consumerGroup)
        {
            if (TestDefaultConfiguration.UseAadAuthentication)
            {
                options.ConfigureEventHubConnection(TestDefaultConfiguration.EventHubFullyQualifiedNamespace, eventHubName, consumerGroup, new DefaultAzureCredential());
            }
            else
            {
                options.ConfigureEventHubConnection(TestDefaultConfiguration.EventHubConnectionString, eventHubName, consumerGroup);
            }

            return options;
        }
    }
}
