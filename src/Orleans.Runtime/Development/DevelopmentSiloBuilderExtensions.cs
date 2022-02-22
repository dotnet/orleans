using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.LeaseProviders;

namespace Orleans.Runtime.Development
{
    /// <summary>
    /// <see cref="ISiloBuilder"/> extensions to configure an in-memory lease provider.
    /// </summary>
    public static class DevelopmentSiloBuilderExtensions
    {
        /// <summary>
        /// Configures silo with test/development features.
        /// </summary>
        /// <remarks>
        /// Not for production use. This is for development and test scenarios only.
        /// </remarks>
        /// <param name="builder">The builder.</param>
        /// <returns>The builder.</returns>
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
