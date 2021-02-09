using System;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Providers
{
    internal static class MemoryMessageBodySerializerFactory<TSerializer>
        where TSerializer : class, IMemoryMessageBodySerializer
    {
        private static readonly Lazy<ObjectFactory> ObjectFactory = new Lazy<ObjectFactory>(
            () => ActivatorUtilities.CreateFactory(
                typeof(TSerializer),
                Type.EmptyTypes));

        public static TSerializer GetOrCreateSerializer(IServiceProvider serviceProvider)
        {
            return serviceProvider.GetService<TSerializer>() ??
                   (TSerializer) ObjectFactory.Value(serviceProvider, null);
        }
    }
}