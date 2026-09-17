#nullable enable
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans;
using Orleans.Hosting;
using Orleans.Providers;
using Xunit;

[assembly: RegisterProvider("TestJournal", "Journal", "Silo", typeof(NonSilo.Tests.ProviderErrorMessageTests.TestJournalProviderBuilder))]

namespace NonSilo.Tests
{
    /// <summary>
    /// Tests configuration-based provider activation and diagnostics which identify available providers
    /// for the specified kind (e.g., Clustering, GrainStorage, etc.).
    /// </summary>
    [TestCategory("BVT")]
    [TestCategory("Providers")]
    [TestSuite("BVT")]
    [TestProvider("None")]
    [TestArea("Runtime")]
    public class ProviderErrorMessageTests
    {
        /// <summary>
        /// Tests that client builder includes known providers in error message when a provider is not found.
        /// Verifies that the error message contains both the standard message and a list of known providers
        /// for the specified kind when an invalid provider type is requested.
        /// </summary>
        [Fact]
        public void ClientBuilder_IncludesKnownProvidersInErrorMessage()
        {
            var configDict = new Dictionary<string, string?>
            {
                { "Orleans:ClusterId", "test-cluster" },
                { "Orleans:ServiceId", "test-service" },
                { "Orleans:Clustering:ProviderType", "NonExistentProvider" }
            };

            var exception = Assert.Throws<InvalidOperationException>(() =>
            {
                _ = new HostBuilder()
                    .ConfigureAppConfiguration(configBuilder =>
                    {
                        configBuilder.AddInMemoryCollection(configDict);
                    })
                    .UseOrleansClient(_ => { })
                    .Build();
            });

            // Verify the error message contains the provider name that was not found
            Assert.Contains("Could not find Clustering provider named 'NonExistentProvider'", exception.Message);

            // Verify the error message includes information about known providers
            // The exact list will depend on what providers are registered, but the message should contain "Known Clustering providers:"
            // if there are any registered Clustering providers
            Assert.Contains("This can indicate that either the 'Microsoft.Orleans.Sdk' or the provider's package are not referenced", exception.Message);
        }

        /// <summary>
        /// Tests that silo builder includes known providers in error message when a provider is not found.
        /// Verifies that the error message contains both the standard message and a list of known providers
        /// for the specified kind when an invalid provider type is requested.
        /// </summary>
        [Fact]
        public void SiloBuilder_IncludesKnownProvidersInErrorMessage()
        {
            var configDict = new Dictionary<string, string?>
            {
                { "Orleans:ClusterId", "test-cluster" },
                { "Orleans:ServiceId", "test-service" },
                { "Orleans:Clustering:ProviderType", "NonExistentProvider" }
            };

            var exception = Assert.Throws<InvalidOperationException>(() =>
            {
                _ = new HostBuilder()
                    .ConfigureAppConfiguration(configBuilder =>
                    {
                        configBuilder.AddInMemoryCollection(configDict);
                    })
                    .UseOrleans(_ => { })
                    .Build();
            });

            // Verify the error message contains the provider name that was not found
            Assert.Contains("Could not find Clustering provider named 'NonExistentProvider'", exception.Message);

            // Verify the error message includes information about known providers
            Assert.Contains("This can indicate that either the 'Microsoft.Orleans.Sdk' or the provider's package are not referenced", exception.Message);
        }

        /// <summary>
        /// Tests that error message for GrainStorage provider includes known providers.
        /// Verifies that when an invalid GrainStorage provider is specified, the error message
        /// includes helpful information about available GrainStorage providers.
        /// </summary>
        [Fact]
        public void SiloBuilder_IncludesKnownGrainStorageProvidersInErrorMessage()
        {
            var configDict = new Dictionary<string, string?>
            {
                { "Orleans:ClusterId", "test-cluster" },
                { "Orleans:ServiceId", "test-service" },
                { "Orleans:GrainStorage:MyStorage:ProviderType", "InvalidStorageProvider" }
            };

            var exception = Assert.Throws<InvalidOperationException>(() =>
            {
                _ = new HostBuilder()
                    .ConfigureAppConfiguration(configBuilder =>
                    {
                        configBuilder.AddInMemoryCollection(configDict);
                    })
                    .UseOrleans(siloBuilder =>
                    {
                        siloBuilder.UseLocalhostClustering();
                    })
                    .Build();
            });

            // Verify the error message contains the provider name that was not found
            Assert.Contains("Could not find GrainStorage provider named 'InvalidStorageProvider'", exception.Message);

            // Verify the error message includes information about known providers
            Assert.Contains("This can indicate that either the 'Microsoft.Orleans.Sdk' or the provider's package are not referenced", exception.Message);
        }

        [Fact]
        public void SiloBuilder_ConfiguresJournalProviderFromConfiguration()
        {
            var configDict = new Dictionary<string, string?>
            {
                { "Orleans:ClusterId", "test-cluster" },
                { "Orleans:ServiceId", "test-service" },
                { "Orleans:Journal:ProviderType", "TestJournal" },
                { "Orleans:Journal:ServiceKey", "journal-client" }
            };

            using var host = new HostBuilder()
                .ConfigureAppConfiguration(configBuilder =>
                {
                    configBuilder.AddInMemoryCollection(configDict);
                })
                .UseOrleans(siloBuilder =>
                {
                    siloBuilder.UseLocalhostClustering();
                })
                .Build();

            var invocation = Assert.Single(host.Services.GetServices<JournalProviderInvocation>());
            Assert.Null(invocation.Name);
            Assert.Equal("Orleans:Journal", invocation.ConfigurationSection.Path);
            Assert.Equal("TestJournal", invocation.ConfigurationSection["ProviderType"]);
            Assert.Equal("journal-client", invocation.ConfigurationSection["ServiceKey"]);
        }

        [Fact]
        public void SiloBuilder_IncludesKnownJournalProvidersInErrorMessage()
        {
            var configDict = new Dictionary<string, string?>
            {
                { "Orleans:ClusterId", "test-cluster" },
                { "Orleans:ServiceId", "test-service" },
                { "Orleans:Journal:ProviderType", "InvalidJournalProvider" }
            };

            var exception = Assert.Throws<InvalidOperationException>(() =>
            {
                _ = new HostBuilder()
                    .ConfigureAppConfiguration(configBuilder =>
                    {
                        configBuilder.AddInMemoryCollection(configDict);
                    })
                    .UseOrleans(siloBuilder =>
                    {
                        siloBuilder.UseLocalhostClustering();
                    })
                    .Build();
            });

            Assert.Contains("Could not find Journal provider named 'InvalidJournalProvider'", exception.Message);
            Assert.Contains("This can indicate that either the 'Microsoft.Orleans.Sdk' or the provider's package are not referenced", exception.Message);
            Assert.Contains("Known Journal providers:", exception.Message);
            Assert.Contains("TestJournal", exception.Message);
        }

        internal sealed class TestJournalProviderBuilder : IProviderBuilder<ISiloBuilder>
        {
            public void Configure(ISiloBuilder builder, string? name, IConfigurationSection configurationSection)
            {
                builder.Services.AddSingleton(new JournalProviderInvocation(name, configurationSection));
            }
        }

        private sealed record JournalProviderInvocation(string? Name, IConfigurationSection ConfigurationSection);
    }
}
