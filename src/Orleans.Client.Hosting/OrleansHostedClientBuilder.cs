using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Client.Hosting
{
    public class OrleansHostedClientBuilder: IOrleansHostedClientBuilder
    {
        public OrleansHostedClientBuilder(IServiceCollection services)
        {
            this.Services = services;
        }

        public IServiceCollection Services { get; set; }
    }
}
