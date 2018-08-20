using System.Diagnostics;
using System.Threading.Tasks;
using Orleans;
using Orleans.Providers;

namespace StatefulCalculator
{
    public interface ICalculatorGrain : IGrainWithGuidKey
    {
        Task<int> GetProcessId();
        Task<int> Add(int value);
    }

    [StorageProvider]
    public class CalculatorGrain : Grain<int>, ICalculatorGrain
    {
        public async Task<int> Add(int value)
        {
            this.State += value;
            await this.WriteStateAsync();
            return this.State;
        }

        public Task<int> GetProcessId() => Task.FromResult(Process.GetCurrentProcess().Id);
    }
}
