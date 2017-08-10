using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Runtime;
using Tester.StorageFacet.Abstractions;

namespace Tester.StorageFacet.Infrastructure
{
    public class StorageFeatureFactory : IStorageFeatureFactory
    {
        private readonly Type instanceType;
        private readonly IGrainActivationContext context;

        public StorageFeatureFactory(Type instanceType, IGrainActivationContext context)
        {
            if (instanceType.GetInterface(typeof(IStorageFeature<>).Name) == null) throw new ArgumentException(nameof(instanceType));
            this.instanceType = instanceType;
            this.context = context;
        }

        public IStorageFeature<TState> Create<TState>(IStorageFeatureConfig config)
        {
            var storage = context.ActivationServices.GetRequiredService(instanceType.MakeGenericType(typeof(TState))) as IStorageFeature<TState>;
            (storage as IConfigurableStorageFeature)?.Configure(config);
            (storage as ILifecycleParticipant<GrainLifecyleStage>)?.Participate(context.ObservableLifecycle);
            return storage;
        }
    }
}
