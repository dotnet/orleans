using System.Threading.Tasks;
using GrainInterfaces;
using Orleans;

namespace Grains
{
    public class SquareGrain : Grain, ISquareGrain
    {
        
        public Task<int> SquareMe(int input)
        {
            return Task.FromResult(input * input);
        }

    }

}
