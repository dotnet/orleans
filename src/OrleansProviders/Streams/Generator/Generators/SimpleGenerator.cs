
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
            streamGuid = Guid.NewGuid();
        }

        /// <summary>
        /// Untill we've generated the configured number of events, return a single generated event
        /// </summary>
        /// <param name="utcNow"></param>
        /// <param name="events"></param>
        /// <returns></returns>
        public bool TryReadEvents(DateTime utcNow, out List<IBatchContainer> events)
        {
            events = new List<IBatchContainer>();
            if (sequenceId >= this.options.EventsInStream)
            {
                return false;
            }

            events.Add(GenerateBatch());

            return true;
        }
        
        private GeneratedBatchContainer GenerateBatch()
        {
            sequenceId++;
            var evt = new GeneratedEvent
            {
                // If this is the last event generated, mark it as such, so test grains know to report results.
                EventType = (sequenceId != this.options.EventsInStream)
                        ? GeneratedEvent.GeneratedEventType.Fill
                        : GeneratedEvent.GeneratedEventType.Report
            };
            return new GeneratedBatchContainer(streamGuid, this.options.StreamNamespace, evt, new EventSequenceTokenV2(sequenceId));
        }
    }
}
