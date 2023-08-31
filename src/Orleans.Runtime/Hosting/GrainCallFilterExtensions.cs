using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Hosting
{
    /// <summary>
    /// Extension methods for configuring <see cref="IIncomingGrainCallFilter"/> and <see cref="IOutgoingGrainCallFilter"/> implementations.
    /// </summary>
    public static class GrainCallFilterSiloBuilderExtensions
    {
        /// <summary>
        /// Adds an <see cref="IIncomingGrainCallFilter"/> to the filter pipeline.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <param name="filter">The filter.</param>
        /// <returns>The builder.</returns>
        public static ISiloBuilder AddIncomingGrainCallFilter(this ISiloBuilder builder, IIncomingGrainCallFilter filter) => builder.ConfigureServices(services => services.AddIncomingGrainCallFilter(filter));

        /// <summary>
        /// Adds an <see cref="IIncomingGrainCallFilter"/> to the filter pipeline.
        /// </summary>
        /// <typeparam name="TImplementation">The filter implementation type.</typeparam>
        /// <param name="builder">The builder.</param>
        /// <returns>The builder.</returns>
        public static ISiloBuilder AddIncomingGrainCallFilter<TImplementation>(this ISiloBuilder builder)
            where TImplementation : class, IIncomingGrainCallFilter => builder.ConfigureServices(services => services.AddIncomingGrainCallFilter<TImplementation>());

        /// <summary>
        /// Adds an <see cref="IIncomingGrainCallFilter"/> to the filter pipeline via a delegate.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <param name="filter">The filter.</param>
        /// <returns>The builder.</returns>
        public static ISiloBuilder AddIncomingGrainCallFilter(this ISiloBuilder builder, IncomingGrainCallFilterDelegate filter) => builder.ConfigureServices(services => services.AddIncomingGrainCallFilter(filter));

        /// <summary>
        /// Adds an <see cref="IOutgoingGrainCallFilter"/> to the filter pipeline.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <param name="filter">The filter.</param>
        /// <returns>The builder.</returns>
        public static ISiloBuilder AddOutgoingGrainCallFilter(this ISiloBuilder builder, IOutgoingGrainCallFilter filter) => builder.ConfigureServices(services => services.AddOutgoingGrainCallFilter(filter));

        /// <summary>
        /// Adds an <see cref="IOutgoingGrainCallFilter"/> to the filter pipeline.
        /// </summary>
        /// <typeparam name="TImplementation">The filter implementation type.</typeparam>
        /// <param name="builder">The builder.</param>
        /// <returns>The builder.</returns>
        public static ISiloBuilder AddOutgoingGrainCallFilter<TImplementation>(this ISiloBuilder builder)
            where TImplementation : class, IOutgoingGrainCallFilter => builder.ConfigureServices(services => services.AddOutgoingGrainCallFilter<TImplementation>());

        /// <summary>
        /// Adds an <see cref="IOutgoingGrainCallFilter"/> to the filter pipeline via a delegate.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <param name="filter">The filter.</param>
        /// <returns>The builder.</returns>
        public static ISiloBuilder AddOutgoingGrainCallFilter(this ISiloBuilder builder, OutgoingGrainCallFilterDelegate filter) => builder.ConfigureServices(services => services.AddOutgoingGrainCallFilter(filter));
    }
}
