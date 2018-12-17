using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Orleans.Hosting
{
    public static class GenericHostExtensions
    {
        private const string SILO_BUILDER_KEY = "SiloBuilder";
        public static IHostBuilder AddOrleans(this IHostBuilder hostBuilder, Action<ISiloBuilder> configureDelegate)
        {
            if (configureDelegate == null) throw new ArgumentNullException(nameof(configureDelegate));

            SiloBuilder siloBuilder;
            if (!hostBuilder.Properties.ContainsKey(SILO_BUILDER_KEY))
            {
                siloBuilder = new SiloBuilder(hostBuilder);
                hostBuilder.Properties.Add(SILO_BUILDER_KEY, siloBuilder);
            }
            else
            {
                siloBuilder = (SiloBuilder)hostBuilder.Properties[SILO_BUILDER_KEY];
            }

            configureDelegate.Invoke(siloBuilder);
            return hostBuilder;
        }
    }
}