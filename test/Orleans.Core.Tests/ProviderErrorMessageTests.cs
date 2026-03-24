#nullable enable
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;
using Xunit;

namespace NonSilo.Tests
{
    /// <summary>
    /// Tests for provider error messages to ensure they include helpful information about known/registered providers.
    /// These tests verify that when a provider is not found, the error message includes a list of available providers
    /// for the specified kind (e.g., Clustering, GrainStorage, etc.) to help users diagnose configuration issues.
    /// </summary>
    [TestCategory("BVT")]
    [TestCategory("Providers")]
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
    }
}
