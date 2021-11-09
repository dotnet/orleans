using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Orleans;
using Orleans.Hosting;
using Orleans.TestingHost;
using Xunit;

namespace Orleans.Connections.Security.Tests
{
    [Trait("Category", "BVT")]
    public class TlsConnectionTests
    {
        private const string CertificateSubjectName = "fakedomain.faketld";
        private const string CertificateConfigKey = "certificate";
        private const string ClientCertificateModeKey = "CertificateMode";

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
                    options.SslProtocols = System.Security.Authentication.SslProtocols.Tls12;
                    options.AllowAnyRemoteCertificate();
                    options.LocalCertificate = localCertificate;
                    options.RemoteCertificateMode = RemoteCertificateMode.RequireCertificate;
                    options.ClientCertificateMode = certificateMode;
                    options.OnAuthenticateAsClient = (connection, sslOptions) =>
                    {
                        sslOptions.TargetHost = CertificateSubjectName;
                    };
                });
            }
        }

        private class TlsServerConfigurator : IHostConfigurator
        {
            public void Configure(IHostBuilder hostBuilder)
            {
                var config = hostBuilder.GetConfiguration();
                var encodedCertificate = config[CertificateConfigKey];
                var localCertificate = TestCertificateHelper.ConvertFromBase64(encodedCertificate);

                var certificateModeString = config[ClientCertificateModeKey];
                var certificateMode = (RemoteCertificateMode)Enum.Parse(typeof(RemoteCertificateMode), certificateModeString);

                hostBuilder.UseOrleans(siloBuilder =>
                {
                    siloBuilder.UseTls(localCertificate, options =>
                    {
                        options.SslProtocols = System.Security.Authentication.SslProtocols.Tls12;
                        options.AllowAnyRemoteCertificate();
                        options.RemoteCertificateMode = RemoteCertificateMode.AllowCertificate;
                        options.ClientCertificateMode = certificateMode;
                        options.OnAuthenticateAsClient = (connection, sslOptions) =>
                        {
                            sslOptions.TargetHost = CertificateSubjectName;
                        };
                    });
                });
            }
        }

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

                var certificate = TestCertificateHelper.CreateSelfSignedCertificate(
                    CertificateSubjectName, oids);
                
                var encodedCertificate = TestCertificateHelper.ConvertToBase64(certificate);
                builder.Properties[CertificateConfigKey] = encodedCertificate;
                builder.Properties[ClientCertificateModeKey] = certificateMode.ToString();

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
                if (testCluster != null)
                {
                    await testCluster.StopAllSilosAsync();
                    testCluster.Dispose();
                }
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
