using System.Threading.Tasks;
using Orleans;


namespace GrainInterfaces
{

    public interface IWorker : IGrainWithIntegerKey
    {
        Task<long> GetAnswer();
    }
}
