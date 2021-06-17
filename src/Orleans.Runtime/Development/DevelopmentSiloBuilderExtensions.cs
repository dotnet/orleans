using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.LeaseProviders;

namespace Orleans.Runtime.Development
{
    public static class DevelopmentSiloBuilderExtensions
    {
        /// <summary>
        /// Configure silo with test/development features.
        /// NOT FOR PRODUCTION USE - dev/test only
        /// </summary>
        public static ISiloBuilder UseInMemoryLeaseProvider(this ISiloBuilder builder)
        {
            return builder.ConfigureServices(UseInMemoryLeaseProvider);
        }

        private static void UseInMemoryLeaseProvider(IServiceCollection services)
        {
            services.AddTransient<ILeaseProvider, InMemoryLeaseProvider>();
        }
    }
}
