using System.Threading.Tasks;
using Orleans;

namespace Grains
{
    /// <summary>
    /// Demonstrates a very basic grain that keeps a single value.
    /// </summary>
    public class BasicGrain : Grain, IBasicGrain
    {
        private int value;

        public Task<int> GetValueAsync() => Task.FromResult(value);

        public Task SetValueAsync(int value)
        {
            this.value = value;
            return Task.CompletedTask;
        }
    }
}