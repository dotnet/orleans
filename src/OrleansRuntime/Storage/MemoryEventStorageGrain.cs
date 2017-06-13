using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.MultiCluster;
using System.Linq;

namespace Orleans.Storage
{

    /// <summary>
    /// Implementation class for the Event-Storage Grain used by In-memory event-storage provider
    /// <c>Orleans.Storage.MemoryEventStorage</c>
    /// </summary>
    /// <typeparam name="TEvent">The base class for the events</typeparam>
    [GlobalSingleInstance]
    internal class MemoryEventStorageGrain<TEvent> : Grain, IMemoryEventStorageGrain<TEvent>
    {
        private Dictionary<string, List<KeyValuePair<Guid, TEvent>>> eventStore;
        private Logger logger;

        public override Task OnActivateAsync()
        {
            eventStore = new Dictionary<string, List<KeyValuePair<Guid, TEvent>>>();
            base.DelayDeactivation(TimeSpan.FromDays(10 * 365)); // Delay Deactivation for MemoryEventStorageGrain virtually indefinitely.
            logger = GetLogger(GetType().Name);
            logger.Info("OnActivateAsync");
            return Task.CompletedTask;
        }

        public override Task OnDeactivateAsync()
        {
            logger.Info("OnDeactivateAsync");
            eventStore = null;
            return Task.CompletedTask;
        }

        private static int Within(int min, int x, int max)
        {
            return Math.Max(min, Math.Min(x, max));
        }

        public Task<int> GetVersion(string streamName)
        {
            List<KeyValuePair<Guid, TEvent>> log;
            if (!eventStore.TryGetValue(streamName, out log))
            {
                return Task.FromResult(0);
            }
            return Task.FromResult(log.Count);
        }

        public Task<EventStreamSegment<TEvent>> Load(string streamName, int startAtVersion = 0, int? endAtVersion = default(int?))
        {
            // check for invalid range parameters
            if (startAtVersion < 0)
            {
                throw new ArgumentException("invalid range", nameof(startAtVersion));
            }
            if (endAtVersion.HasValue && endAtVersion.Value < startAtVersion)
            {
                throw new ArgumentException("invalid range", nameof(endAtVersion));
            }

            List<KeyValuePair<Guid, TEvent>> log;
            if (!eventStore.TryGetValue(streamName, out log))
            {
                eventStore.Add(streamName, log = new List<KeyValuePair<Guid, TEvent>>());
            }

            if (startAtVersion > log.Count)
            {
                // we were asked for not-yet-existing events... 
                // return latest version and empty segment
                return Task.FromResult(new EventStreamSegment<TEvent>()
                {
                    StreamName = streamName,
                    Events = new List<KeyValuePair<Guid, TEvent>>(),
                    FromVersion = log.Count,
                    ToVersion = log.Count
                });
            }

            if (startAtVersion < 0)
                startAtVersion = 0;

            // if only a part of the log is retrieved, make a copy of the subrange
            if (startAtVersion > 0 || endAtVersion.HasValue)
            {
                int segmentLength;
                if (endAtVersion == null)
                {
                    // return the remainder of the log
                    segmentLength = log.Count - startAtVersion;
                }
                else
                {
                    // return the asked-for segment, but not more than exist, and not less than zero
                    segmentLength = Math.Max(0, Math.Min(log.Count, endAtVersion.Value) - startAtVersion);
                }
                log = log.GetRange(startAtVersion, segmentLength);
            }

            return Task.FromResult(new EventStreamSegment<TEvent>() {
                StreamName = streamName,
                Events = log,
                FromVersion = startAtVersion,
                ToVersion = startAtVersion + log.Count
            });
        }

        public Task<bool> Append(string streamName, IEnumerable<KeyValuePair<Guid, TEvent>> events, int? expectedVersion = null)
        {
            List<KeyValuePair<Guid, TEvent>> log;
            if (!eventStore.TryGetValue(streamName, out log))
            {
                eventStore.Add(streamName, log = new List<KeyValuePair<Guid, TEvent>>());
            }

            if (expectedVersion.HasValue && expectedVersion.Value != log.Count)
            {
                // idempotent appends return true
                if (expectedVersion.Value >= 0 && expectedVersion < log.Count)
                    return Task.FromResult(log[expectedVersion.Value].Key == events.First().Key);

                return Task.FromResult(false);
            }
            else
            {
                log.AddRange(events);
                return Task.FromResult(true);
            }
        }


        public Task<bool> Delete(string streamName, int? expectedVersion = null)
        {
            List<KeyValuePair<Guid, TEvent>> log;
            if (!eventStore.TryGetValue(streamName, out log))
            {
                // a non-existent stream is treated to be equivalent to having version zero
                return Task.FromResult(expectedVersion == null || expectedVersion.Value == 0);
            }
            else
            {
                if (expectedVersion != null && expectedVersion.Value != log.Count)
                {
                    return Task.FromResult(false);
                }
                else
                {
                    eventStore.Remove(streamName);
                    return Task.FromResult(true);
                }
            }
        }
    }
}
