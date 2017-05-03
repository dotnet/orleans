using System.Threading.Tasks;
using Orleans;

namespace UnitTests.Grains
{
    class SpecializedSimpleGenericGrain : SimpleGenericGrain<double>
    {
        public override Task Transform()
        {
            Value = Value * 2.0;
            return Task.CompletedTask;
        }
    }
}
