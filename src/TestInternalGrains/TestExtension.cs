using System.Threading.Tasks;
using Orleans;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    public class TestExtension : ITestExtension
    {
        private readonly ExtensionTestGrain grain;
        private readonly IGrainFactory grainFactory;

        public TestExtension(ExtensionTestGrain g, IGrainFactory grainFactory)
        {
            grain = g;
            this.grainFactory = grainFactory;
        }

        public Task<string> CheckExtension_1()
        {
            return Task.FromResult(grain.ExtensionProperty);
        }

        // check that one can send messages from within extensions.
        public Task<string> CheckExtension_2()
        {
            ITestGrain g = grainFactory.GetGrain<ITestGrain>(23);
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
            ITestGrain g = GrainClient.GrainFactory.GetGrain<ITestGrain>(24);
            return g.GetLabel();
        }
    }
}