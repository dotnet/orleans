using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Providers;

namespace Orleans.Configuration
{
    /// <summary>
    /// Extension methods for configuring dependency injection.
    /// </summary>
    public static class ServiceCollectionExtensions
    {
        /// <summary>
        /// Adds an <see cref="IGrainCallFilter"/> to the filter pipeline.
        /// </summary>
        /// <param name="collection">The service collection.</param>
        /// <param name="filter">The filter.</param>
        /// <returns>The service collection.</returns>
        public static IServiceCollection AddGrainCallFilter(this IServiceCollection collection, IGrainCallFilter filter)
        {
            return collection.AddSingleton(filter);
        }

        /// <summary>
        /// Adds an <see cref="IGrainCallFilter"/> to the filter pipeline.
        /// </summary>
        /// <typeparam name="TImplementation">The filter implementation type.</typeparam>
        /// <param name="collection">The service collection.</param>
        /// <returns>The service collection.</returns>
        public static IServiceCollection AddGrainCallFilter<TImplementation>(this IServiceCollection collection)
            where TImplementation : class, IGrainCallFilter
        {
            return collection.AddSingleton<IGrainCallFilter, TImplementation>();
        }

        /// <summary>
        /// Adds an <see cref="IGrainCallFilter"/> to the filter pipeline via a delegate.
        /// </summary>
        /// <param name="collection">The service collection.</param>
        /// <param name="filter">The filter.</param>
        /// <returns>The service collection.</returns>
        public static IServiceCollection AddGrainCallFilter(this IServiceCollection collection, GrainCallFilterDelegate filter)
        {
            return collection.AddSingleton<IGrainCallFilter>(
                new GrainCallFilterWrapper(filter));
        }

        private class GrainCallFilterWrapper : IGrainCallFilter
        {
            private readonly GrainCallFilterDelegate interceptor;

            public GrainCallFilterWrapper(GrainCallFilterDelegate interceptor)
            {
                this.interceptor = interceptor;
            }

            public Task Invoke(IGrainCallContext context) => this.interceptor.Invoke(context);
        }
    }
}
