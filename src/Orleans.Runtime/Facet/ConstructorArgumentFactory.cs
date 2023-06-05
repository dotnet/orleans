using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Runtime
{
    internal class ConstructorArgumentFactory
    {
        private static readonly Type FacetMarkerInterfaceType = typeof(IFacetMetadata);
        private readonly IServiceProvider services;

        public ConstructorArgumentFactory(IServiceProvider services)
        {
            this.services = services;
        }

        public ArgumentFactory CreateFactory(Type grainClass)
        {
            return new ArgumentFactory(services, grainClass);
        }

        /// <summary>
        /// Facet Argument factory
        /// </summary>
        internal class ArgumentFactory
        {
            private static readonly MethodInfo GetFactoryMethod = typeof(ArgumentFactory).GetMethod("GetFactory", BindingFlags.NonPublic | BindingFlags.Static);
            private readonly List<Factory<IGrainContext, object>> argumentFactorys;

            public ArgumentFactory(IServiceProvider services, Type type)
            {
                argumentFactorys = new List<Factory<IGrainContext, object>>();
                var types = new List<Type>();
                // find constructor - supports only single public constructor
                var parameters = type.GetConstructors()
                                                            .FirstOrDefault()?
                                                            .GetParameters() ?? Enumerable.Empty<ParameterInfo>();
                foreach (var parameter in parameters)
                {
                    // look for attribute with a facet marker interface - supports only single facet attribute
                    var attribute = parameter.GetCustomAttributes()
                                             .FirstOrDefault(attrib => FacetMarkerInterfaceType.IsInstanceOfType(attrib));
                    if (attribute == null) continue;
                    // Since the IAttributeToFactoryMapper is specific to the attribute specialization, we create a generic method to provide a attribute independent call pattern.
                    var getFactory = GetFactoryMethod.MakeGenericMethod(attribute.GetType());
                    var argumentFactory = (Factory<IGrainContext, object>)getFactory.Invoke(this, new object[] { services, parameter, attribute, type });
                    // cache argument factory
                    argumentFactorys.Add(argumentFactory);
                    // cache argument type
                    types.Add(parameter.ParameterType);
                }
                ArgumentTypes = types.ToArray();
            }

            public Type[] ArgumentTypes { get; }

            public object[] CreateArguments(IGrainContext grainContext)
            {
                var i = 0;
                var results = new object[argumentFactorys.Count];
                foreach (var argumentFactory in argumentFactorys)
                {
                    results[i++] = argumentFactory(grainContext);
                }
                return results;
            }

            private static Factory<IGrainContext, object> GetFactory<TMetadata>(IServiceProvider services, ParameterInfo parameter, IFacetMetadata metadata, Type type)
                where TMetadata : IFacetMetadata
            {
                var factoryMapper = services.GetService<IAttributeToFactoryMapper<TMetadata>>();
                if (factoryMapper == null) throw new OrleansException($"Missing attribute mapper for attribute {metadata.GetType()} used in grain constructor for grain type {type}.");
                var factory = factoryMapper.GetFactory(parameter, (TMetadata)metadata);
                if(factory == null) throw new OrleansException($"Attribute mapper {factoryMapper.GetType()} failed to create a factory for grain type {type}.");
                return factory;
            }
        }
    }
}
