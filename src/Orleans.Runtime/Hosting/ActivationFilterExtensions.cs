using System.Diagnostics.CodeAnalysis;

namespace Orleans.Hosting
{
    /// <summary>
    /// Extension methods for configuring <see cref="IActivationFilter"/> implementations.
    /// </summary>
    public static class ActivationFilterSiloBuilderExtensions
    {
        /// <summary>
        /// Adds an <see cref="IActivationFilter"/> to the filter pipeline.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <param name="filter">The filter.</param>
        /// <returns>The builder.</returns>
        public static ISiloBuilder AddActivationFilter(this ISiloBuilder builder, IActivationFilter filter)
        {
            return builder.ConfigureServices(services => services.AddActivationFilter(filter));
        }

        /// <summary>
        /// Adds an <see cref="IActivationFilter"/> to the filter pipeline.
        /// </summary>
        /// <typeparam name="TImplementation">The filter implementation type.</typeparam>
        /// <param name="builder">The builder.</param>
        /// <returns>The builder.</returns>
        public static ISiloBuilder AddActivationFilter<
            [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TImplementation>(
            this ISiloBuilder builder)
            where TImplementation : class, IActivationFilter
        {
            return builder.ConfigureServices(services => services.AddActivationFilter<TImplementation>());
        }

        /// <summary>
        /// Adds an <see cref="IActivationFilter"/> to the filter pipeline via a delegate.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <param name="filter">The filter.</param>
        /// <returns>The builder.</returns>
        public static ISiloBuilder AddActivationFilter(this ISiloBuilder builder, ActivationFilterDelegate filter)
        {
            return builder.ConfigureServices(services => services.AddActivationFilter(filter));
        }
    }
}
