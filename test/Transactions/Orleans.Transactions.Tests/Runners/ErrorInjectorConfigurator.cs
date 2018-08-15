using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.TestingHost;

namespace Orleans.Transactions.Tests
{
    public class ErrorInjectorConfigurator : ISiloBuilderConfigurator
    {
        private static readonly double probability = 0.05;

        public void Configure(ISiloHostBuilder hostBuilder)
        {
            hostBuilder
                .ConfigureServices(services => services.AddSingleton<ITransactionalFaultInjector>(sp => new ErrorInjector(probability)));
        }
    }
}
