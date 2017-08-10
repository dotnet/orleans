using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Runtime;
using Tester.StorageFacet.Abstractions;

namespace Tester.StorageFacet.Infrastructure
{
    public class StorageFeatureParameterFacetFactory : IParameterFacetFactory<StorageFeatureAttribute>
    {
        private static readonly MethodInfo create = typeof(INamedStorageFeatureFactory).GetMethod("Create");

        public Factory<IGrainActivationContext, object> Create(ParameterInfo parameter, StorageFeatureAttribute attribute)
        {
            IStorageFeatureConfig config = attribute;
            // set state name to parameter name, if not already specified
            if (string.IsNullOrEmpty(config.StateName))
            {
                config = new StorageFeatureConfig(parameter.Name);
            }
            // use generic type args to define collection type.
            MethodInfo genericCreate = create.MakeGenericMethod(parameter.ParameterType.GetGenericArguments());
            object[] args = new object[] {attribute.StorageProviderName, config};
            return context => Create(context, genericCreate, args);
        }

        private object Create(IGrainActivationContext context, MethodInfo genericCreate, object[] args)
        {
            INamedStorageFeatureFactory factory = context.ActivationServices.GetRequiredService<INamedStorageFeatureFactory>();
            return genericCreate.Invoke(factory, args);
        }
    }

}
