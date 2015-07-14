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
using System.Diagnostics;
using System.Threading.Tasks;

using Orleans.Providers;
using Orleans.Runtime;

namespace Orleans.Storage
{
    /// <summary>
    /// This is a simple in-memory implementation of a storage provider which presents fixed latency of storage calls.
    /// This class is useful for system testing and investigation of the effects of storage latency.
    /// </summary>
    /// <remarks>
    /// This storage provider is ONLY intended for simple in-memory Test scenarios.
    /// This class should NOT be used in Production environment, 
    ///  because [by-design] it does not provide any resilience 
    ///  or long-term persistence capabilities.
    /// </remarks>
    /// <example>
    /// Example configuration for this storage provider in OrleansConfiguration.xml file:
    /// <code>
    /// &lt;OrleansConfiguration xmlns="urn:orleans">
    ///   &lt;Globals>
    ///     &lt;StorageProviders>
    ///       &lt;Provider Type="Orleans.Storage.MemoryStorageWithLatency" Name="MemoryStoreWithLatency" Latency="00:00:00.500"/>
    ///   &lt;/StorageProviders>
    /// </code>
    /// </example>
    [DebuggerDisplay("MemoryStore:{Name},WithLatency:{latency}")]
    public class MemoryStorageWithLatency : MemoryStorage
    {
        internal const string LATENCY_PARAM_STRING = "Latency";
        internal const string MOCK_CALLS_PARAM_STRING = "MockCalls";
        internal static readonly TimeSpan DefaultLatency = TimeSpan.FromMilliseconds(200);

        private const int NUM_STORE_GRAINS = 1;
        
        private TimeSpan latency;
        private bool mockCallsOnly;

        public MemoryStorageWithLatency()
            : base(NUM_STORE_GRAINS)
        {
        }

        public override async Task Init(string name, IProviderRuntime providerRuntime, IProviderConfiguration config)
        {
            await base.Init(name, providerRuntime, config);

            string latencyParam = config.Properties[LATENCY_PARAM_STRING];
            latency = latencyParam == null ? DefaultLatency : TimeSpan.Parse(latencyParam);
            Log.Info("Init: Fixed Store Latency={0}", latency);

            mockCallsOnly = config.Properties.ContainsKey(MOCK_CALLS_PARAM_STRING) &&
                "true".Equals(config.Properties[MOCK_CALLS_PARAM_STRING], StringComparison.OrdinalIgnoreCase);
        }

        public override async Task Close()
        {
            await MakeFixedLatencyCall(() => base.Close());
        }

        public override async Task ReadStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            await MakeFixedLatencyCall(() => base.ReadStateAsync(grainType, grainReference, grainState));
        }

        public override async Task WriteStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            await MakeFixedLatencyCall(() => base.WriteStateAsync(grainType, grainReference, grainState));
        }

        public override async Task ClearStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            await MakeFixedLatencyCall(() => base.ClearStateAsync(grainType, grainReference, grainState));
        }

        private async Task MakeFixedLatencyCall(Func<Task> action)
        {
            var sw = Stopwatch.StartNew();
            Exception error = null;
            try
            {
                if (mockCallsOnly)
                {
                    // Simulated call with slight delay
                    await Task.Delay(10);
                }
                else
                {
                    // Make the real call
                    await action();
                }
            }
            catch (Exception exc)
            {
                error = exc;
            }

            if (sw.Elapsed < latency)
            {
                await Task.Delay(latency.Subtract(sw.Elapsed));
            }

            if (error != null)
            {
                // Wrap in AggregateException so that the original error stack trace is preserved.
                throw new AggregateException(error); 
            }
        }
    }
}
