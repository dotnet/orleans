using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Runtime;

/// <summary>
/// Constructs instances of a grain class using constructor dependency injection.
/// </summary>
public class GrainConstructorArgumentFactory
{
    private static readonly Type FacetMarkerInterfaceType = typeof(IFacetMetadata);
    private static readonly MethodInfo GetFactoryMethod = typeof(GrainConstructorArgumentFactory).GetMethod(nameof(GetArgumentFactory), BindingFlags.NonPublic | BindingFlags.Static);
    private readonly List<Factory<IGrainContext, object>> _argumentFactories;

    /// <summary>
    /// Gets the constructor argument types.
    /// </summary>
    public Type[] ArgumentTypes { get; }

    /// <summary>
    /// Initializes a new <see cref="GrainConstructorArgumentFactory"/> instance.
    /// </summary>
    /// <param name="serviceProvider">The service provider.</param>
    /// <param name="grainType">The grain type.</param>
    public GrainConstructorArgumentFactory(IServiceProvider serviceProvider, Type grainType)
    {
        _argumentFactories = [];
        var types = new List<Type>();

        // Find the constructor - supports only single public constructor.
        var constructor = grainType.GetConstructors().FirstOrDefault();
        if (constructor is null)
        {
            ArgumentTypes = [];
            return;
        }

        var parameters = constructor.GetParameters();

        foreach (var parameter in parameters)
        {
            types.Add(parameter.ParameterType);

            // Look for attribute with a facet marker interface - supports only single facet attribute
            var facetAttribute = parameter.GetCustomAttributes()
                .FirstOrDefault(FacetMarkerInterfaceType.IsInstanceOfType);

            if (facetAttribute != null)
            {
                // This is an Orleans facet i.e. [PersistentState]! Since the IAttributeToFactoryMapper is specific to the
                // attribute specialization, we create a generic method to provide a attribute independent call pattern.

                var getFactory = GetFactoryMethod.MakeGenericMethod(facetAttribute.GetType());

                var argumentFactory = (Factory<IGrainContext, object>)getFactory.Invoke(this,
                    [serviceProvider, parameter, facetAttribute, grainType]);

                _argumentFactories.Add(argumentFactory);
            }
            else
            {
                // This is a standard DI parameter. So we create a factory that resolves it directly.
                _argumentFactories.Add(context =>
                {
                    var activationServices = context.ActivationServices;

                    if (parameter.GetCustomAttribute<FromKeyedServicesAttribute>() is { } keyedAttribute)
                    {
                        return activationServices.GetRequiredKeyedService(parameter.ParameterType, keyedAttribute.Key);
                    }
                    else
                    {
                        return activationServices.GetRequiredService(parameter.ParameterType);
                    }
                });
            }
        }

        ArgumentTypes = [.. types];
    }

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
