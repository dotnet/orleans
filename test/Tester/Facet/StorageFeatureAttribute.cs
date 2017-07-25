using System;
using Orleans;
using Orleans.Runtime;

namespace Tester
{
    [AttributeUsage(AttributeTargets.Property)]
    public class StorageFeatureAttribute : GrainFacetAttribute, IStorageFeatureConfig
    {
        public string StateName { get; private set; }
        public string StorageProviderName { get; }

        public StorageFeatureAttribute(string storageProviderName = null, string stateName = null)
        {
            this.StorageProviderName = storageProviderName;
            this.StateName = stateName;
        }

        public override Factory<IGrainActivationContext, object> GetFactory(Type parameterType, string parameterName)
        {
            if (string.IsNullOrEmpty(this.StateName))
            {
                this.StateName = parameterName;
            }
            Type factoryType = typeof(IStorageFeatureFactory<>).MakeGenericType(parameterType.GetGenericArguments());
            return context =>
            {
                // Get facet factory
                IStorageFeatureFactory factory = context.ActivationServices.GetService(factoryType) as IStorageFeatureFactory;

                // Create facet
                object facet = factory?.Create(this);

                // register with grain lifecycle
                facet.ParticipateInGrainLifecycle(context.ObservableLifeCycle);

                return facet;
            };
        }
    }
}
