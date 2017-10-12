using System;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Hosting
{
    internal class DelegateServiceProviderFactory : IServiceProviderFactory<IServiceCollection>
    {
        private readonly Func<IServiceCollection, IServiceProvider> containerBuilder;

        public DelegateServiceProviderFactory(Func<IServiceCollection, IServiceProvider> containerBuilder)
        {
            this.containerBuilder = containerBuilder;
        }

        /// <inheritdoc />
        public IServiceCollection CreateBuilder(IServiceCollection services) => services;

        /// <inheritdoc />
        public IServiceProvider CreateServiceProvider(IServiceCollection services) => this.containerBuilder(services);
    }
}