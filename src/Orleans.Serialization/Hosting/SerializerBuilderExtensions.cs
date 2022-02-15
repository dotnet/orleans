using Orleans.Serialization.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Extensions.Options;

namespace Orleans.Serialization
{
    /// <summary>
    /// Extensions for <see cref="ISerializerBuilder"/>.
    /// </summary>
    public static class SerializerBuilderExtensions
    {
        private static readonly object _assembliesKey = new();

        /// <summary>
        /// Configures the serialization builder.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <param name="factory">The factory.</param>
        /// <returns>The serialization builder</returns>
        public static ISerializerBuilder Configure(this ISerializerBuilder builder, Func<IServiceProvider, IConfigureOptions<TypeManifestOptions>> factory) => ((ISerializerBuilderImplementation)builder).ConfigureServices(services => services.AddTransient(sp => factory(sp)));

        /// <summary>
        /// Configures the serialization builder.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <param name="configure">The configuration delegate.</param>
        /// <returns>The serialization builder</returns>
        public static ISerializerBuilder Configure(this ISerializerBuilder builder, IConfigureOptions<TypeManifestOptions> configure) => ((ISerializerBuilderImplementation)builder).ConfigureServices(services => services.AddSingleton(configure));

        /// <summary>
        /// Configures the serialization builder.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <param name="configure">The configuration delegate.</param>
        /// <returns>The serialization builder</returns>
        public static ISerializerBuilder Configure(this ISerializerBuilder builder, Action<TypeManifestOptions> configure) => ((ISerializerBuilderImplementation)builder).ConfigureServices(services => services.Configure(configure));

        /// <summary>
        /// Adds an assembly to the builder.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <param name="assembly">The assembly.</param>
        /// <returns>The serialization builder</returns>
        public static ISerializerBuilder AddAssembly(this ISerializerBuilder builder, Assembly assembly)
        {
            var builderImpl = (ISerializerBuilderImplementation)builder;
            var properties = builderImpl.Properties;
            HashSet<Assembly> assembliesSet;
            if (!properties.TryGetValue(_assembliesKey, out var assembliesSetObj))
            {
                assembliesSet = new HashSet<Assembly>();
                properties[_assembliesKey] = assembliesSet;
            }
            else
            {
                assembliesSet = (HashSet<Assembly>)assembliesSetObj;
            }
                
            if (!assembliesSet.Add(assembly))
            {
                return builder;
            }

            var attrs = assembly.GetCustomAttributes<TypeManifestProviderAttribute>();

            foreach (var attr in attrs)
            {
                _ = builderImpl.ConfigureServices(services => services.AddSingleton(typeof(IConfigureOptions<TypeManifestOptions>), attr.ProviderType));
            }

            return builder;
        }
    }
}