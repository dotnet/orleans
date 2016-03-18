using System;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime.Providers;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    public class ExtensionTestGrain : Grain, IExtensionTestGrain
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
                if (!SiloProviderRuntime.Instance.TryAddExtension(extender))
                {
                    throw new SystemException("Unable to add new extension");
                }
            }
            ExtensionProperty = name;
            return TaskDone.Done;
        }

        public Task RemoveExtension()
        {
            SiloProviderRuntime.Instance.RemoveExtension(extender);
            extender = null;
            return TaskDone.Done;
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
                extender = new GenericTestExtension<T>(this);
                if (!SiloProviderRuntime.Instance.TryAddExtension(extender))
                {
                    throw new SystemException("Unable to add new extension");
                }
            }
            ExtensionProperty = name;
            return TaskDone.Done;
        }

        public Task RemoveExtension()
        {
            SiloProviderRuntime.Instance.RemoveExtension(extender);
            extender = null;
            return TaskDone.Done;
        }
    }
}