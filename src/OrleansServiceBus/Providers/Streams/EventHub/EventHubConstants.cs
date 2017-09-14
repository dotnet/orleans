namespace Orleans.ServiceBus.Providers
{
    internal class EventHubConstants
    {
        public readonly static string StartOfStream =
#if !BUILD_FLAVOR_LEGACY
            Microsoft.Azure.EventHubs.PartitionReceiver.StartOfStream;
#else
            Microsoft.ServiceBus.Messaging.EventHubConsumerGroup.StartOfStream;
#endif
    }
}