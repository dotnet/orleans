namespace Orleans.Hosting
{
    public static class GrainCallFilterExtensions
    {
        /// <summary>
        /// Adds an <see cref="IIncomingGrainCallFilter"/> to the filter pipeline.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <param name="filter">The filter.</param>
        /// <returns>The builder.</returns>
        public static ISiloBuilder AddIncomingGrainCallFilter(this ISiloBuilder builder, IIncomingGrainCallFilter filter)
        {
            return builder.ConfigureServices(services => services.AddIncomingGrainCallFilter(filter));
        }

        /// <summary>
        /// Adds an <see cref="IIncomingGrainCallFilter"/> to the filter pipeline.
        /// </summary>
        /// <typeparam name="TImplementation">The filter implementation type.</typeparam>
        /// <param name="builder">The builder.</param>
        /// <returns>The builder.</returns>
        public static ISiloBuilder AddIncomingGrainCallFilter<TImplementation>(this ISiloBuilder builder)
            where TImplementation : class, IIncomingGrainCallFilter
        {
            return builder.ConfigureServices(services => services.AddIncomingGrainCallFilter<TImplementation>());
        }

        /// <summary>
        /// Adds an <see cref="IIncomingGrainCallFilter"/> to the filter pipeline via a delegate.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <param name="filter">The filter.</param>
        /// <returns>The builder.</returns>
        public static ISiloBuilder AddIncomingGrainCallFilter(this ISiloBuilder builder, IncomingGrainCallFilterDelegate filter)
        {
            return builder.ConfigureServices(services => services.AddIncomingGrainCallFilter(filter));
        }

        /// <summary>
        /// Adds an <see cref="IOutgoingGrainCallFilter"/> to the filter pipeline.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <param name="filter">The filter.</param>
        /// <returns>The builder.</returns>
        public static ISiloBuilder AddOutgoingGrainCallFilter(this ISiloBuilder builder, IOutgoingGrainCallFilter filter)
        {
            return builder.ConfigureServices(services => services.AddOutgoingGrainCallFilter(filter));
        }

        /// <summary>
        /// Adds an <see cref="IOutgoingGrainCallFilter"/> to the filter pipeline.
        /// </summary>
        /// <typeparam name="TImplementation">The filter implementation type.</typeparam>
        /// <param name="builder">The builder.</param>
        /// <returns>The builder.</returns>
        public static ISiloBuilder AddOutgoingGrainCallFilter<TImplementation>(this ISiloBuilder builder)
            where TImplementation : class, IOutgoingGrainCallFilter
        {
            return builder.ConfigureServices(services => services.AddOutgoingGrainCallFilter<TImplementation>());
        }

        /// <summary>
        /// Adds an <see cref="IOutgoingGrainCallFilter"/> to the filter pipeline via a delegate.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <param name="filter">The filter.</param>
        /// <returns>The builder.</returns>
        public static ISiloBuilder AddOutgoingGrainCallFilter(this ISiloBuilder builder, OutgoingGrainCallFilterDelegate filter)
        {
            return builder.ConfigureServices(services => services.AddOutgoingGrainCallFilter(filter));
        }
    }
}
