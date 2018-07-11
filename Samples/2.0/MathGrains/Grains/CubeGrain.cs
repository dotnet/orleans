using System.Threading.Tasks;
using GrainInterfaces;
using Orleans;

namespace Grains
{
    public class CubeGrain : Grain, ICubeGrain
    {

        public Task<int> CubeMe(int input)
        {
            return Task.FromResult(input * input * input);
        }

    }

}
