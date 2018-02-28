using System.Threading.Tasks;

namespace GrainInterfaces
{
    public interface ICollector : Orleans.IGrainWithIntegerKey
    {
        Task<long> GetSum();
    }
}
