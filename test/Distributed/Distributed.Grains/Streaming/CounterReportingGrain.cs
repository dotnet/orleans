using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Distributed.GrainInterfaces.Streaming;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Hosting;
using Orleans.Providers.Streams.Generator;
using Orleans.Streams;
using Orleans.Streams.Core;

namespace Distributed.Grains.Streaming
{
    public class CounterGrain : Grain, ICounterGrain
    {
        private readonly ReportingOptions _options;
        private readonly List<IGrainWithCounter> _trackedGrains = new();

        public CounterGrain(IOptions<ReportingOptions> options)
        {
            Debugger.Break();
            _options = options.Value;
        }

        public Task<TimeSpan> GetRunDuration() => Task.FromResult(TimeSpan.FromSeconds(_options.Duration));

        public async Task<int> GetTotalCounterValue(string counterName)
        {
            var counter = 0;
            foreach (var grain in _trackedGrains)
            {
                counter += await grain.GetCounterValue(counterName);
            }
            return counter;
        }

        public Task Track(IGrainWithCounter grain)
        {
            _trackedGrains.Add(grain);
            return Task.CompletedTask;
        }

        public Task<TimeSpan> WaitTimeForReport()
        {
            var ts = _options.ReportAt - DateTime.UtcNow;
            return ts < TimeSpan.Zero
                ? Task.FromResult(TimeSpan.Zero)
                : Task.FromResult(ts);
        }
    }
}
