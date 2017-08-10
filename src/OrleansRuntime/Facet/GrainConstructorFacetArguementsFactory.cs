using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Runtime
{
    internal class GrainConstructorFacetArguementsFactory
    {
        private static readonly Type facetFactoryType = typeof(IParameterFacetFactory<>);
        /// <summary>
        /// Cached constructor arguement factorys by type
        /// TODO: consider storing in grain type data and constructing at startup to avoid runtime errors. - jbragg
        /// </summary>
        private readonly ConcurrentDictionary<Type, ArguementsFactory> arguementsFactorys;
        private readonly IServiceProvider services;

        public GrainConstructorFacetArguementsFactory(IServiceProvider services)
        {
            this.services = services;
            arguementsFactorys = new ConcurrentDictionary<Type, ArguementsFactory>();
        }

        public Type[] ArguementTypes(Type type)
        {
            ArguementsFactory arguementsFactory = arguementsFactorys.GetOrAdd(type, t => new ArguementsFactory(this.services, t));
            return arguementsFactory.ArguementTypes;
        }

        public object[] CreateArguements(IGrainActivationContext grainActivationContext)
        {
            ArguementsFactory arguementsFactory = arguementsFactorys.GetOrAdd(grainActivationContext.GrainType, type => new ArguementsFactory(this.services, type));
            return arguementsFactory.CreateArguements(grainActivationContext);
        }

        /// <summary>
        /// Facet Arguement factory
        /// </summary>
        private class ArguementsFactory
        {
            private readonly List<Factory<IGrainActivationContext, object>> arguementFactorys;

            public ArguementsFactory(IServiceProvider services, Type type)
            {
                this.arguementFactorys = new List<Factory<IGrainActivationContext, object>>();
                List<Type> types = new List<Type>();
                IEnumerable<ParameterInfo> parameters = type.GetConstructors()
                                                            .FirstOrDefault()?
                                                            .GetParameters() ?? Enumerable.Empty<ParameterInfo>();
                foreach (ParameterInfo parameter in parameters)
                {
                    var attribute = parameter.GetCustomAttribute<FacetAttribute>();
                    if (attribute == null) continue;
                    IFactoryBridge bridge = Activator.CreateInstance(typeof(FactoryBridge<>).MakeGenericType(attribute.GetType())) as IFactoryBridge;
                    if (bridge == null) continue;
                    bridge.Factory = services.GetRequiredService(facetFactoryType.MakeGenericType(attribute.GetType()));
                    types.Add(parameter.ParameterType);
                    this.arguementFactorys.Add(bridge.Create(parameter, attribute));
                }
                this.ArguementTypes = types.ToArray();
            }

            public Type[] ArguementTypes { get; }

            public object[] CreateArguements(IGrainActivationContext grainContext)
            {
                int i = 0;
                object[] results = new object[arguementFactorys.Count];
                foreach (Factory<IGrainActivationContext, object> arguementFactory in arguementFactorys)
                {
                    results[i++] = arguementFactory(grainContext);
                }
                return results;
            }
        }

        private interface IFactoryBridge
        {
            object Factory { set; }
            Factory<IGrainActivationContext, object> Create(ParameterInfo parameter, FacetAttribute attribute);
        }

        private class FactoryBridge<TAttribute> : IFactoryBridge
            where TAttribute : FacetAttribute
        {
            public object Factory { private get; set; }
            public Factory<IGrainActivationContext, object> Create(ParameterInfo parameter, FacetAttribute attribute)
            {
                return (Factory as IParameterFacetFactory<TAttribute>)?.Create(parameter, attribute as TAttribute);
            }
        }
    }
}
