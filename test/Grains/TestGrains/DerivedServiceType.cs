using System.Threading.Tasks;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    public class DerivedServiceType : ServiceType, IDerivedServiceType
    {
        public Task<string> DerivedServiceTypeMethod1() => Task.FromResult("DerivedServiceTypeMethod1");

        public Task<string> H1Method() => Task.FromResult("H1");

        public Task<string> H2Method() => Task.FromResult("H2");

        public Task<string> H3Method() => Task.FromResult("H3");
    }
}
