using System;
using System.Collections.Generic;
using Orleans;
using Orleans.Providers.Streams.Common;
using Orleans.Providers.Streams.Generator;
using Orleans.Runtime;
using Orleans.Streams;

namespace Distributed.Grains.Streaming
{
    public class TimeBoundEventGeneratorConfig : IStreamGeneratorConfig
    {
        public Type StreamGeneratorType => typeof(TimeBoundEventGenerator);

        public string StreamNamespace { get; set; }

        public int NumberOfStreams { get; set; }

        public Type PayloadType { get; set; } = typeof(object);

        public DateTime StartTime { get; set; }

        public DateTime EndTime { get; set; }
    }

    public class TimeBoundEventGenerator : IStreamGenerator
    {
        private DateTime _startTime;
        private DateTime _endTime;
        private readonly List<StreamId> _streamIds = new List<StreamId>();
        private int _sequenceId = 0;
        private object _payload;

        public void Configure(IServiceProvider serviceProvider, IStreamGeneratorConfig generatorConfig)
        {
            var config = (generatorConfig as TimeBoundEventGeneratorConfig) ?? throw new ArgumentException("Invalid configuration type", nameof(generatorConfig));
            _startTime = config.StartTime;
            _endTime = config.EndTime;
            for (var i = 0; i < config.NumberOfStreams; i++)
            {
                _streamIds.Add(StreamId.Create(config.StreamNamespace, Guid.NewGuid()));
            }
            _payload = Activator.CreateInstance(config.PayloadType);
        }

        public bool TryReadEvents(DateTime utcNow, int maxCount, out List<IBatchContainer> events)
        {
            if (utcNow < _startTime || utcNow > _endTime)
            {
                events = null;
                return false;
            }

            var streamId = _streamIds[_sequenceId % _streamIds.Count];
            var container = new GeneratedBatchContainer(streamId, _payload, new EventSequenceTokenV2(_sequenceId));
            events = new() { container };
            _sequenceId++;
            return true;
        }
    }
}
