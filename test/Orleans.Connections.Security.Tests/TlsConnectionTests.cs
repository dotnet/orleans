using System;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Orleans;
using Orleans.Connections.Security.Tests;
using Orleans.Hosting;
using Orleans.TestingHost;
using Xunit;

namespace NetCore.Tests
{
    [Trait("Category", "BVT")]
    public class TlsConnectionTests
    {
        private const string CertificateSubjectName = "fakedomain.faketld";
        private const string CertificateConfigKey = "certificate";

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

        private class TlsConfigurator : ISiloConfigurator, IClientBuilderConfigurator
        {
            public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
            {
                clientBuilder.ConfigureApplicationParts(parts => parts.AddApplicationPart(typeof(IPingGrain).Assembly));

                var encodedCertificate = configuration[CertificateConfigKey];
                var localCertificate = TestCertificateHelper.ConvertFromBase64(encodedCertificate);
                clientBuilder.UseTls(localCertificate, options =>
                {
                    options.SslProtocols = System.Security.Authentication.SslProtocols.Tls12;
                    options.RemoteCertificateValidation = (remoteCertificate, chain, errors) =>
                    {
                        return true;
                    };

                    options.OnAuthenticateAsClient = (connection, sslOptions) =>
                    {
                        sslOptions.TargetHost = CertificateSubjectName;
                    };
                });
            }

            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder.ConfigureApplicationParts(parts => parts.AddApplicationPart(typeof(IPingGrain).Assembly));

                var config = hostBuilder.GetConfiguration();
                var encodedCertificate = config[CertificateConfigKey];
                var localCertificate = TestCertificateHelper.ConvertFromBase64(encodedCertificate);
                hostBuilder.UseTls(localCertificate, options =>
                {
                    options.SslProtocols = System.Security.Authentication.SslProtocols.Tls12;
                    options.RemoteCertificateValidation = (remoteCertificate, chain, errors) =>
                    {
                        return true;
                    };

                    options.OnAuthenticateAsClient = (connection, sslOptions) =>
                    {
                        sslOptions.TargetHost = CertificateSubjectName;
                    };
                });
            }
        }

        [Fact]
        public async Task TlsBasicEndToEnd()
        {
            TestCluster testCluster = default;
            try
            {
                var builder = new TestClusterBuilder()
                    .AddSiloBuilderConfigurator<TlsConfigurator>()
                    .AddClientBuilderConfigurator<TlsConfigurator>();

                var certificate = TestCertificateHelper.CreateSelfSignedCertificate(
                    CertificateSubjectName,
                    new[] { TestCertificateHelper.ClientAuthenticationOid, TestCertificateHelper.ServerAuthenticationOid });
                var encodedCertificate = TestCertificateHelper.ConvertToBase64(certificate);
                builder.Properties[CertificateConfigKey] = encodedCertificate;

                testCluster = builder.Build();
                await testCluster.DeployAsync();

                var client = testCluster.Client;

                var grain = client.GetGrain<IPingGrain>("pingu");
                var expected = "secret chit chat";
                var actual = await grain.Echo(expected);
                Assert.Equal(expected, actual);
            }
            finally
            {
                await testCluster?.StopAllSilosAsync();
                testCluster?.Dispose();
            }
        }
    }

    public interface IPingGrain : IGrainWithStringKey
    {
        Task<string> Echo(string value);
    }

    public class PingGrain : Grain, IPingGrain
    {
        public Task<string> Echo(string value) => Task.FromResult(value);
    }
}
