namespace Orleans.ServiceBus.Providers
{
    internal class EventHubConstants
    {
        public readonly static string StartOfStream =
#if NETSTANDARD
            Microsoft.Azure.EventHubs.PartitionReceiver.StartOfStream;
#else
            Microsoft.ServiceBus.Messaging.EventHubConsumerGroup.StartOfStream;
#endif
    }
}