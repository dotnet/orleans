using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    internal class TestExtension : ITestExtension
    {
        private readonly ExtensionTestGrain grain;
        private readonly IGrainFactory grainFactory;

        public TestExtension(ExtensionTestGrain g, IGrainFactory grainFactory)
        {
            grain = g;
            this.grainFactory = grainFactory;
        }

        public Task<string> CheckExtension_1() => Task.FromResult(grain.ExtensionProperty);

        // check that one can send messages from within extensions.
        public Task<string> CheckExtension_2()
        {
            var g = grainFactory.GetGrain<ITestGrain>(23);
            return g.GetLabel();
        }
    }

    public class SimpleExtension : ISimpleExtension
    {
        private readonly string someString;

        public SimpleExtension(string someString)
        {
            this.someString = someString;
        }

        public Task<string> CheckExtension_1() => Task.FromResult(someString);
    }
    
    public class AutoExtension : IAutoExtension
    {
        public Task<string> CheckExtension() => Task.FromResult("whoot!");
    }

    public class GenericTestExtension<T> : IGenericTestExtension<T>
    {
        private readonly GenericExtensionTestGrain<T> grain;
        private readonly IGrainFactory grainFactory;

        public GenericTestExtension(GenericExtensionTestGrain<T> g, IGrainFactory grainFactory)
        {
            grain = g;
            this.grainFactory = grainFactory;
        }

        public Task<T> CheckExtension_1() => Task.FromResult(grain.ExtensionProperty);

        // check that one can send messages from within extensions.
        public Task<string> CheckExtension_2()
        {
            var g = grainFactory.GetGrain<ITestGrain>(24);
            return g.GetLabel();
        }
    }
}