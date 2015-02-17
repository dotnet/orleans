using System.Threading.Tasks;
using Orleans;
using UnitTestGrainInterfaces;

namespace UnitTestGrains
{
    public class TestExtension : ITestExtension
    {
        private readonly ExtensionTestGrain grain;

        public TestExtension(ExtensionTestGrain g)
        {
            grain = g;
        }

        public Task<string> CheckExtension_1()
        {
            return Task.FromResult(grain.ExtensionProperty);
        }

        // check that one can send messages from within extensions.
        public Task<string> CheckExtension_2()
        {
            ISimpleSelfManagedGrain g = SimpleSelfManagedGrainFactory.GetGrain(23);
            return g.GetLabel();
        }
    }

    public class GenericTestExtension<T> : IGenericTestExtension<T>
    {
        private readonly GenericExtensionTestGrain<T> grain;

        public GenericTestExtension(GenericExtensionTestGrain<T> g)
        {
            grain = g;
        }

        public Task<T> CheckExtension_1()
        {
            return Task.FromResult(grain.ExtensionProperty);
        }

        // check that one can send messages from within extensions.
        public Task<string> CheckExtension_2()
        {
            ISimpleSelfManagedGrain g = SimpleSelfManagedGrainFactory.GetGrain(24);
            return g.GetLabel();
        }
    }
}