using System.Collections.Concurrent;
using System.Net.Security;
using System.Text;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Runtime.Messaging;
using Orleans.TestingHost;
using TestExtensions;
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
    [TestCategory("BVT")]
    [TestSuite("BVT")]
    [TestProvider("None")]
    [TestArea("Security")]
    public class TlsConnectionTests
    {
        [Fact]
        public void UseGatewayTls_ThrowsWhenConfiguredMoreThanOnce()
        {
            var builder = Host.CreateApplicationBuilder();

            builder.UseOrleans(siloBuilder =>
            {
                siloBuilder.UseGatewayTls(options => options.LocalServerCertificateSelector = static (_, _) => null!);

                var exception = Assert.Throws<InvalidOperationException>(
                    () => siloBuilder.UseGatewayTls(
                        options => options.LocalServerCertificateSelector = static (_, _) => null!));

                Assert.Equal("Gateway TLS has already been configured.", exception.Message);
            });
        }

        private const string CertificateSubjectName = "fakedomain.faketld";
        private const string CertificateConfigKey = "certificate";
        private const string ClientCertificateModeKey = "CertificateMode";
        private const string ClientCertificateSelectorKey = "ClientCertificateSelector";
        private const string ProtocolRecorderKey = "ProtocolRecorder";
        private const string AuthenticatedSiloProtocol = "orleans-auth-test";
        private const string OrleansProtocol = "Orleans1";
        private static readonly ConcurrentDictionary<string, ProtocolRecorder> ProtocolRecorders = new();

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
            using var original = TestCertificateHelper.CreateSelfSignedCertificate(
                CertificateSubjectName,
                new[] { TestCertificateHelper.ClientAuthenticationOid, TestCertificateHelper.ServerAuthenticationOid });
            var encoded = TestCertificateHelper.ConvertToBase64(original);
            using var decoded = TestCertificateHelper.ConvertFromBase64(encoded);
            Assert.Equal(original, decoded);
        }

        [Fact]
        public void RequiredClientCertificateSelector_RejectsNullCertificate()
        {
            Assert.Throws<InvalidOperationException>(
                () => TlsClientConnectionMiddleware.ValidateSelectedCertificate(
                    certificate: null,
                    RemoteCertificateMode.RequireCertificate));
        }

        [Fact]
        public void RequiredClientCertificateSelector_RejectsCertificateWithoutClientAuthenticationEku()
        {
            using var certificate = TestCertificateHelper.CreateSelfSignedCertificate(
                CertificateSubjectName,
                [TestCertificateHelper.ServerAuthenticationOid]);

            Assert.Throws<InvalidOperationException>(
                () => TlsClientConnectionMiddleware.ValidateSelectedCertificate(
                    certificate,
                    RemoteCertificateMode.RequireCertificate));
        }

        [Fact]
        public void RequiredClientCertificateSelector_RejectsCertificateWithoutPrivateKey()
        {
            using var certificate = TestCertificateHelper.CreateSelfSignedCertificate(
                CertificateSubjectName,
                [TestCertificateHelper.ClientAuthenticationOid]);
#if NET9_0_OR_GREATER
            using var publicCertificate =
                System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificate(certificate.RawData);
#else
#pragma warning disable SYSLIB0057
            using var publicCertificate =
                new System.Security.Cryptography.X509Certificates.X509Certificate2(certificate.RawData);
#pragma warning restore SYSLIB0057
#endif

            Assert.Throws<InvalidOperationException>(
                () => TlsClientConnectionMiddleware.ValidateSelectedCertificate(
                    publicCertificate,
                    RemoteCertificateMode.RequireCertificate));
        }

        [Fact]
        public void OptionalClientCertificateWithoutPrivateKey_IsIgnored()
        {
            using var certificate = TestCertificateHelper.CreateSelfSignedCertificate(
                CertificateSubjectName,
                [TestCertificateHelper.ClientAuthenticationOid]);
#if NET9_0_OR_GREATER
            using var publicCertificate =
                System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificate(certificate.RawData);
#else
#pragma warning disable SYSLIB0057
            using var publicCertificate =
                new System.Security.Cryptography.X509Certificates.X509Certificate2(certificate.RawData);
#pragma warning restore SYSLIB0057
#endif

            Assert.Null(TlsClientConnectionMiddleware.ValidateCertificate(
                publicCertificate,
                RemoteCertificateMode.AllowCertificate));
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
                var localCertificate = TestCertificateHelper.ConvertFromBase64(encodedCertificate!);

                var certificateModeString = configuration[ClientCertificateModeKey];
                var certificateMode = (RemoteCertificateMode)Enum.Parse(typeof(RemoteCertificateMode), certificateModeString!);
                var useCertificateSelector = bool.Parse(configuration[ClientCertificateSelectorKey]!);

                clientBuilder.UseTls(options =>
                {
                    // Use TLS 1.2 for secure communication
                    options.SslProtocols = System.Security.Authentication.SslProtocols.Tls12;
                    // Allow any certificate for testing (in production, validate properly)
                    options.AllowAnyRemoteCertificate();
                    // Client's certificate for mutual TLS
                    if (useCertificateSelector)
                    {
                        options.LocalClientCertificateSelector = (_, _, _, _, _) => localCertificate;
                    }
                    else
                    {
                        options.LocalCertificate = localCertificate;
                    }
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
                var localCertificate = TestCertificateHelper.ConvertFromBase64(encodedCertificate!);

                var certificateModeString = config[ClientCertificateModeKey];
                var certificateMode = (RemoteCertificateMode)Enum.Parse(typeof(RemoteCertificateMode), certificateModeString!);

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
        [InlineData(null, RemoteCertificateMode.AllowCertificate, false)]
        [InlineData(null, RemoteCertificateMode.NoCertificate, false)]
        [InlineData(new[] { TestCertificateHelper.ServerAuthenticationOid }, RemoteCertificateMode.AllowCertificate, false)]
        [InlineData(new[] { TestCertificateHelper.ServerAuthenticationOid }, RemoteCertificateMode.NoCertificate, false)]
        [InlineData(new[] { TestCertificateHelper.ClientAuthenticationOid, TestCertificateHelper.ServerAuthenticationOid }, RemoteCertificateMode.NoCertificate, false)]
        [InlineData(new[] { TestCertificateHelper.ClientAuthenticationOid, TestCertificateHelper.ServerAuthenticationOid }, RemoteCertificateMode.AllowCertificate, false)]
        [InlineData(new[] { TestCertificateHelper.ClientAuthenticationOid, TestCertificateHelper.ServerAuthenticationOid }, RemoteCertificateMode.RequireCertificate, false)]
        [InlineData(new[] { TestCertificateHelper.ClientAuthenticationOid, TestCertificateHelper.ServerAuthenticationOid }, RemoteCertificateMode.RequireCertificate, true)]
        public async Task TlsEndToEnd(
            string[]? oids,
            RemoteCertificateMode certificateMode,
            bool useCertificateSelector)
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            TestCluster? testCluster = default;
            try
            {
                var builder = new TestClusterBuilder()
                    .AddSiloBuilderConfigurator<TlsServerConfigurator>()
                    .AddClientBuilderConfigurator<TlsClientConfigurator>();

                // Create a self-signed certificate with specified OIDs
                using var certificate = TestCertificateHelper.CreateSelfSignedCertificate(
                    CertificateSubjectName, oids);

                // Pass certificate through configuration (simulates real deployment)
                var encodedCertificate = TestCertificateHelper.ConvertToBase64(certificate);
                builder.Properties[CertificateConfigKey] = encodedCertificate;
                builder.Properties[ClientCertificateModeKey] = certificateMode.ToString();
                builder.Properties[ClientCertificateSelectorKey] = useCertificateSelector.ToString();

                testCluster = builder.Build();
                await testCluster.DeployAsync(cancellationToken);

                var client = testCluster.Client;

                // Test that grain calls work over TLS-encrypted connections
                var grain = client!.GetGrain<IPingGrain>("pingu"); // DeployAsync initializes the client.
                var expected = "secret chit chat";
                var actual = await grain.Echo(expected);
                Assert.Equal(expected, actual);
            }
            finally
            {
                if (testCluster != null)
                {
                    try
                    {
                        await testCluster.StopAllSilosAsync(cancellationToken);
                    }
                    finally
                    {
                        testCluster.Dispose();
                    }
                }
            }
        }

        [Fact]
        public async Task SeparateSiloAndGatewayTls_NegotiateConfiguredApplicationProtocols()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var recorderId = Guid.NewGuid().ToString();
            var recorder = new ProtocolRecorder();
            Assert.True(ProtocolRecorders.TryAdd(recorderId, recorder));

            TestCluster? testCluster = default;
            try
            {
                using var certificate = TestCertificateHelper.CreateSelfSignedCertificate(
                    CertificateSubjectName,
                    [TestCertificateHelper.ClientAuthenticationOid, TestCertificateHelper.ServerAuthenticationOid]);
                var builder = new TestClusterBuilder()
                    .AddSiloBuilderConfigurator<AlpnTlsServerConfigurator>()
                    .AddClientBuilderConfigurator<AlpnTlsClientConfigurator>();
                builder.Options.InitialSilosCount = 2;
                builder.Properties[CertificateConfigKey] = TestCertificateHelper.ConvertToBase64(certificate);
                builder.Properties[ProtocolRecorderKey] = recorderId;

                testCluster = builder.Build();
                await testCluster.DeployAsync(cancellationToken);

                var grain = testCluster.Client.GetGrain<IPingGrain>("alpn");
                Assert.Equal("ping", await grain.Echo("ping"));

                Assert.Contains(AuthenticatedSiloProtocol, recorder.GetProtocols(ConnectionPath.SiloInbound));
                Assert.Contains(AuthenticatedSiloProtocol, recorder.GetProtocols(ConnectionPath.SiloOutbound));
                Assert.Contains(OrleansProtocol, recorder.GetProtocols(ConnectionPath.GatewayInbound));
                Assert.Contains(OrleansProtocol, recorder.GetProtocols(ConnectionPath.ClientOutbound));
                Assert.DoesNotContain(AuthenticatedSiloProtocol, recorder.GetProtocols(ConnectionPath.GatewayInbound));
                Assert.DoesNotContain(AuthenticatedSiloProtocol, recorder.GetProtocols(ConnectionPath.ClientOutbound));
                Assert.Contains(
                    System.Security.Cryptography.X509Certificates.X509RevocationMode.Online,
                    recorder.GetRevocationModes(ConnectionPath.SiloOutbound));
                Assert.Contains(
                    System.Security.Cryptography.X509Certificates.X509RevocationMode.Online,
                    recorder.GetRevocationModes(ConnectionPath.ClientOutbound));
            }
            finally
            {
                ProtocolRecorders.TryRemove(recorderId, out _);
                if (testCluster is not null)
                {
                    await testCluster.StopAllSilosAsync(cancellationToken);
                    testCluster.Dispose();
                }
            }
        }

        private sealed class AlpnTlsServerConfigurator : IHostConfigurator
        {
            public void Configure(IHostBuilder hostBuilder)
            {
                var configuration = hostBuilder.GetConfiguration();
                var certificate = TestCertificateHelper.ConvertFromBase64(configuration[CertificateConfigKey]!);
                var recorder = ProtocolRecorders[configuration[ProtocolRecorderKey]!];

                hostBuilder.UseOrleans((_, siloBuilder) =>
                {
                    siloBuilder.UseSiloTls(options =>
                    {
                        ConfigureTls(options, certificate);
                        options.OnAuthenticateAsClient = (_, authenticationOptions) =>
                        {
                            recorder.RecordRevocationMode(
                                ConnectionPath.SiloOutbound,
                                authenticationOptions.CertificateRevocationCheckMode);
                            authenticationOptions.CertificateRevocationCheckMode =
                                System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck;
                            authenticationOptions.TargetHost = CertificateSubjectName;
                            authenticationOptions.ApplicationProtocols =
                            [
                                new SslApplicationProtocol(AuthenticatedSiloProtocol),
                                new SslApplicationProtocol(OrleansProtocol)
                            ];
                        };
                        options.OnAuthenticateAsServer = (_, authenticationOptions) =>
                        {
                            authenticationOptions.ApplicationProtocols =
                            [
                                new SslApplicationProtocol(AuthenticatedSiloProtocol),
                                new SslApplicationProtocol(OrleansProtocol)
                            ];
                        };
                    });

                    siloBuilder.UseGatewayTls(options =>
                    {
                        ConfigureTls(options, certificate);
                        options.RemoteCertificateMode = RemoteCertificateMode.NoCertificate;
                    });

                    siloBuilder.Configure<SiloConnectionOptions>(options =>
                    {
                        options.ConfigureSiloInboundConnection(
                            builder => builder.UseMiddleware(new ProtocolRecordingMiddleware(recorder, ConnectionPath.SiloInbound)));
                        options.ConfigureSiloOutboundConnection(
                            builder => builder.UseMiddleware(new ProtocolRecordingMiddleware(recorder, ConnectionPath.SiloOutbound)));
                        options.ConfigureGatewayInboundConnection(
                            builder => builder.UseMiddleware(new ProtocolRecordingMiddleware(recorder, ConnectionPath.GatewayInbound)));
                    });
                });
            }
        }

        private sealed class AlpnTlsClientConfigurator : IClientBuilderConfigurator
        {
            public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
            {
                var recorder = ProtocolRecorders[configuration[ProtocolRecorderKey]!];
                clientBuilder.UseTls(options =>
                {
                    options.AllowAnyRemoteCertificate();
                    options.CheckCertificateRevocation = true;
                    options.OnAuthenticateAsClient = (_, authenticationOptions) =>
                    {
                        recorder.RecordRevocationMode(
                            ConnectionPath.ClientOutbound,
                            authenticationOptions.CertificateRevocationCheckMode);
                        authenticationOptions.CertificateRevocationCheckMode =
                            System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck;
                        authenticationOptions.TargetHost = CertificateSubjectName;
                    };
                });

                clientBuilder.Configure<ClientConnectionOptions>(options =>
                    options.ConfigureConnection(
                        builder => builder.UseMiddleware(new ProtocolRecordingMiddleware(recorder, ConnectionPath.ClientOutbound))));
            }
        }

        private static void ConfigureTls(TlsOptions options, System.Security.Cryptography.X509Certificates.X509Certificate2 certificate)
        {
            options.LocalCertificate = certificate;
            options.SslProtocols = System.Security.Authentication.SslProtocols.Tls12;
            options.AllowAnyRemoteCertificate();
            options.RemoteCertificateMode = RemoteCertificateMode.AllowCertificate;
            options.CheckCertificateRevocation = true;
        }

        private sealed class ProtocolRecordingMiddleware(ProtocolRecorder recorder, ConnectionPath path) : IConnectionMiddleware
        {
            public async Task OnConnectionAsync(ConnectionContext context, ConnectionDelegate next)
            {
                var feature = context.Features.Get<ITlsApplicationProtocolFeature>();
                recorder.Record(path, feature is null ? null : Encoding.ASCII.GetString(feature.ApplicationProtocol.Span));
                await next(context);
            }
        }

        private sealed class ProtocolRecorder
        {
            private readonly ConcurrentDictionary<ConnectionPath, ConcurrentBag<string>> _protocols = new();
            private readonly ConcurrentDictionary<
                ConnectionPath,
                ConcurrentBag<System.Security.Cryptography.X509Certificates.X509RevocationMode>> _revocationModes = new();

            public void Record(ConnectionPath path, string? protocol)
            {
                _protocols.GetOrAdd(path, static _ => []).Add(protocol ?? "<none>");
            }

            public string[] GetProtocols(ConnectionPath path)
            {
                return _protocols.TryGetValue(path, out var protocols) ? protocols.ToArray() : [];
            }

            public void RecordRevocationMode(
                ConnectionPath path,
                System.Security.Cryptography.X509Certificates.X509RevocationMode mode)
            {
                _revocationModes.GetOrAdd(path, static _ => []).Add(mode);
            }

            public System.Security.Cryptography.X509Certificates.X509RevocationMode[] GetRevocationModes(
                ConnectionPath path)
            {
                return _revocationModes.TryGetValue(path, out var modes) ? modes.ToArray() : [];
            }
        }

        private enum ConnectionPath
        {
            SiloInbound,
            SiloOutbound,
            GatewayInbound,
            ClientOutbound
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
