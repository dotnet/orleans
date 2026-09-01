
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.Providers
{
    /// <summary>
    /// Memory stream queue grain. This grain works as a storage queue of event data. Enqueue and Dequeue operations are supported.
    /// the max event count sets the max storage limit to the queue.
    /// </summary>
    public class MemoryStreamQueueGrain : Grain, IMemoryStreamQueueGrain, IGrainMigrationParticipant
    {
        private Queue<MemoryMessageData> _eventQueue = new Queue<MemoryMessageData>();
        private long sequenceNumber = DateTime.UtcNow.Ticks;

        /// <summary>
        /// The maximum event count. 
        /// </summary>
        private const int MaxEventCount = 16384;

        /// <summary>
        /// Enqueues an event data. If the current total count reaches the max limit. throws an exception.
        /// </summary>
        /// <param name="data">The event data.</param>
        /// <returns>A <see cref="Task"/> representing the operation.</returns>
        public Task Enqueue(MemoryMessageData data)
            => Enqueue(data, CancellationToken.None);

        /// <inheritdoc/>
        public Task Enqueue(MemoryMessageData data, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_eventQueue.Count >= MaxEventCount)
            {
                throw new InvalidOperationException($"Can not enqueue since the count has reached its maximum of {MaxEventCount}");
            }
            data.SequenceNumber = sequenceNumber++;
            _eventQueue.Enqueue(data);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Dequeues up to a max amount of maxCount event data from the queue.
        /// </summary>
        /// <param name="maxCount">The maximum number of events to dequeue.</param>
        /// <returns>The dequeued events.</returns>
        public Task<List<MemoryMessageData>> Dequeue(int maxCount)
            => Dequeue(maxCount, CancellationToken.None);

        /// <inheritdoc/>
        public Task<List<MemoryMessageData>> Dequeue(int maxCount, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<MemoryMessageData> list = new List<MemoryMessageData>();

            for (int i = 0; i < maxCount && _eventQueue.Count > 0; ++i)
            {
                list.Add(_eventQueue.Dequeue());
            }

            return Task.FromResult(list);
        }

        void IGrainMigrationParticipant.OnDehydrate(IDehydrationContext dehydrationContext)
        {
            dehydrationContext.TryAddValue("queue", _eventQueue);
        }

        void IGrainMigrationParticipant.OnRehydrate(IRehydrationContext rehydrationContext)
        {
            if (rehydrationContext.TryGetValue<Queue<MemoryMessageData>>("queue", out var value))
            {
                _eventQueue = value;
            }
        }
    }
}
