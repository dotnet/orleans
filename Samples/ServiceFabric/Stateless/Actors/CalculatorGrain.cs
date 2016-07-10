using System.Threading.Tasks;
using GrainInterfaces;
using Orleans;

namespace Actors
{
    using System;

    using Orleans.Placement;
    using Orleans.Providers;

    [StorageProvider(ProviderName = "fabric")]
    [ConsistentPartitionPlacement]
    public class CalculatorGrain : Grain<CalculatorGrainState>, ICalculatorGrain
    {
        public async Task<double> Add(double value)
        {
            State.Value += value;
            await WriteStateAsync();
            return State.Value;
        }

        public async Task<double> Divide(double value)
        {
            State.Value /= value;
            await WriteStateAsync();
            return State.Value;
        }

        public Task<double> Get()
        {
            return Task.FromResult(State.Value);
        }

        public async Task<double> Multiply(double value)
        {
            State.Value *= value;
            await WriteStateAsync();
            return State.Value;
        }

        public async Task<double> Set(double value)
        {
            State.Value = value;
            await WriteStateAsync();
            return State.Value;
        }

        public async Task<double> Subtract(double value)
        {
            State.Value -= value;
            await WriteStateAsync();
            return State.Value;
        }
    }

    [Serializable]
    public class CalculatorGrainState
    {
        public double Value { get; set; }
    }
}
