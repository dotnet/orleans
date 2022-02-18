using System;
using Orleans.Runtime;
using Orleans.Streams;

namespace Orleans
{
    /// <summary>
    /// Extension methods for accessing stream providers from a <see cref="Grain"/> or <see cref="IGrainBase"/> implementation.
    /// </summary>
    public static class GrainStreamingExtensions
    {
        public static IStreamProvider GetStreamProvider(this Grain grain, string name)
            => GetStreamProvider((IGrainBase)grain, name);

        public static IStreamProvider GetStreamProvider(this IGrainBase grain, string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentNullException(nameof(name));
            }

            return grain.GrainContext.ActivationServices.GetRequiredServiceByName<IStreamProvider>(name);
        }
    }

    /// <summary>
    /// Extension methods for accessing stream providers from a client.
    /// </summary>
    public static class ClientStreamingExtensions
    {
        /// <inheritdoc />
        public static IStreamProvider GetStreamProvider(this IClusterClient client, string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentNullException(nameof(name));
            }

            return client.ServiceProvider.GetRequiredServiceByName<IStreamProvider>(name);
        }
    }
}
