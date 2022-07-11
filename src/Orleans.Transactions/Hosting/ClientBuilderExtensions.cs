namespace Orleans.Hosting
{
    public static class ClientBuilderExtensions
    {
        public static IClientBuilder UseTransactions(this IClientBuilder builder)
            => builder.ConfigureServices(services => services.UseTransactionsWithClient());
    }
}