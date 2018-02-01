using Orleans.Providers.Streams.Common;
using System;
using System.Threading;

namespace ServiceBus.Tests.MonitorTests
{
    public class EventHubReceiverMonitorForTesting : IQueueAdapterReceiverMonitor
    {
        public static EventHubReceiverMonitorForTesting Instance = new EventHubReceiverMonitorForTesting();
        public EventHubReceiverMonitorCounters CallCounters;
        private EventHubReceiverMonitorForTesting()
        {
            this.CallCounters = new EventHubReceiverMonitorCounters();
        }
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
    public class EventHubReceiverMonitorCounters 
    {
        public int TrackInitializationCallCounter = 0;
        public int TrackReadCallCounter = 0;
        public int TrackMessagesReceivedCallCounter = 0;
        public int TrackShutdownCallCounter = 0;
    }
}
