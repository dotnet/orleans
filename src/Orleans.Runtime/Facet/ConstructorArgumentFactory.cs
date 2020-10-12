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
            return new ArgumentFactory(this.services, grainClass);
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
                this.argumentFactorys = new List<Factory<IGrainContext, object>>();
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
                    var argumentFactory = (Factory<IGrainContext, object>)getFactory.Invoke(this, new object[] { services, parameter, attribute, type });
                    // cache argument factory
                    this.argumentFactorys.Add(argumentFactory);
                    // cache argument type
                    types.Add(parameter.ParameterType);
                }
                this.ArgumentTypes = types.ToArray();
            }

            public Type[] ArgumentTypes { get; }

            public object[] CreateArguments(IGrainContext grainContext)
            {
                int i = 0;
                object[] results = new object[argumentFactorys.Count];
                foreach (Factory<IGrainContext, object> argumentFactory in argumentFactorys)
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
                Factory<IGrainContext, object> factory = factoryMapper.GetFactory(parameter, (TMetadata)metadata);
                if(factory == null) throw new OrleansException($"Attribute mapper {factoryMapper.GetType()} failed to create a factory for grain type {type}.");
                return factory;
            }
        }
    }
}
