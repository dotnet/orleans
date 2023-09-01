using Orleans.Runtime;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    internal class ExtensionTestGrain : Grain, IExtensionTestGrain
    {
        private readonly IGrainContext _grainContext;
        public string ExtensionProperty { get; private set; }
        private TestExtension extender;

        public ExtensionTestGrain(IGrainContext grainContext)
        {
            _grainContext = grainContext;
        }

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            ExtensionProperty = "";
            extender = null;
            return base.OnActivateAsync(cancellationToken);
        }

        public Task InstallExtension(string name)
        {
            if (extender == null)
            {
                extender = new TestExtension(this, GrainFactory);
                _grainContext.SetComponent<ITestExtension>(extender);
            }

            ExtensionProperty = name;
            return Task.CompletedTask;
        }
    }

    public class GenericExtensionTestGrain<T> : Grain, IGenericExtensionTestGrain<T>
    {
        private readonly IGrainContext _grainContext;
        public T ExtensionProperty { get; private set; }
        private GenericTestExtension<T> extender;

        public GenericExtensionTestGrain(IGrainContext grainContext)
        {
            _grainContext = grainContext;
        }

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            ExtensionProperty = default;
            extender = null;
            return base.OnActivateAsync(cancellationToken);
        }

        public Task InstallExtension(T name)
        {
            if (extender == null)
            {
                extender = new GenericTestExtension<T>(this, this.GrainFactory);
                _grainContext.SetComponent<IGenericTestExtension<T>>(extender);
            }

            ExtensionProperty = name;
            return Task.CompletedTask;
        }
    }

    internal class GenericGrainWithNonGenericExtension<T> : Grain, IGenericGrainWithNonGenericExtension<T>
    {
        private readonly IGrainContext _grainContext;
        private SimpleExtension extender;

        public GenericGrainWithNonGenericExtension(IGrainContext grainContext)
        {
            _grainContext = grainContext;
        }

        public Task DoSomething()
        {
            return Task.CompletedTask;
        }

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            if (extender == null)
            {
                extender = new SimpleExtension("A");
                _grainContext.SetComponent<ISimpleExtension>(extender);
            }

            return base.OnActivateAsync(cancellationToken);
        }
    }
}