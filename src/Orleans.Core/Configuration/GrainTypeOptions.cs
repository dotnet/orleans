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

    internal sealed class DefaultGrainTypeOptionsProvider : IConfigureOptions<GrainTypeOptions>
    {
        private readonly TypeManifestOptions _typeManifestOptions;

        public DefaultGrainTypeOptionsProvider(IOptions<TypeManifestOptions> typeManifestOptions) => _typeManifestOptions = typeManifestOptions.Value;

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
                if (typeof(Grain).IsAssignableFrom(type))
                {
                    options.Classes.Add(type switch
                    {
                        { IsGenericType: true, IsConstructedGenericType: false } => type.GetGenericTypeDefinition(),
                        _ => type
                    });
                }
            }
        }
    }

    public sealed class GrainTypeOptionsValidator : IConfigurationValidator
    {
        private readonly IOptions<GrainTypeOptions> _options;
        private readonly IServiceProvider _serviceProvider;

        public GrainTypeOptionsValidator(IOptions<GrainTypeOptions> options, IServiceProvider serviceProvider)
        {
            _options = options;
            _serviceProvider = serviceProvider;
        }

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
