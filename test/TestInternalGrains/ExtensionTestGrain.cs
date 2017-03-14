using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Runtime;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    internal class ExtensionTestGrain : Grain, IExtensionTestGrain
    {
        private readonly ISiloRuntimeClient runtimeClient;
        public string ExtensionProperty { get; private set; }
        private TestExtension extender;

        public ExtensionTestGrain(ISiloRuntimeClient runtimeClient)
        {
            this.runtimeClient = runtimeClient;
        }

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
                if (!runtimeClient.TryAddExtension(extender))
                {
                    throw new SystemException("Unable to add new extension");
                }
            }
            ExtensionProperty = name;
            return TaskDone.Done;
        }

        public Task RemoveExtension()
        {
            runtimeClient.RemoveExtension(extender);
            extender = null;
            return TaskDone.Done;
        }
    }

    public class GenericExtensionTestGrain<T> : Grain, IGenericExtensionTestGrain<T>
    {
        private readonly ISiloRuntimeClient runtimeClient;
        public T ExtensionProperty { get; private set; }
        private GenericTestExtension<T> extender;

        public GenericExtensionTestGrain()
        {
            this.runtimeClient = this.ServiceProvider.GetRequiredService<ISiloRuntimeClient>();
        }

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
                if (!runtimeClient.TryAddExtension(extender))
                {
                    throw new SystemException("Unable to add new extension");
                }
            }
            ExtensionProperty = name;
            return TaskDone.Done;
        }

        public Task RemoveExtension()
        {
            runtimeClient.RemoveExtension(extender);
            extender = null;
            return TaskDone.Done;
        }
    }

    internal class GenericGrainWithNonGenericExtension<T> : Grain, IGenericGrainWithNonGenericExtension<T>
    {
        private readonly ISiloRuntimeClient runtimeClient;
        private SimpleExtension extender;

        public GenericGrainWithNonGenericExtension(ISiloRuntimeClient runtimeClient)
        {
            this.runtimeClient = runtimeClient;
        }
        
        public Task DoSomething() {
            return TaskDone.Done;
        }
        
        public override Task OnActivateAsync()
        {
            if (extender == null)
            {
                extender = new SimpleExtension("A");
                if (!runtimeClient.TryAddExtension(extender))
                {
                    throw new SystemException("Unable to add new extension");
                }
            }

            return base.OnActivateAsync();
        }
    }
}