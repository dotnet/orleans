using System;
using Orleans.Runtime;
using Orleans.Streams;

namespace Orleans
{
    public static class GrainStreamingExtensions
    {
        public static IStreamProvider GetStreamProvider(this Grain grain, string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentNullException(nameof(name));
            }

            return grain.ServiceProvider.GetRequiredServiceByName<IStreamProvider>(name);
        }
    }

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
