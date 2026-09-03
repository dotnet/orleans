namespace Orleans.Hosting
{
    /// <summary>
    /// Provides transaction configuration extensions for <see cref="IClientBuilder"/>.
    /// </summary>
    public static class ClientBuilderExtensions
    {
        /// <summary>
        /// Adds Orleans transaction services to the client.
        /// </summary>
        /// <param name="builder">The client builder.</param>
        /// <returns>The client builder.</returns>
        public static IClientBuilder UseTransactions(this IClientBuilder builder)
            => builder.ConfigureServices(services => services.UseTransactionsWithClient());
    }
}