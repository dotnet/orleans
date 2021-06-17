using System.Threading.Tasks;

namespace UnitTests.Grains
{
    public class SpecializedSimpleGenericGrain : SimpleGenericGrain<double>
    {
        public override Task Transform()
        {
            Value = Value * 2.0;
            return Task.CompletedTask;
        }
    }
}
