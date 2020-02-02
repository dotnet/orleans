using System;
using System.Threading.Tasks;
using Orleans;

namespace Grains
{
    /// <summary>
    /// Demonstrates a grain that calls another grain on-demand.
    /// </summary>
    public class CallingGrain : Grain, ICallingGrain
    {
        private int counter;

        public Task IncrementAsync()
        {
            counter += 1;
            return Task.CompletedTask;
        }

        public Task PublishAsync() => GrainFactory.GetGrain<ISummaryGrain>(Guid.Empty).SetAsync(GrainKey, counter);

        /// <summary>
        /// Opens up the grain factory for mocking.
        /// </summary>
        public virtual new IGrainFactory GrainFactory => base.GrainFactory;

        /// <summary>
        /// Opens up the grain key name for mocking.
        /// </summary>
        public virtual string GrainKey => this.GetPrimaryKeyString();
    }
}
