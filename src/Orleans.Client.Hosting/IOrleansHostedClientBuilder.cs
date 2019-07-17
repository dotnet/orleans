using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Client.Hosting
{
    public interface IOrleansHostedClientBuilder
    {
        IServiceCollection Services { get; set; }
    }
}
