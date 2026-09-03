using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.TestingHost;

namespace Orleans.Transactions.TestKit
{
    /// <summary>
    /// Configures a test silo to use a randomly skewed transaction clock.
    /// </summary>
    public class SkewedClockConfigurator : ISiloConfigurator
    {
        private static readonly TimeSpan MinSkew = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan MaxSkew = TimeSpan.FromSeconds(5);

        /// <inheritdoc/>
        public void Configure(ISiloBuilder hostBuilder)
        {
            hostBuilder
                .ConfigureServices(services => services.AddSingleton<IClock>(sp => new SkewedClock(MinSkew, MaxSkew)));
        }
    }
}
