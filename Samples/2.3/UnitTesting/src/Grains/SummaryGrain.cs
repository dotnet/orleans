using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans;

namespace Grains
{
    public class SummaryGrain : Grain, ISummaryGrain
    {
        private readonly Dictionary<string, int> counters = new Dictionary<string, int>();

        public Task SetAsync(string name, int value)
        {
            counters[name] = value;
            return Task.CompletedTask;
        }
    }
}