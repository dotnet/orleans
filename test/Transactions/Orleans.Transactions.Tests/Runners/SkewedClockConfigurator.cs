using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.TestingHost;

namespace Orleans.Transactions.Tests
{
    public class SkewedClockConfigurator : ISiloBuilderConfigurator
    {
        private static readonly TimeSpan MinSkew = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan MaxSkew = TimeSpan.FromSeconds(5);

        public void Configure(ISiloHostBuilder hostBuilder)
        {
            hostBuilder
                .ConfigureServices(services => services.AddSingleton<IClock>(sp => new SkewedClock(MinSkew, MaxSkew)));
        }
    }
}
