using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;
using Orleans.TestingHost;
using Xunit;

namespace Orleans.Connections.Security.Tests;

[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Security")]
public sealed class ClientConnectionAuthenticationTests
{
    private const string CertificateConfigKey = "ClientAuthenticationCertificate";
    private const string RecorderConfigKey = "ClientAuthenticationRecorder";
    private const string Token = "client-authentication-test-token";
    private const string TargetHost = "client-authentication.test";
    private static readonly ConcurrentDictionary<string, ValidationRecorder> Recorders = new();

    [Fact]
    public async Task AuthenticatedClientConnection_CanCallGrain()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var recorderId = Guid.NewGuid().ToString();
        var recorder = new ValidationRecorder();
        Assert.True(Recorders.TryAdd(recorderId, recorder));

        TestCluster? cluster = null;
        try
        {
            using var certificate = TestCertificateHelper.CreateSelfSignedCertificate(
                TargetHost,
                [TestCertificateHelper.ClientAuthenticationOid, TestCertificateHelper.ServerAuthenticationOid]);
            var builder = new TestClusterBuilder()
                .AddSiloBuilderConfigurator<AuthenticatedGatewayConfigurator>()
                .AddClientBuilderConfigurator<AuthenticatedClientConfigurator>();
            builder.Options.InitialSilosCount = 2;
            builder.Properties[CertificateConfigKey] = TestCertificateHelper.ConvertToBase64(certificate);
            builder.Properties[RecorderConfigKey] = recorderId;

            cluster = builder.Build();
            await cluster.DeployAsync(cancellationToken);

            var grain = cluster.Client.GetGrain<IPingGrain>("authenticated-client");
            Assert.Equal("authenticated", await grain.Echo("authenticated"));
            Assert.True(recorder.ValidationCount > 0);
            Assert.Equal(SiloConnectionAuthenticationTarget.Client, recorder.LastTarget);
            Assert.Equal(cluster.Options.ClusterId, recorder.LastClusterId);
        }
        finally
        {
            Recorders.TryRemove(recorderId, out _);
            if (cluster is not null)
            {
                await cluster.StopAllSilosAsync(cancellationToken);
                cluster.Dispose();
            }
        }
    }

    private sealed class AuthenticatedGatewayConfigurator : IHostConfigurator
    {
        public void Configure(IHostBuilder hostBuilder)
        {
            var configuration = hostBuilder.GetConfiguration();
            var certificate = TestCertificateHelper.ConvertFromBase64(configuration[CertificateConfigKey]!);
            var recorder = Recorders[configuration[RecorderConfigKey]!];

            hostBuilder.UseOrleans((_, siloBuilder) =>
                siloBuilder.UseAuthenticatedClientConnections(
                    tls =>
                    {
                        tls.LocalCertificate = certificate;
                        tls.RemoteCertificateMode = RemoteCertificateMode.RequireCertificate;
                        tls.AllowAnyRemoteCertificate();
                    },
                    authentication =>
                    {
                        authentication.Mode = SiloConnectionAuthenticationMode.Audit;
                        authentication.UseTokenValidator(new RecordingTokenValidator(recorder));
                    }));
        }
    }

    private sealed class AuthenticatedClientConfigurator : IClientBuilderConfigurator
    {
        public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
        {
            var certificate = TestCertificateHelper.ConvertFromBase64(configuration[CertificateConfigKey]!);
            clientBuilder.UseAuthenticatedClientConnections(
                tls =>
                {
                    tls.AllowAnyRemoteCertificate();
                    tls.ClientCertificateMode = RemoteCertificateMode.RequireCertificate;
                    tls.LocalClientCertificateSelector = (_, _, _, _, _) => certificate;
                },
                authentication =>
                {
                    authentication.Mode = SiloConnectionAuthenticationMode.Audit;
                    authentication.UseTokenProvider(new FixedTokenProvider());
                });
        }
    }

    private sealed class FixedTokenProvider : ISiloConnectionTokenProvider
    {
        public ValueTask<SiloConnectionToken> GetTokenAsync(
            SiloConnectionTokenRequestContext context,
            CancellationToken cancellationToken)
        {
            Assert.Equal(SiloConnectionAuthenticationTarget.Client, context.Target);
            return ValueTask.FromResult(
                new SiloConnectionToken(Token, DateTimeOffset.UtcNow.AddMinutes(10)));
        }
    }

    private sealed class RecordingTokenValidator(ValidationRecorder recorder) : ISiloConnectionTokenValidator
    {
        public ValueTask<SiloConnectionTokenValidationResult> ValidateTokenAsync(
            string token,
            SiloConnectionTokenValidationContext context,
            CancellationToken cancellationToken)
        {
            Assert.Equal(Token, token);
            recorder.Record(context);
            var principal = new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "test-client")], "test-token"));
            return ValueTask.FromResult(
                SiloConnectionTokenValidationResult.Success(principal, DateTimeOffset.UtcNow.AddMinutes(10)));
        }
    }

    private sealed class ValidationRecorder
    {
        private int _validationCount;

        public int ValidationCount => Volatile.Read(ref _validationCount);

        public string? LastClusterId { get; private set; }

        public SiloConnectionAuthenticationTarget LastTarget { get; private set; }

        public void Record(SiloConnectionTokenValidationContext context)
        {
            LastClusterId = context.ClusterId;
            LastTarget = context.Target;
            Interlocked.Increment(ref _validationCount);
        }
    }
}
