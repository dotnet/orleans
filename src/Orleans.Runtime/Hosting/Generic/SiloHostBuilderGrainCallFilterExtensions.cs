namespace Orleans.Hosting
{
    /// <summary>
    /// Extensions for configuring grain call filters.
    /// </summary>
    public static class SiloHostBuilderGrainCallFilterExtensions
    {
        /// <summary>
        /// Adds an <see cref="IIncomingGrainCallFilter"/> to the filter pipeline.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <param name="filter">The filter.</param>
        /// <returns>The builder.</returns>
        public static ISiloHostBuilder AddIncomingGrainCallFilter(this ISiloHostBuilder builder, IIncomingGrainCallFilter filter)
        {
            return builder.ConfigureServices(services => services.AddIncomingGrainCallFilter(filter));
        }

        /// <summary>
        /// Adds an <see cref="IIncomingGrainCallFilter"/> to the filter pipeline.
        /// </summary>
        /// <typeparam name="TImplementation">The filter implementation type.</typeparam>
        /// <param name="builder">The builder.</param>
        /// <returns>The builder.</returns>
        public static ISiloHostBuilder AddIncomingGrainCallFilter<TImplementation>(this ISiloHostBuilder builder)
            where TImplementation : class, IIncomingGrainCallFilter
        {
            return builder.ConfigureServices(services => services.AddIncomingGrainCallFilter<TImplementation>());
        }

        /// <summary>
        /// Adds an <see cref="IOutgoingGrainCallFilter"/> to the filter pipeline via a delegate.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <param name="filter">The filter.</param>
        /// <returns>The builder.</returns>
        public static ISiloHostBuilder AddIncomingGrainCallFilter(this ISiloHostBuilder builder, IncomingGrainCallFilterDelegate filter)
        {
            return builder.ConfigureServices(services => services.AddIncomingGrainCallFilter(filter));
        }

        /// <summary>
        /// Adds an <see cref="IOutgoingGrainCallFilter"/> to the filter pipeline.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <param name="filter">The filter.</param>
        /// <returns>The builder.</returns>
        public static ISiloHostBuilder AddOutgoingGrainCallFilter(this ISiloHostBuilder builder, IOutgoingGrainCallFilter filter)
        {
            return builder.ConfigureServices(services => services.AddOutgoingGrainCallFilter(filter));
        }

        /// <summary>
        /// Adds an <see cref="IOutgoingGrainCallFilter"/> to the filter pipeline.
        /// </summary>
        /// <typeparam name="TImplementation">The filter implementation type.</typeparam>
        /// <param name="builder">The builder.</param>
        /// <returns>The builder.</returns>
        public static ISiloHostBuilder AddOutgoingGrainCallFilter<TImplementation>(this ISiloHostBuilder builder)
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
        public static ISiloHostBuilder AddOutgoingGrainCallFilter(this ISiloHostBuilder builder, OutgoingGrainCallFilterDelegate filter)
        {
            return builder.ConfigureServices(services => services.AddOutgoingGrainCallFilter(filter));
        }
    }
}