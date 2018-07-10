using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Xunit;

namespace NonSilo.Tests
{
    /// <summary>
    /// Tests for configuration.
    /// </summary>
    [TestCategory("BVT"), TestCategory("Config")]
    public class ConfigurationTests
    {
        /// <summary>
        /// Tests that <see cref="ClientMessagingOptions"/> can be customized by registering configuration delegates for
        /// <see cref="MessagingOptions"/>.
        /// </summary>
        [Fact]
        public void Configure_ClientMessagingOptions_Via_MessagingOptions()
        {
            var client = new ClientBuilder()
                .Configure<MessagingOptions>(options => options.ResponseTimeout = TimeSpan.FromTicks(12345))
                .Configure<ClientMessagingOptions>(options => options.ClientSenderBuckets = 89)
                .UseLocalhostClustering()
                .Build();

            var opts = client.ServiceProvider.GetRequiredService<IOptions<ClientMessagingOptions>>().Value;
            Assert.Equal(12345, opts.ResponseTimeout.Ticks);
            Assert.Equal(89, opts.ClientSenderBuckets);
        }

        /// <summary>
        /// Tests that <see cref="SiloMessagingOptions"/> can be customized by registering configuration delegates for
        /// <see cref="MessagingOptions"/>.
        /// </summary>
        [Fact]
        public void Configure_SiloMessagingOptions_Via_MessagingOptions()
        {
            var silo = new SiloHostBuilder()
                .Configure<MessagingOptions>(options => options.ResponseTimeout = TimeSpan.FromTicks(12345))
                .Configure<SiloMessagingOptions>(options => options.ClientDropTimeout = TimeSpan.FromTicks(6789))
                .UseLocalhostClustering()
                .Build();

            var opts = silo.Services.GetRequiredService<IOptions<SiloMessagingOptions>>().Value;
            Assert.Equal(12345, opts.ResponseTimeout.Ticks);
            Assert.Equal(6789, opts.ClientDropTimeout.Ticks);
        }
    }
}
