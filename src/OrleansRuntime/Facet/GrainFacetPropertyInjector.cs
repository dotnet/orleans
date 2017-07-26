
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Orleans.Runtime
{
    internal class GrainFacetPropertyInjector
    {
        // TODO: consider storing in grain type data and constructing at startup to avoid runtime errors. - jbragg
        /// <summary>
        /// Cached property injector by type
        /// </summary>
        private readonly ConcurrentDictionary<Type, Injector> injectors;

        public GrainFacetPropertyInjector()
        {
            injectors = new ConcurrentDictionary<Type, Injector>();
        }
        
        public void InjectProperties(IGrainActivationContext grainActivationContext)
        {
            Injector injector = injectors.GetOrAdd(grainActivationContext.GrainType, type => new Injector(type));
            injector.InjectProperties(grainActivationContext);
        }

        /// <summary>
        /// Injects grain facets into properties of an object
        /// </summary>
        private class Injector
        {
            private readonly List<Action<IGrainActivationContext>> propertyInjectors;

            public Injector(Type type)
            {
                this.propertyInjectors = CreatePropertyInjectors(type).ToList();
            }

            public void InjectProperties(IGrainActivationContext grainContext)
            {
                foreach (Action<IGrainActivationContext> injector in propertyInjectors)
                {
                    injector(grainContext);
                }
            }

            private static IEnumerable<Action<IGrainActivationContext>> CreatePropertyInjectors(Type type)
            {
                IEnumerable<PropertyInfo> properties =
                    type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                foreach (PropertyInfo propertyInfo in properties)
                {
                    var attribute = propertyInfo.GetCustomAttribute<GrainFacetAttribute>(true);
                    if (attribute == null)
                    {
                        continue;
                    }

                    if (propertyInfo.GetSetMethod(true) == null || propertyInfo.GetGetMethod(true) == null)
                    {
                        throw new ArgumentException($"Property {propertyInfo.Name} is missing a getter or setter");
                    }

                    Factory<IGrainActivationContext, object> factory = attribute.GetFactory(propertyInfo.PropertyType, propertyInfo.Name);
                    // The only thing called per grain activation is the cached factory to create the instance and the property setter.
                    yield return context => propertyInfo.SetValue(context.GrainInstance, factory.Invoke(context));
                }
            }
        }
    }
}
