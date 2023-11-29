using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Streams;

namespace Orleans
{
    /// <summary>
    /// Extension methods for accessing stream providers from a <see cref="Grain"/> or <see cref="IGrainBase"/> implementation.
    /// </summary>
    public static class GrainStreamingExtensions
    {
        /// <summary>
        /// Gets the stream provider with the specified <paramref name="name"/>.
        /// </summary>
        /// <param name="grain">The grain.</param>
        /// <param name="name">The provider name.</param>
        /// <returns>The stream provider.</returns>
        public static IStreamProvider GetStreamProvider(this Grain grain, string name)
            => GetStreamProvider((IGrainBase)grain, name);

        /// <summary>
        /// Gets the stream provider with the specified <paramref name="name"/>.
        /// </summary>
        /// <param name="grain">The grain.</param>
        /// <param name="name">The provider name.</param>
        /// <returns>The stream provider.</returns>
        public static IStreamProvider GetStreamProvider(this IGrainBase grain, string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentNullException(nameof(name));
            }

            try
            {
                return grain.GrainContext.ActivationServices.GetRequiredKeyedService<IStreamProvider>(name);
            }
            catch (InvalidOperationException ex)
            {
                // We used to throw KeyNotFoundException before, keep it like this for backward compatibility
                throw new KeyNotFoundException($"Stream provider '{name}' not found", ex);
            }
        }
    }

    /// <summary>
    /// Extension methods for accessing stream providers from a client.
    /// </summary>
    public static class ClientStreamingExtensions
    {
        /// <summary>
        /// Gets the stream provider with the specified <paramref name="name"/>.
        /// </summary>
        /// <param name="client">The client.</param>
        /// <param name="name">The provider name.</param>
        /// <returns>The stream provider.</returns>
        public static IStreamProvider GetStreamProvider(this IClusterClient client, string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentNullException(nameof(name));
            }

            try
            {
                return client.ServiceProvider.GetRequiredKeyedService<IStreamProvider>(name);
            }
            catch (InvalidOperationException ex)
            {
                // We used to throw KeyNotFoundException before, keep it like this for backward compatibility
                throw new KeyNotFoundException($"Stream provider '{name}' not found", ex);
            }
        }
    }
}
