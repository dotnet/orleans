using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Utilities;

namespace Orleans.Runtime
{
    internal class ConstructorArgumentFactory
    {
        /// <summary>
        /// Cached constructor Argument factorys by type
        /// TODO: consider storing in grain type data and constructing at startup to avoid runtime errors. - jbragg
        /// </summary>
        private readonly CachedReadConcurrentDictionary<Type, ArgumentFactory> argumentsFactorys;
        private readonly IServiceProvider services;

        public ConstructorArgumentFactory(IServiceProvider services)
        {
            this.services = services;
            argumentsFactorys = new CachedReadConcurrentDictionary<Type, ArgumentFactory>();
        }

        public Type[] ArgumentTypes(Type type)
        {
            ArgumentFactory argumentsFactory = argumentsFactorys.GetOrAdd(type, t => new ArgumentFactory(this.services, t));
            return argumentsFactory.ArgumentTypes;
        }

        public object[] CreateArguments(IGrainActivationContext grainActivationContext)
        {
            ArgumentFactory argumentsFactory = argumentsFactorys.GetOrAdd(grainActivationContext.GrainType, type => new ArgumentFactory(this.services, type));
            return argumentsFactory.CreateArguments(grainActivationContext);
        }

        /// <summary>
        /// Facet Argument factory
        /// </summary>
        private class ArgumentFactory
        {
            private static readonly MethodInfo GetFactoryMethod = typeof(ArgumentFactory).GetMethod("GetFactory", BindingFlags.NonPublic | BindingFlags.Static);
            private readonly List<Factory<IGrainActivationContext, object>> argumentFactorys;

            public ArgumentFactory(IServiceProvider services, Type type)
            {
                this.argumentFactorys = new List<Factory<IGrainActivationContext, object>>();
                List<Type> types = new List<Type>();
                IEnumerable<ParameterInfo> parameters = type.GetConstructors()
                                                            .FirstOrDefault()?
                                                            .GetParameters() ?? Enumerable.Empty<ParameterInfo>();
                foreach (ParameterInfo parameter in parameters)
                {
                    var attribute = parameter.GetCustomAttribute<FacetAttribute>();
                    if (attribute == null) continue;
                    // Since the IAttributeToFactoryMapper is specific to the attribute specialization, we create a generic method to provide a attribute independent call pattern.
                    MethodInfo getFactory = GetFactoryMethod.MakeGenericMethod(attribute.GetType());
                    var argumentFactory = (Factory < IGrainActivationContext, object> )getFactory.Invoke(this, new object[] { services, parameter, attribute });
                    if (argumentFactory == null) continue;
                    // cache arguement factory
                    this.argumentFactorys.Add(argumentFactory);
                    types.Add(parameter.ParameterType);
                }
                this.ArgumentTypes = types.ToArray();
            }

            public Type[] ArgumentTypes { get; }

            public object[] CreateArguments(IGrainActivationContext grainContext)
            {
                int i = 0;
                object[] results = new object[argumentFactorys.Count];
                foreach (Factory<IGrainActivationContext, object> argumentFactory in argumentFactorys)
                {
                    results[i++] = argumentFactory(grainContext);
                }
                return results;
            }

            private static Factory<IGrainActivationContext, object> GetFactory<TAttribute>(IServiceProvider services, ParameterInfo parameter, FacetAttribute attribute)
                where TAttribute : FacetAttribute
            {
                var factoryMapper = services.GetRequiredService<IAttributeToFactoryMapper<TAttribute>>();
                return factoryMapper.GetFactory(parameter, (TAttribute)attribute);
            }
        }
    }
}
