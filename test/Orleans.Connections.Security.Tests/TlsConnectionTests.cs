using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Orleans.TestingHost;
using Xunit;

namespace Orleans.Connections.Security.Tests
{
    /// <summary>
    /// Tests for TLS (Transport Layer Security) support in Orleans connections.
    /// 
    /// Orleans supports TLS encryption for:
    /// - Client-to-silo connections (gateway connections)
    /// - Silo-to-silo connections (membership protocol)
    /// 
    /// Key features tested:
    /// - Certificate creation and encoding/decoding
    /// - Mutual TLS authentication (mTLS) with client certificates
    /// - Different certificate validation modes
    /// - End-to-end encrypted communication
    /// 
    /// TLS is essential for:
    /// - Securing Orleans deployments in untrusted networks
    /// - Meeting compliance requirements (HIPAA, PCI-DSS, etc.)
    /// - Preventing man-in-the-middle attacks
    /// - Authenticating clients and silos
    /// </summary>
    [Trait("Category", "BVT")]
    public class TlsConnectionTests
    {
        private const string CertificateSubjectName = "fakedomain.faketld";
        private const string CertificateConfigKey = "certificate";
        private const string ClientCertificateModeKey = "CertificateMode";

        /// <summary>
        /// Tests the certificate utility functions for creating self-signed certificates.
        /// Verifies that certificates can be:
        /// - Created with specific OIDs (Object Identifiers) for client/server authentication
        /// - Encoded to Base64 for configuration storage
        /// - Decoded back to the original certificate
        /// </summary>
        [Fact]
        public void CanCreateCertificates()
        {
            var original = TestCertificateHelper.CreateSelfSignedCertificate(
                CertificateSubjectName,
                new[] { TestCertificateHelper.ClientAuthenticationOid, TestCertificateHelper.ServerAuthenticationOid });
            var encoded = TestCertificateHelper.ConvertToBase64(original);
            var decoded = TestCertificateHelper.ConvertFromBase64(encoded);
            Assert.Equal(original, decoded);
        }
        
        /// <summary>
        /// Configures TLS for Orleans clients in the test cluster.
        /// Sets up:
        /// - Client certificate for mutual TLS
        /// - SSL protocols (TLS 1.2)
        /// - Certificate validation policies
        /// - Target host name for certificate validation
        /// </summary>
        private class TlsClientConfigurator : IClientBuilderConfigurator
        {
            public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
            {
                var encodedCertificate = configuration[CertificateConfigKey];
                var localCertificate = TestCertificateHelper.ConvertFromBase64(encodedCertificate);

                var certificateModeString = configuration[ClientCertificateModeKey];
                var certificateMode = (RemoteCertificateMode)Enum.Parse(typeof(RemoteCertificateMode), certificateModeString);

                clientBuilder.UseTls(options =>
                {
                    // Use TLS 1.2 for secure communication
                    options.SslProtocols = System.Security.Authentication.SslProtocols.Tls12;
                    // Allow any certificate for testing (in production, validate properly)
                    options.AllowAnyRemoteCertificate();
                    // Client's certificate for mutual TLS
                    options.LocalCertificate = localCertificate;
                    // Require server to present a certificate
                    options.RemoteCertificateMode = RemoteCertificateMode.RequireCertificate;
                    // Configure whether server requires client certificate
                    options.ClientCertificateMode = certificateMode;
                    // Set target host for certificate validation
                    options.OnAuthenticateAsClient = (connection, sslOptions) =>
                    {
                        sslOptions.TargetHost = CertificateSubjectName;
                    };
                });
            }
        }

        /// <summary>
        /// Configures TLS for Orleans silos in the test cluster.
        /// Sets up:
        /// - Server certificate for TLS
        /// - Client certificate requirements
        /// - SSL protocol versions
        /// - Certificate validation policies
        /// </summary>
        private class TlsServerConfigurator : IHostConfigurator
        {
            public void Configure(IHostBuilder hostBuilder)
            {
                var config = hostBuilder.GetConfiguration();
                var encodedCertificate = config[CertificateConfigKey];
                var localCertificate = TestCertificateHelper.ConvertFromBase64(encodedCertificate);

                var certificateModeString = config[ClientCertificateModeKey];
                var certificateMode = (RemoteCertificateMode)Enum.Parse(typeof(RemoteCertificateMode), certificateModeString);

                hostBuilder.UseOrleans((ctx, siloBuilder) =>
                {
                    siloBuilder.UseTls(localCertificate, options =>
                    {
                        // Use TLS 1.2 for secure communication
                        options.SslProtocols = System.Security.Authentication.SslProtocols.Tls12;
                        // Allow any certificate for testing (in production, validate properly)
                        options.AllowAnyRemoteCertificate();
                        // Allow but don't require remote certificates (for silo-to-silo)
                        options.RemoteCertificateMode = RemoteCertificateMode.AllowCertificate;
                        // Configure client certificate requirements based on test parameters
                        options.ClientCertificateMode = certificateMode;
                        // Set target host when acting as client (silo-to-silo connections)
                        options.OnAuthenticateAsClient = (connection, sslOptions) =>
                        {
                            sslOptions.TargetHost = CertificateSubjectName;
                        };
                    });
                });
            }
        }

        /// <summary>
        /// End-to-end test of TLS communication with various certificate configurations.
        /// Tests different combinations of:
        /// - Certificate OIDs (null, server-only, or both client and server authentication)
        /// - Certificate modes (NoCertificate, AllowCertificate, RequireCertificate)
        /// 
        /// Verifies that:
        /// - TLS connections are established successfully
        /// - Grain calls work over encrypted connections
        /// - Different authentication modes are properly enforced
        /// - Data integrity is maintained (echo test)
        /// </summary>
        [Theory]
        [InlineData(null, RemoteCertificateMode.AllowCertificate)]
        [InlineData(null, RemoteCertificateMode.NoCertificate)]
        [InlineData(new[] { TestCertificateHelper.ServerAuthenticationOid }, RemoteCertificateMode.AllowCertificate)]
        [InlineData(new[] { TestCertificateHelper.ServerAuthenticationOid }, RemoteCertificateMode.NoCertificate)]
        [InlineData(new[] { TestCertificateHelper.ClientAuthenticationOid, TestCertificateHelper.ServerAuthenticationOid }, RemoteCertificateMode.NoCertificate)]
        [InlineData(new[] { TestCertificateHelper.ClientAuthenticationOid, TestCertificateHelper.ServerAuthenticationOid }, RemoteCertificateMode.AllowCertificate)]
        [InlineData(new[] { TestCertificateHelper.ClientAuthenticationOid, TestCertificateHelper.ServerAuthenticationOid }, RemoteCertificateMode.RequireCertificate)]
        public async Task TlsEndToEnd(string[] oids, RemoteCertificateMode certificateMode)
        {
            TestCluster testCluster = default;
            try
            {
                var builder = new TestClusterBuilder()
                    .AddSiloBuilderConfigurator<TlsServerConfigurator>()
                    .AddClientBuilderConfigurator<TlsClientConfigurator>();

                // Create a self-signed certificate with specified OIDs
                var certificate = TestCertificateHelper.CreateSelfSignedCertificate(
                    CertificateSubjectName, oids);
                
                // Pass certificate through configuration (simulates real deployment)
                var encodedCertificate = TestCertificateHelper.ConvertToBase64(certificate);
                builder.Properties[CertificateConfigKey] = encodedCertificate;
                builder.Properties[ClientCertificateModeKey] = certificateMode.ToString();

                testCluster = builder.Build();
                await testCluster.DeployAsync();

                var client = testCluster.Client;

                // Test that grain calls work over TLS-encrypted connections
                var grain = client.GetGrain<IPingGrain>("pingu");
                var expected = "secret chit chat";
                var actual = await grain.Echo(expected);
                Assert.Equal(expected, actual);
            }
            finally
            {
                if (testCluster != null)
                {
                    await testCluster.StopAllSilosAsync();
                    testCluster.Dispose();
                }
            }
        }
    }

    /// <summary>
    /// Simple test grain interface for verifying TLS connections.
    /// The echo method ensures data integrity over encrypted connections.
    /// </summary>
    public interface IPingGrain : IGrainWithStringKey
    {
        Task<string> Echo(string value);
    }

    /// <summary>
    /// Test grain implementation that echoes back the input.
    /// Used to verify that data is correctly transmitted over TLS connections.
    /// </summary>
    public class PingGrain : Grain, IPingGrain
    {
        public Task<string> Echo(string value) => Task.FromResult(value);
    }
}
