using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;

namespace Orleans
{
    public class PersistentStateAttributeMapper : IAttributeToFactoryMapper<PersistentStateAttribute>
    {
        private static readonly MethodInfo create = typeof(IPersistentStateFactory).GetMethod("Create");

        public Factory<IGrainActivationContext, object> GetFactory(ParameterInfo parameter, PersistentStateAttribute attribute)
        {
            IPersistentStateConfiguration config = attribute;
            // set state name to parameter name, if not already specified
            if (string.IsNullOrEmpty(config.StateName))
            {
                config = new PersistentStateConfiguration() { StateName = parameter.Name, StorageName = attribute.StorageName };
            }
            // use generic type args to define collection type.
            MethodInfo genericCreate = create.MakeGenericMethod(parameter.ParameterType.GetGenericArguments());
            return context => Create(context, genericCreate, config);
        }

        private object Create(IGrainActivationContext context, MethodInfo genericCreate, IPersistentStateConfiguration config)
        {
            IPersistentStateFactory factory = context.ActivationServices.GetRequiredService<IPersistentStateFactory>();
            object[] args = new object[] { context, config };
            return genericCreate.Invoke(factory, args);
        }

        private class PersistentStateConfiguration : IPersistentStateConfiguration
        {
            public string StateName { get; set; }

            public string StorageName { get; set; }
        }
    }
}
