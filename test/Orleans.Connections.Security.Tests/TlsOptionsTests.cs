using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Connections.Transport.Security;
using Orleans.Hosting;
using TestExtensions;
using Xunit;

namespace Orleans.Connections.Security.Tests;

[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Security")]
public class TlsOptionsTests
{
    [Fact]
    public void SiloTlsConfiguration_AppliesOncePerConnectionRole()
    {
        var services = new ServiceCollection();
        var configuredOptions = new List<TlsOptions>();
        var authenticationCount = 0;
        var builder = new TestSiloBuilder(services);
        builder.UseTls(options =>
        {
            configuredOptions.Add(options);
            options.OnAuthenticateAsServer += (_, _) => authenticationCount++;
        });
        using var provider = services.BuildServiceProvider();
        var monitor = provider.GetRequiredService<IOptionsMonitor<TlsOptions>>();
        var defaultOptions = monitor.CurrentValue;
        var siloOptions = monitor.Get("silo");
        var gatewayOptions = monitor.Get("gateway");

        Assert.Equal([defaultOptions, siloOptions, gatewayOptions], configuredOptions);
        Assert.Same(defaultOptions, monitor.Get(Options.DefaultName));
        Assert.Same(siloOptions, monitor.Get("silo"));
        Assert.Same(gatewayOptions, monitor.Get("gateway"));
        Assert.Null(monitor.Get("other").OnAuthenticateAsServer);
        foreach (var options in configuredOptions)
        {
            Assert.NotNull(options.OnAuthenticateAsServer);
            options.OnAuthenticateAsServer(null!, new TlsServerAuthenticationOptions());
        }

        Assert.Equal(3, authenticationCount);
        Assert.Equal(3, configuredOptions.Count);
    }

    [Fact]
    public void CertificateModes_DefaultToRequiredPeerAndOptionalLocalClientCertificate()
    {
        var options = new TlsOptions();

        Assert.Equal(RemoteCertificateMode.RequireCertificate, options.RemoteCertificateMode);
        Assert.Equal(RemoteCertificateMode.AllowCertificate, options.ClientCertificateMode);
    }

    [Fact]
    public void HandshakeTimeout_InfiniteTimeSpan_RoundTripsAndCreatesUntimedTokenSource()
    {
        var options = new TlsOptions
        {
            HandshakeTimeout = Timeout.InfiniteTimeSpan
        };
        var copiedOptions = new TlsOptions
        {
            HandshakeTimeout = options.HandshakeTimeout
        };

        Assert.Equal(Timeout.InfiniteTimeSpan, options.HandshakeTimeout);
        Assert.Equal(Timeout.InfiniteTimeSpan, copiedOptions.HandshakeTimeout);
        using var cancellationTokenSource = options.CreateHandshakeCancellationTokenSource();
        Assert.False(cancellationTokenSource.IsCancellationRequested);
    }

    [Fact]
    public void HandshakeTimeout_MaximumSupportedFiniteValue_CreatesCancelableTokenSource()
    {
        var maximum = TimeSpan.FromMilliseconds(uint.MaxValue - 1);
        var options = new TlsOptions
        {
            HandshakeTimeout = maximum
        };

        Assert.Equal(maximum, options.HandshakeTimeout);
        using var cancellationTokenSource = options.CreateHandshakeCancellationTokenSource();
        Assert.True(cancellationTokenSource.Token.CanBeCanceled);
    }

    [Fact]
    public void HandshakeTimeout_FirstUnsupportedFiniteValue_ThrowsArgumentOutOfRangeException()
    {
        var options = new TlsOptions();
        var rejectedValue = TimeSpan.FromMilliseconds(uint.MaxValue);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => options.HandshakeTimeout = rejectedValue);

        Assert.Equal("value", exception.ParamName);
        Assert.Contains("must be positive and no greater than", exception.Message);
        Assert.Equal(TimeSpan.FromSeconds(10), options.HandshakeTimeout);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void HandshakeTimeout_NonPositiveFiniteValue_ThrowsArgumentOutOfRangeException(int ticks)
    {
        var options = new TlsOptions();

        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => options.HandshakeTimeout = TimeSpan.FromTicks(ticks));

        Assert.Equal("value", exception.ParamName);
        Assert.Contains("HandshakeTimeout must be positive", exception.Message);
        Assert.Equal(TimeSpan.FromSeconds(10), options.HandshakeTimeout);
    }

    private sealed class TestSiloBuilder(IServiceCollection services) : ISiloBuilder
    {
        public IServiceCollection Services { get; } = services;
        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
    }
}
