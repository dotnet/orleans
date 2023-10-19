
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.EventSourcing;
using Orleans.Runtime;
using Orleans.Runtime.LogConsistency;

namespace Orleans.Hosting
{
    internal static class LogConsistencyProtocolSiloBuilderExtensions
    {
        internal static IServiceCollection AddLogConsistencyProtocolServicesFactory(this IServiceCollection services)
        {
            services.TryAddSingleton<Factory<IGrainContext, ILogConsistencyProtocolServices>>(serviceProvider =>
            {
                var factory = ActivatorUtilities.CreateFactory(typeof(ProtocolServices), new[] { typeof(IGrainContext) });
                return arg1 => (ILogConsistencyProtocolServices)factory(serviceProvider, new object[] { arg1 });
            });

            return services;
        }
    }
}