using System.Threading.Tasks;
using Orleans;

namespace UnitTests.GrainInterfaces
{

    public interface IChainedGrain : IGrainWithIntegerKey
    {
        Task<int> GetId();
        Task<int> GetX();
        Task<IChainedGrain> GetNext();
        //[ReadOnly]
        Task<int> GetCalculatedValue();
        Task SetNext(IChainedGrain next);
        //[ReadOnly]
        Task Validate(bool nextIsSet);
        Task PassThis(IChainedGrain next);
    }
}
