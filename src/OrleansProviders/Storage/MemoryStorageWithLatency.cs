using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Providers;
using Orleans.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;

namespace Orleans.Storage
{

    public class MemoryStorageWithLatencyOptions : MemoryGrainStorageOptions
    {
        public static readonly TimeSpan DefaultLatency = TimeSpan.FromMilliseconds(200);
        public TimeSpan Latency { get; set; } = DefaultLatency;
        public bool MockCallsOnly { get;set; }
    }

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
    public class MemoryGrainStorageWithLatency :IGrainStorage
    {
        private const int NUM_STORE_GRAINS = 1;
        private MemoryGrainStorage baseGranStorage;
        private MemoryStorageWithLatencyOptions options;
        /// <summary> Default constructor. </summary>
        public MemoryGrainStorageWithLatency(string name, MemoryStorageWithLatencyOptions options,
            ILoggerFactory loggerFactory, IGrainFactory grainFactory)
        {
            this.baseGranStorage = new MemoryGrainStorage(name, options, loggerFactory, grainFactory);
            this.options = options;
        }

        /// <summary> Read state data function for this storage provider. </summary>
        /// <see cref="IStorageProvider.ReadStateAsync"/>
        public async Task ReadStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            await MakeFixedLatencyCall(() => baseGranStorage.ReadStateAsync(grainType, grainReference, grainState));
        }

        /// <summary> Write state data function for this storage provider. </summary>
        /// <see cref="IStorageProvider.WriteStateAsync"/>
        public async Task WriteStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
           await MakeFixedLatencyCall(() => baseGranStorage.WriteStateAsync(grainType, grainReference, grainState));
        }

        /// <summary> Delete / Clear state data function for this storage provider. </summary>
        /// <see cref="IStorageProvider.ClearStateAsync"/>
        public async Task ClearStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            await MakeFixedLatencyCall(() => baseGranStorage.ClearStateAsync(grainType, grainReference, grainState));
        }

        private async Task MakeFixedLatencyCall(Func<Task> action)
        {
            var sw = Stopwatch.StartNew();
            Exception error = null;
            try
            {
                if (this.options.MockCallsOnly)
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

            if (sw.Elapsed < this.options.Latency)
            {
                await Task.Delay(this.options.Latency.Subtract(sw.Elapsed));
            }

            if (error != null)
            {
                // Wrap in AggregateException so that the original error stack trace is preserved.
                throw new AggregateException(error);
            }
        }
    }
}
