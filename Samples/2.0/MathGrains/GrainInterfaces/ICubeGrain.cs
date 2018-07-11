using System.Threading.Tasks;
using Orleans;

namespace GrainInterfaces
{
    public interface ICubeGrain : IGrainWithGuidKey
    {
        Task<int> CubeMe(int input);
    }
}
