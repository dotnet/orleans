using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;

namespace Orleans
{
    /// <summary>
    /// Attribute mapper which maps persistent state attributes to a corresponding factory instance.
    /// </summary>
    public class PersistentStateAttributeMapper : IAttributeToFactoryMapper<PersistentStateAttribute>
    {
        private static readonly MethodInfo create = typeof(IPersistentStateFactory).GetMethod("Create");

        /// <inheritdoc/>
        public Factory<IGrainContext, object> GetFactory(ParameterInfo parameter, PersistentStateAttribute attribute)
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

        private object Create(IGrainContext context, MethodInfo genericCreate, IPersistentStateConfiguration config)
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
