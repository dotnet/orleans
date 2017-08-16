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
        private static readonly Type FacetMarkerInterfaceType = typeof(IFacetMetadata);
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
                // find constructor - supports only single public constructor
                IEnumerable<ParameterInfo> parameters = type.GetConstructors()
                                                            .FirstOrDefault()?
                                                            .GetParameters() ?? Enumerable.Empty<ParameterInfo>();
                foreach (ParameterInfo parameter in parameters)
                {
                    // look for attribute with a facet marker interface - supports only single facet attribute
                    var attribute = parameter.GetCustomAttributes()
                                             .FirstOrDefault(attrib => FacetMarkerInterfaceType.IsInstanceOfType(attrib));
                    if (attribute == null) continue;
                    // Since the IAttributeToFactoryMapper is specific to the attribute specialization, we create a generic method to provide a attribute independent call pattern.
                    MethodInfo getFactory = GetFactoryMethod.MakeGenericMethod(attribute.GetType());
                    var argumentFactory = (Factory<IGrainActivationContext,object> )getFactory.Invoke(this, new object[] { services, parameter, attribute, type });
                    // cache arguement factory
                    this.argumentFactorys.Add(argumentFactory);
                    // cache arguement type
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

            private static Factory<IGrainActivationContext, object> GetFactory<TMetadata>(IServiceProvider services, ParameterInfo parameter, IFacetMetadata metadata, Type type)
                where TMetadata : IFacetMetadata
            {
                var factoryMapper = services.GetService<IAttributeToFactoryMapper<TMetadata>>();
                if (factoryMapper == null) throw new OrleansException($"Missing attribute mapper for attribute {metadata.GetType()} used in grain constructor for grain type {type}.");
                Factory<IGrainActivationContext, object> factory = factoryMapper.GetFactory(parameter, (TMetadata)metadata);
                if(factory == null) throw new OrleansException($"Attribute mapper {factoryMapper.GetType()} failed to create a factory for grain type {type}.");
                return factory;
            }
        }
    }
}
