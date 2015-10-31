/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Collections.Generic;
using Orleans.Providers.Streams.Common;
using Orleans.Streams;
using TestGrains;

namespace Tester.TestStreamProviders.Generator.Generators
{
    /// <summary>
    /// Simple Generator
    /// Generates a single stream of a configurable number of events.  One event per poll.
    /// </summary>
    internal class SimpleGenerator : IStreamGenerator
    {
        private SimpleGeneratorConfig config;
        private Guid streamGuid;
        private int sequenceId;

        public void Configure(IServiceProvider serviceProvider, IStreamGeneratorConfig generatorConfig)
        {
            var cfg = generatorConfig as SimpleGeneratorConfig;
            if (cfg == null)
            {
                throw new ArgumentOutOfRangeException("generatorConfig");
            }
            config = cfg;
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
            if (sequenceId >= config.EventsInStream)
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
                EventType = (sequenceId != config.EventsInStream)
                        ? GeneratedEvent.GeneratedEventType.Fill
                        : GeneratedEvent.GeneratedEventType.End,
            };
            return new GeneratedBatchContainer(streamGuid, config.StreamNamespace, evt, new EventSequenceToken(sequenceId));
        }
    }
}
