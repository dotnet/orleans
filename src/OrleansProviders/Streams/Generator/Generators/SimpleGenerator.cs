
using System;
using System.Collections.Generic;
using Orleans.Hosting;
using Orleans.Providers.Streams.Common;
using Orleans.Streams;

namespace Orleans.Providers.Streams.Generator
{
    /// <summary>
    /// Simple Generator
    /// Generates a single stream of a configurable number of events.  One event per poll.
    /// </summary>
    internal class SimpleGenerator : IStreamGenerator
    {
        private SimpleGeneratorOptions options;
        private Guid streamGuid;
        private long sequenceId;
        private int generated;

        public void Configure(IServiceProvider serviceProvider, IStreamGeneratorConfig generatorConfig)
        {
            var cfg = generatorConfig as SimpleGeneratorOptions;
            if (cfg == null)
            {
                throw new ArgumentOutOfRangeException(nameof(generatorConfig));
            }
            this.options = cfg;
            this.sequenceId = DateTime.UtcNow.Ticks;
            this.generated = 0;
            this.streamGuid = Guid.NewGuid();
        }

        /// <summary>
        /// Until we've generated the configured number of events, return a single generated event
        /// </summary>
        public bool TryReadEvents(DateTime utcNow, int maxCount, out List<IBatchContainer> events)
        {
            events = new List<IBatchContainer>();
            if (generated >= this.options.EventsInStream)
            {
                return false;
            }

            for(int i=0; i< maxCount; i++)
            {
                if (!TryGenerateBatch(out GeneratedBatchContainer batch))
                    break;
                events.Add(batch);
            }

            return true;
        }
        
        private bool TryGenerateBatch(out GeneratedBatchContainer batch)
        {
            batch = null;
            if (generated >= this.options.EventsInStream)
            {
                return false;
            }
            generated++;
            var evt = new GeneratedEvent
            {
                // If this is the last event generated, mark it as such, so test grains know to report results.
                EventType = (generated != this.options.EventsInStream)
                        ? GeneratedEvent.GeneratedEventType.Fill
                        : GeneratedEvent.GeneratedEventType.Report
            };
            batch = new GeneratedBatchContainer(streamGuid, this.options.StreamNamespace, evt, new EventSequenceTokenV2(this.generated));
            return true;
        }
    }
}
