using Orleans;
using System.Threading.Tasks;

namespace UnitTests.GrainInterfaces
{
    public class NullableState
    {
        public string Name { get; set; }
    }

    public interface INullStateGrain : IGrainWithIntegerKey
    {
        Task SetStateAndDeactivate(NullableState state);
        Task<NullableState> GetState();
    }
}