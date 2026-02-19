using Orleans.Providers.Streams.Common;

namespace ServiceBus.Tests.MonitorTests
{
    public class EventHubReceiverMonitorForTesting : IQueueAdapterReceiverMonitor
    {
        public EventHubReceiverMonitorCounters CallCounters { get; } = new EventHubReceiverMonitorCounters();

        public void TrackInitialization(bool success, TimeSpan callTime, Exception exception)
        {
            if(success) Interlocked.Increment(ref this.CallCounters.TrackInitializationCallCounter);
        }

        public void TrackRead(bool success, TimeSpan callTime, Exception exception)
        {
            if (success) Interlocked.Increment(ref this.CallCounters.TrackReadCallCounter);
        }

        public void TrackMessagesReceived(long count, DateTime? oldestEnqueueTime, DateTime? newestEnqueueTime)
        {
            Interlocked.Increment(ref this.CallCounters.TrackMessagesReceivedCallCounter);
        }

        public void TrackShutdown(bool success, TimeSpan callTime, Exception exception)
        {
            Interlocked.Increment(ref this.CallCounters.TrackShutdownCallCounter);
        }
    }

    [Serializable]
    [Orleans.GenerateSerializer]
    public class EventHubReceiverMonitorCounters 
    {
        [Orleans.Id(0)]
        public int TrackInitializationCallCounter;
        [Orleans.Id(1)]
        public int TrackReadCallCounter;
        [Orleans.Id(2)]
        public int TrackMessagesReceivedCallCounter;
        [Orleans.Id(3)]
        public int TrackShutdownCallCounter;
    }
}
