using System;
using Microsoft.Extensions.Hosting;

namespace Orleans.Hosting
{
    public static class GenericHostExtensions
    {
        public static IHostBuilder AddOrleans(this IHostBuilder hostBuilder, Action<ISiloBuilder> configureDelegate)
        {
            if (configureDelegate == null) throw new ArgumentNullException(nameof(configureDelegate));
            var siloBuilder = new SiloBuilder(hostBuilder);
            configureDelegate.Invoke(siloBuilder);
            return hostBuilder;
        }
    }
}