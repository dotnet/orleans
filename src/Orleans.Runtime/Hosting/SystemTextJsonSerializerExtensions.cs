using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;
using Orleans.Storage;

#nullable enable

namespace Orleans.Hosting
{
    /// <summary>
    /// Extensions for configuring System.Text.Json-based grain storage serialization.
    /// </summary>
    public static class SystemTextJsonSerializerExtensions
    {
        /// <summary>
        /// Configures System.Text.Json as the default grain storage serializer.
        /// </summary>
        /// <param name="siloBuilder">The silo builder to configure with System.Text.Json grain storage support.</param>
        /// <returns>The silo builder.</returns>
        public static ISiloBuilder UseSystemTextJsonGrainStorageSerializer(this ISiloBuilder siloBuilder)
        {
            siloBuilder.Services.AddSingleton<IGrainStorageSerializer, SystemTextJsonGrainStorageSerializer>();
            return siloBuilder;
        }

        /// <summary>
        /// Configures System.Text.Json as the default grain storage serializer.
        /// </summary>
        /// <param name="siloBuilder">The silo builder to configure with System.Text.Json grain storage support.</param>
        /// <param name="configureOptions">The delegate used to configure the serializer options.</param>
        /// <returns>The silo builder.</returns>
        /// <remarks>
        /// <paramref name="configureOptions"/> runs before Orleans adds its own converters, so converters added here take precedence.
        /// </remarks>
        public static ISiloBuilder UseSystemTextJsonGrainStorageSerializer(this ISiloBuilder siloBuilder, Action<SystemTextJsonGrainStorageSerializerOptions> configureOptions)
        {
            ArgumentNullException.ThrowIfNull(configureOptions);
            siloBuilder.Services.Configure(configureOptions);
            return siloBuilder.UseSystemTextJsonGrainStorageSerializer();
        }
    }
}
