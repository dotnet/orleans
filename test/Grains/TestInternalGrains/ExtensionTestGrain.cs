using System.Threading.Tasks;
using Orleans;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    internal class ExtensionTestGrain : Grain, IExtensionTestGrain
    {
        public string ExtensionProperty { get; private set; }
        private TestExtension extender;

        public override Task OnActivateAsync()
        {
            ExtensionProperty = "";
            extender = null;
            return base.OnActivateAsync();
        }

        public Task InstallExtension(string name)
        {
            if (extender == null)
            {
                extender = new TestExtension(this, GrainFactory);
                this.Data.SetComponent<ITestExtension>(extender);
            }
            ExtensionProperty = name;
            return Task.CompletedTask;
        }
    }

    public class GenericExtensionTestGrain<T> : Grain, IGenericExtensionTestGrain<T>
    {
        public T ExtensionProperty { get; private set; }
        private GenericTestExtension<T> extender;

        public override Task OnActivateAsync()
        {
            ExtensionProperty = default(T);
            extender = null;
            return base.OnActivateAsync();
        }

        public Task InstallExtension(T name)
        {
            if (extender == null)
            {
                extender = new GenericTestExtension<T>(this, this.GrainFactory);
                this.Data.SetComponent<IGenericTestExtension<T>>(extender);
            }
            ExtensionProperty = name;
            return Task.CompletedTask;
        }
    }

    internal class GenericGrainWithNonGenericExtension<T> : Grain, IGenericGrainWithNonGenericExtension<T>
    {
        private SimpleExtension extender;
        
        public Task DoSomething() {
            return Task.CompletedTask;
        }
        
        public override Task OnActivateAsync()
        {
            if (extender == null)
            {
                extender = new SimpleExtension("A");
                this.Data.SetComponent<ISimpleExtension>(extender);
            }

            return base.OnActivateAsync();
        }
    }
}