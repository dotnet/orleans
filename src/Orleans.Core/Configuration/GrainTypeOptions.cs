using System;
using System.Collections.Generic;
using Microsoft.Extensions.Options;
using Orleans.Runtime;
using Orleans.Serialization.Configuration;

namespace Orleans.Configuration
{
    /// <summary>
    /// Contains grain type descriptions.
    /// </summary>
    public class GrainTypeOptions
    {
        /// <summary>
        /// Gets a collection of metadata about grain classes.
        /// </summary>
        public HashSet<Type> Classes { get; } = new ();

        /// <summary>
        /// Gets a collection of metadata about grain interfaces.
        /// </summary>
        public HashSet<Type> Interfaces { get; } = new ();
    }

    /// <summary>
    /// The default configuration provider for <see cref="GrainTypeOptions"/>.
    /// </summary>
    internal sealed class DefaultGrainTypeOptionsProvider : IConfigureOptions<GrainTypeOptions>
    {
        private readonly TypeManifestOptions _typeManifestOptions;

        /// <summary>
        /// Initializes a new instance of the <see cref="DefaultGrainTypeOptionsProvider"/> class.
        /// </summary>
        /// <param name="typeManifestOptions">The type manifest options.</param>
        public DefaultGrainTypeOptionsProvider(IOptions<TypeManifestOptions> typeManifestOptions) => _typeManifestOptions = typeManifestOptions.Value;

        /// <inheritdoc />
        public void Configure(GrainTypeOptions options)
        {
            foreach (var type in _typeManifestOptions.Interfaces)
            {
                if (typeof(IAddressable).IsAssignableFrom(type))
                {
                    options.Interfaces.Add(type);
                }
            }

            foreach (var type in _typeManifestOptions.InterfaceImplementations)
            {
                if (IsImplementationType(type))
                {
                    options.Classes.Add(type switch
                    {
                        { IsGenericType: true, IsConstructedGenericType: false } => type.GetGenericTypeDefinition(),
                        _ => type
                    });
                }
            }

            static bool IsImplementationType(Type type)
            {
                if (type.IsAbstract || type.IsInterface)
                {
                    return false;
                }

                if (typeof(IGrain).IsAssignableFrom(type))
                {
                    return true;
                }

                return false;
            }
        }
    }

    /// <summary>
    /// Validates <see cref="GrainTypeOptions"/>.
    /// </summary>
    public sealed class GrainTypeOptionsValidator : IConfigurationValidator
    {
        private readonly IOptions<GrainTypeOptions> _options;
        private readonly IServiceProvider _serviceProvider;

        /// <summary>
        /// Initializes a new instance of the <see cref="GrainTypeOptionsValidator"/> class.
        /// </summary>
        /// <param name="options">The options.</param>
        /// <param name="serviceProvider">The service provider.</param>
        public GrainTypeOptionsValidator(IOptions<GrainTypeOptions> options, IServiceProvider serviceProvider)
        {
            _options = options;
            _serviceProvider = serviceProvider;
        }

        /// <inheritdoc />
        public void ValidateConfiguration()
        {
            if (_options.Value.Interfaces is not { Count: > 0 })
            {
                throw new OrleansConfigurationException($"No grain interfaces have been configured. Either add some grain interfaces and reference the Orleans.Sdk package, or remove {nameof(GrainTypeOptionsValidator)} from the services collection.");
            }

            var isSilo = _serviceProvider.GetService(typeof(ILocalSiloDetails)) != null;
            if (isSilo)
            {
                if (_options.Value.Classes is not { Count: > 0 })
                {
                    throw new OrleansConfigurationException($"No grain classes have been configured. Either add some grain classes and reference the Orleans.Sdk package, or remove {nameof(GrainTypeOptionsValidator)} from the services collection.");
                }
            }
        }
    }
}
