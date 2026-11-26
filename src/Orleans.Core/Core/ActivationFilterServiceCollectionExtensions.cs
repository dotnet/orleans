using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Hosting
{
    /// <summary>
    /// <see cref="IServiceCollection"/> extensions for <see cref="IActivationFilter"/>.
    /// </summary>
    internal static class ActivationFilterServiceCollectionExtensions
    {
        /// <summary>
        /// Adds an <see cref="IActivationFilter"/> to the filter pipeline.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="filter">The filter.</param>
        /// <returns>The service collection.</returns>
        internal static IServiceCollection AddActivationFilter(this IServiceCollection services, IActivationFilter filter)
        {
            return services.AddSingleton(filter);
        }

        /// <summary>
        /// Adds an <see cref="IActivationFilter"/> to the filter pipeline.
        /// </summary>
        /// <typeparam name="TImplementation">The filter implementation type.</typeparam>
        /// <param name="services">The service collection.</param>
        /// <returns>The service collection.</returns>
        internal static IServiceCollection AddActivationFilter<
            [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TImplementation>(
            this IServiceCollection services)
            where TImplementation : class, IActivationFilter
        {
            return services.AddSingleton<IActivationFilter, TImplementation>();
        }

        /// <summary>
        /// Adds an <see cref="IActivationFilter"/> to the filter pipeline via a delegate.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="filter">The filter.</param>
        /// <returns>The service collection.</returns>
        internal static IServiceCollection AddActivationFilter(this IServiceCollection services, ActivationFilterDelegate filter)
        {
            return services.AddSingleton<IActivationFilter>(new ActivationFilterWrapper(filter));
        }

        /// <summary>
        /// Implements <see cref="IActivationFilter"/> by delegating all <see cref="IActivationFilter.Invoke"/> calls to the provided delegate.
        /// </summary>
        private sealed class ActivationFilterWrapper : IActivationFilter
        {
            private readonly ActivationFilterDelegate interceptor;

            /// <summary>
            /// Initializes a new instance of the <see cref="ActivationFilterWrapper"/> class.
            /// </summary>
            /// <param name="interceptor">The interceptor.</param>
            public ActivationFilterWrapper(ActivationFilterDelegate interceptor)
            {
                this.interceptor = interceptor;
            }

            /// <summary>
            /// Invokes this filter.
            /// </summary>
            /// <param name="context">The activation context.</param>
            /// <returns>A <see cref="Task"/> representing the work performed.</returns>
            public Task Invoke(IActivationContext context) => this.interceptor.Invoke(context);
        }
    }
}
