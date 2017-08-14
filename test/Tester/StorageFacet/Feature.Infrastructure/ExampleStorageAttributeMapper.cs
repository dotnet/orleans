﻿using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Runtime;
using Tester.StorageFacet.Abstractions;

namespace Tester.StorageFacet.Infrastructure
{
    public class ExampleStorageAttributeMapper : IAttributeToFactoryMapper<ExampleStorageAttribute>
    {
        private static readonly MethodInfo create = typeof(INamedExampleStorageFactory).GetMethod("Create");

        public Factory<IGrainActivationContext, object> GetFactory(ParameterInfo parameter, ExampleStorageAttribute attribute)
        {
            IExampleStorageConfig config = attribute;
            // set state name to parameter name, if not already specified
            if (string.IsNullOrEmpty(config.StateName))
            {
                config = new ExampleStorageConfig(parameter.Name);
            }
            // use generic type args to define collection type.
            MethodInfo genericCreate = create.MakeGenericMethod(parameter.ParameterType.GetGenericArguments());
            object[] args = new object[] {attribute.StorageProviderName, config};
            return context => Create(context, genericCreate, args);
        }

        private object Create(IGrainActivationContext context, MethodInfo genericCreate, object[] args)
        {
            INamedExampleStorageFactory factory = context.ActivationServices.GetRequiredService<INamedExampleStorageFactory>();
            return genericCreate.Invoke(factory, args);
        }
    }
}
