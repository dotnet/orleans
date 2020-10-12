
using System;
using System.Collections.Generic;
using Orleans.Hosting;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
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
        private StreamId streamId;
        private int sequenceId;

        public void Configure(IServiceProvider serviceProvider, IStreamGeneratorConfig generatorConfig)
        {
            var cfg = generatorConfig as SimpleGeneratorOptions;
            if (cfg == null)
            {
                throw new ArgumentOutOfRangeException(nameof(generatorConfig));
            }
            options = cfg;
            sequenceId = 0;
            streamId = StreamId.Create(options.StreamNamespace, Guid.NewGuid());
        }

        /// <summary>
        /// Until we've generated the configured number of events, return a single generated event
        /// </summary>
        public bool TryReadEvents(DateTime utcNow, int maxCount, out List<IBatchContainer> events)
        {
            events = new List<IBatchContainer>();
            if (sequenceId >= this.options.EventsInStream)
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
            if (sequenceId >= this.options.EventsInStream)
            {
                return false;
            }
            sequenceId++;
            var evt = new GeneratedEvent
            {
                // If this is the last event generated, mark it as such, so test grains know to report results.
                EventType = (sequenceId != this.options.EventsInStream)
                        ? GeneratedEvent.GeneratedEventType.Fill
                        : GeneratedEvent.GeneratedEventType.Report
            };
            batch = new GeneratedBatchContainer(streamId, evt, new EventSequenceTokenV2(sequenceId));
            return true;
        }
    }
}
