using System;

namespace Orleans.Streams
{
    public static class StreamProviderManagerExtensions
    {
        public static IStreamProvider GetStreamProvider(this IStreamProviderManager streamProviderManager, string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentNullException(nameof(name));
            return streamProviderManager.GetProvider(name) as IStreamProvider;
        }
    }
}