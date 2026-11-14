using Orleans.Serialization.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System;
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
        /// Adds the context's closed type graph to serializer resolution.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <param name="context">The generated context.</param>
        /// <returns>The serialization builder.</returns>
        public static ISerializerBuilder AddGeneratedContext(this ISerializerBuilder builder, GeneratedContext context)
        {
            if (builder is null) throw new ArgumentNullException(nameof(builder));
            if (context is null) throw new ArgumentNullException(nameof(context));
            return builder.Configure(context);
        }

        /// <summary>
        /// Configures the serialization builder.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <param name="factory">The factory.</param>
        /// <returns>The serialization builder</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        public static ISerializerBuilder Configure(this ISerializerBuilder builder, Func<IServiceProvider, IConfigureOptions<TypeManifestOptions>> factory)
        {
            if (builder is null) throw new ArgumentNullException(nameof(builder));
            builder.Services.AddSingleton<IConfigureOptions<TypeManifestOptions>>(factory);
            return builder;
        }

        /// <summary>
        /// Configures the serialization builder.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <param name="configure">The configuration delegate.</param>
        /// <returns>The serialization builder</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        public static ISerializerBuilder Configure(this ISerializerBuilder builder, IConfigureOptions<TypeManifestOptions> configure)
        {
            if (builder is null) throw new ArgumentNullException(nameof(builder));
            builder.Services.AddSingleton<IConfigureOptions<TypeManifestOptions>>(configure);
            return builder;
        }

        /// <summary>
        /// Configures the serialization builder.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <param name="configure">The configuration delegate.</param>
        /// <returns>The serialization builder</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        public static ISerializerBuilder Configure(this ISerializerBuilder builder, Action<TypeManifestOptions> configure)
        {
            if (builder is null) throw new ArgumentNullException(nameof(builder));
            builder.Services.Configure(configure);
            return builder;
        }

        /// <summary>
        /// Adds an assembly to the builder.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <param name="assembly">The assembly.</param>
        /// <returns>The serialization builder</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        public static ISerializerBuilder AddAssembly(this ISerializerBuilder builder, Assembly assembly)
        {
            if (builder is null) throw new ArgumentNullException(nameof(builder));
            var attrs = assembly.GetCustomAttributes<TypeManifestProviderAttribute>();

            foreach (var attr in attrs)
            {
                _ = builder.Services.AddSingleton(typeof(IConfigureOptions<TypeManifestOptions>), attr.ProviderType);
            }

            return builder;
        }
    }
}
