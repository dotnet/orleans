using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime.Configuration;

namespace Orleans.Runtime.Messaging
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Naming", "CA1711:IdentifiersShouldNotHaveIncorrectSuffix")]
    internal class InboundMessageQueue : IInboundMessageQueue
    {
        private readonly BlockingCollection<Message>[] messageQueues;

        private readonly ILogger log;

        private readonly QueueTrackingStatistic[] queueTracking;

        private readonly StatisticsLevel statisticsLevel;

        private bool disposed;

        /// <inheritdoc />
        public int Count
        {
            get
            {
                int n = 0;
                foreach (var queue in this.messageQueues)
                {
                    n += queue.Count;
                }
                
                return n;
            }
        }

        internal InboundMessageQueue(ILoggerFactory loggerFactory, IOptions<StatisticsOptions> statisticsOptions)
        {
            int n = Enum.GetValues(typeof(Message.Categories)).Length;
            this.messageQueues = new BlockingCollection<Message>[n];
            this.queueTracking = new QueueTrackingStatistic[n];
            int i = 0;
            this.statisticsLevel = statisticsOptions.Value.CollectionLevel;
            foreach (var category in Enum.GetValues(typeof(Message.Categories)))
            {
                this.messageQueues[i] = new BlockingCollection<Message>();
                if (this.statisticsLevel.CollectQueueStats())
                {
                    var queueName = "IncomingMessageAgent." + category;
                    this.queueTracking[i] = new QueueTrackingStatistic(queueName, statisticsOptions);
                    this.queueTracking[i].OnStartExecution();
                }

                i++;
            }

            this.log = loggerFactory.CreateLogger<InboundMessageQueue>();
        }

        /// <inheritdoc />
        public void Stop()
        {
            foreach (var q in this.messageQueues)
            {
                q.CompleteAdding();
            }

            if (!this.statisticsLevel.CollectQueueStats())
            {
                return;
            }

            foreach (var q in this.queueTracking)
            {
                q.OnStopExecution();
            }
        }

        /// <inheritdoc />
        public void PostMessage(Message msg)
        {
            this.messageQueues[(int)msg.Category].Add(msg);

            if (this.log.IsEnabled(LogLevel.Trace))
            {
                this.log.Trace("Queued incoming {0} message", msg.Category.ToString());
            }
        }

        /// <inheritdoc />
        public Message WaitMessage(Message.Categories type)
        {
            try
            {
                return this.messageQueues[(int)type].Take();
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (this.disposed) return;
            lock (this.messageQueues)
            {
                if (this.disposed) return;

                this.Stop();

                foreach (var q in this.messageQueues)
                {
                    q.Dispose();
                }

                this.disposed = true;
            }
        }
    }
}
