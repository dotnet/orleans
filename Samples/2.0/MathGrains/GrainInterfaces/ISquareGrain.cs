using System.Threading.Tasks;
using Orleans;

namespace GrainInterfaces
{
    public interface ISquareGrain : IGrainWithGuidKey
    {
        Task<int> SquareMe(int input);
    }
}
