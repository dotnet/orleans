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
        public static ISerializerBuilder Configure(this ISerializerBuilder builder, Func<IServiceProvider, IConfigureOptions<TypeManifestOptions>> factory)
        {
            builder.Services.AddSingleton<IConfigureOptions<TypeManifestOptions>>(factory);
            return builder;
        }

        /// <summary>
        /// Configures the serialization builder.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <param name="configure">The configuration delegate.</param>
        /// <returns>The serialization builder</returns>
        public static ISerializerBuilder Configure(this ISerializerBuilder builder, IConfigureOptions<TypeManifestOptions> configure)
        {
            builder.Services.AddSingleton<IConfigureOptions<TypeManifestOptions>>(configure);
            return builder;
        }

        /// <summary>
        /// Configures the serialization builder.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <param name="configure">The configuration delegate.</param>
        /// <returns>The serialization builder</returns>
        public static ISerializerBuilder Configure(this ISerializerBuilder builder, Action<TypeManifestOptions> configure)
        {
            builder.Services.Configure(configure);
            return builder;
        }

        /// <summary>
        /// Adds an assembly to the builder.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <param name="assembly">The assembly.</param>
        /// <returns>The serialization builder</returns>
        public static ISerializerBuilder AddAssembly(this ISerializerBuilder builder, Assembly assembly)
        {
            var attrs = assembly.GetCustomAttributes<TypeManifestProviderAttribute>();

            foreach (var attr in attrs)
            {
                _ = builder.Services.AddSingleton(typeof(IConfigureOptions<TypeManifestOptions>), attr.ProviderType);
            }

            return builder;
        }
    }
}