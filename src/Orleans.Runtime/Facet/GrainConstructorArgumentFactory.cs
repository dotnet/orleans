using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Runtime
{
    /// <summary>
    /// Constructs instances of a grain class using constructor dependency injection.
    /// </summary>
    public class GrainConstructorArgumentFactory
    {
        private static readonly Type FacetMarkerInterfaceType = typeof(IFacetMetadata);
        private static readonly MethodInfo GetFactoryMethod = typeof(GrainConstructorArgumentFactory).GetMethod(nameof(GetArgumentFactory), BindingFlags.NonPublic | BindingFlags.Static);
        private readonly List<Factory<IGrainContext, object>> _argumentFactories;

        /// <summary>
        /// Initializes a new <see cref="GrainConstructorArgumentFactory"/> instance.
        /// </summary>
        /// <param name="serviceProvider">The service provider.</param>
        /// <param name="grainType">The grain type.</param>
        public GrainConstructorArgumentFactory(IServiceProvider serviceProvider, Type grainType)
        {
            _argumentFactories = new List<Factory<IGrainContext, object>>();

            // Find the constructor - supports only single public constructor.
            var parameters = grainType.GetConstructors().FirstOrDefault()?.GetParameters() ?? Enumerable.Empty<ParameterInfo>();
            var types = new List<Type>();
            foreach (var parameter in parameters)
            {
                // Look for attribute with a facet marker interface - supports only single facet attribute
                var attribute = parameter.GetCustomAttributes()
                                         .FirstOrDefault(static attribute => FacetMarkerInterfaceType.IsInstanceOfType(attribute));

                if (attribute is null) continue;

                // Since the IAttributeToFactoryMapper is specific to the attribute specialization, we create a generic method to provide a attribute independent call pattern.
                var getFactory = GetFactoryMethod.MakeGenericMethod(attribute.GetType());
                var argumentFactory = (Factory<IGrainContext, object>)getFactory.Invoke(this, new object[] { serviceProvider, parameter, attribute, grainType });

                // Record the argument factory
                _argumentFactories.Add(argumentFactory);

                // Record the argument type
                types.Add(parameter.ParameterType);
            }

            ArgumentTypes = types.ToArray();
        }

        /// <summary>
        /// Gets the constructor argument types.
        /// </summary>
        public Type[] ArgumentTypes { get; }

        /// <summary>
        /// Creates the arguments for the grain constructor.
        /// </summary>
        /// <param name="grainContext">The grain context.</param>
        /// <returns>The constructor arguments.</returns>
        public object[] CreateArguments(IGrainContext grainContext)
        {
            var i = 0;
            var results = new object[_argumentFactories.Count];
            foreach (var argumentFactory in _argumentFactories)
            {
                results[i++] = argumentFactory(grainContext);
            }

            return results;
        }

        private static Factory<IGrainContext, object> GetArgumentFactory<TMetadata>(IServiceProvider services, ParameterInfo parameter, IFacetMetadata metadata, Type type)
            where TMetadata : IFacetMetadata
        {
            var factoryMapper = services.GetService<IAttributeToFactoryMapper<TMetadata>>();
            if (factoryMapper is null)
            {
                throw new OrleansException($"Missing attribute mapper for attribute {metadata.GetType()} used in grain constructor for grain type {type}.");
            }

            var factory = factoryMapper.GetFactory(parameter, (TMetadata)metadata);
            if (factory is null)
            {
                throw new OrleansException($"Attribute mapper {factoryMapper.GetType()} failed to create a factory for grain type {type}.");
            }

            return factory;
        }
    }
}
