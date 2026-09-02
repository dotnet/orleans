using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Connections.Security;
using Orleans.Hosting;
using Xunit;

namespace Orleans.Connections.Security.Tests;

public class SiloHostingExtensionsTests
{
    [Fact]
    public void UseTls_NullCertificateWithConfigureAction_ThrowsArgumentNullExceptionForCertificate()
    {
        var builder = new RecordingSiloBuilder();

        var exception = Assert.Throws<ArgumentNullException>(
            () => builder.UseTls((X509Certificate2)null!, static _ => { }));

        Assert.Equal("certificate", exception.ParamName);
        Assert.Equal(0, builder.SiloConnectionOptionsConfigurationCount);
    }

    [Fact]
    public void UseTls_NullCertificateWithoutConfigureAction_ThrowsArgumentNullExceptionForCertificate()
    {
        var builder = new RecordingSiloBuilder();

        var exception = Assert.Throws<ArgumentNullException>(
            () => builder.UseTls((X509Certificate2)null!));

        Assert.Equal("certificate", exception.ParamName);
        Assert.Equal(0, builder.SiloConnectionOptionsConfigurationCount);
    }

    [Fact]
    public void UseTls_NullConfigureAction_ThrowsArgumentNullExceptionForConfigureOptions()
    {
        var builder = new RecordingSiloBuilder();

        var exception = Assert.Throws<ArgumentNullException>(
            () => builder.UseTls((Action<TlsOptions>)null!));

        Assert.Equal("configureOptions", exception.ParamName);
        Assert.Equal(0, builder.SiloConnectionOptionsConfigurationCount);
    }

    [Fact]
    public void UseTls_CertificateOverloadWithNullConfigureAction_ThrowsArgumentNullExceptionForConfigureOptions()
    {
        using var certificate = TestCertificateHelper.CreateSelfSignedCertificate("silo-null-configure.test");
        var builder = new RecordingSiloBuilder();

        var exception = Assert.Throws<ArgumentNullException>(
            () => builder.UseTls(certificate, null!));

        Assert.Equal("configureOptions", exception.ParamName);
        Assert.Equal(0, builder.SiloConnectionOptionsConfigurationCount);
    }

    [Fact]
    public void UseTls_StoreOverloadWithNullConfigureAction_ThrowsBeforeAccessingCertificateStore()
    {
        var builder = new RecordingSiloBuilder();

        var exception = Assert.Throws<ArgumentNullException>(
            () => builder.UseTls(
                StoreName.My,
                "unused.test",
                allowInvalid: false,
                StoreLocation.CurrentUser,
                null!));

        Assert.Equal("configureOptions", exception.ParamName);
        Assert.Equal(0, builder.SiloConnectionOptionsConfigurationCount);
    }

    [Fact]
    public void UseTls_WithoutLocalCertificateOrSelector_ThrowsConfigurationFailure()
    {
        var builder = new RecordingSiloBuilder();

        var exception = Assert.Throws<InvalidOperationException>(
            () => builder.UseTls(static _ => { }));

        Assert.Equal("No certificate specified", exception.Message);
        Assert.Equal(0, builder.SiloConnectionOptionsConfigurationCount);
    }

    [Fact]
    public void UseTls_CertificateWithoutPrivateKey_ThrowsArgumentExceptionForCertificate()
    {
        using var certificate = TestCertificateHelper.CreateCertificateWithoutPrivateKey("silo-no-private-key.test");
        var builder = new RecordingSiloBuilder();

        var exception = Assert.Throws<ArgumentException>(
            () => builder.UseTls(certificate, static _ => { }));

        Assert.Equal("certificate", exception.ParamName);
        Assert.Contains("does not contain a private key", exception.Message);
        Assert.Equal(0, builder.SiloConnectionOptionsConfigurationCount);
    }

    [Fact]
    public void UseTls_ConfiguredCertificateWithoutPrivateKey_ThrowsArgumentExceptionForTlsOptionsLocalCertificate()
    {
        using var certificate = TestCertificateHelper.CreateCertificateWithoutPrivateKey("silo-configured-no-private-key.test");
        var builder = new RecordingSiloBuilder();

        var exception = Assert.Throws<ArgumentException>(
            () => builder.UseTls(options => options.LocalCertificate = certificate));

        Assert.Equal("TlsOptions.LocalCertificate", exception.ParamName);
        Assert.Contains("does not contain a private key", exception.Message);
        Assert.Equal(0, builder.SiloConnectionOptionsConfigurationCount);
    }

    [Fact]
    public void UseTls_ServerCertificateSelectorWithoutLocalCertificate_IsAccepted()
    {
        using var certificate = TestCertificateHelper.CreateSelfSignedCertificate("silo-selector.test");
        var builder = new RecordingSiloBuilder();
        Func<ConnectionContext, string?, X509Certificate2> expectedSelector = (_, _) => certificate;
        TlsOptions? configuredOptions = null;

        var result = builder.UseTls(
            options =>
            {
                configuredOptions = options;
                options.LocalServerCertificateSelector = expectedSelector;
            });

        Assert.Same(builder, result);
        var actual = Assert.IsType<TlsOptions>(configuredOptions);
        Assert.Null(actual.LocalCertificate);
        Assert.Same(expectedSelector, actual.LocalServerCertificateSelector);
        Assert.Equal(1, builder.SiloConnectionOptionsConfigurationCount);
        using var provider = builder.BuildServiceProvider();
        Assert.IsType<SiloConnectionOptions>(
            provider.GetRequiredService<IOptions<SiloConnectionOptions>>().Value);
    }

    [Fact]
    public void UseTls_CertificateOverload_AssignsCertificateBeforeInvokingConfigureAction()
    {
        using var certificate = TestCertificateHelper.CreateSelfSignedCertificate("silo-order.test");
        var builder = new RecordingSiloBuilder();
        X509Certificate2? certificateObservedByConfigureAction = null;

        var result = builder.UseTls(
            certificate,
            options => certificateObservedByConfigureAction = options.LocalCertificate);

        Assert.Same(builder, result);
        Assert.Same(certificate, certificateObservedByConfigureAction);
        Assert.Equal(1, builder.SiloConnectionOptionsConfigurationCount);
    }

    [Fact]
    public void UseTls_ConfigureActionMutations_AppearExactlyOnCapturedTlsOptionsAndResolvedConnectionOptions()
    {
        using var certificate = TestCertificateHelper.CreateSelfSignedCertificate("silo-options.test");
        var builder = new RecordingSiloBuilder();
        var expectedTimeout = TimeSpan.FromMilliseconds(2_137);
        Func<ConnectionContext, string?, X509Certificate2> expectedSelector = (_, _) => certificate;
        RemoteCertificateValidator expectedValidator = static (_, _, errors) =>
            errors == System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors;
        TlsOptions? configuredOptions = null;

        builder.UseTls(
            certificate,
            options =>
            {
                configuredOptions = options;
                options.HandshakeTimeout = expectedTimeout;
                options.RemoteCertificateMode = RemoteCertificateMode.RequireCertificate;
                options.SslProtocols = SslProtocols.Tls12;
                options.LocalServerCertificateSelector = expectedSelector;
                options.RemoteCertificateValidation = expectedValidator;
            });

        var actual = Assert.IsType<TlsOptions>(configuredOptions);
        Assert.Same(certificate, actual.LocalCertificate);
        Assert.Equal(expectedTimeout, actual.HandshakeTimeout);
        Assert.Equal(RemoteCertificateMode.RequireCertificate, actual.RemoteCertificateMode);
        Assert.Equal(SslProtocols.Tls12, actual.SslProtocols);
        Assert.Same(expectedSelector, actual.LocalServerCertificateSelector);
        Assert.Same(expectedValidator, actual.RemoteCertificateValidation);
        using var provider = builder.BuildServiceProvider();
        var connectionOptions = provider.GetRequiredService<IOptions<SiloConnectionOptions>>().Value;
        var gatewayBuilder = new RecordingConnectionBuilder(provider);
        var siloInboundBuilder = new RecordingConnectionBuilder(provider);
        var siloOutboundBuilder = new RecordingConnectionBuilder(provider);
        connectionOptions.ApplyGatewayInboundTo(gatewayBuilder);
        connectionOptions.ApplySiloInboundTo(siloInboundBuilder);
        connectionOptions.ApplySiloOutboundTo(siloOutboundBuilder);
        Assert.Equal(1, gatewayBuilder.MiddlewareRegistrationCount);
        Assert.Equal(1, siloInboundBuilder.MiddlewareRegistrationCount);
        Assert.Equal(1, siloOutboundBuilder.MiddlewareRegistrationCount);
    }

    [Fact]
    public void UseTls_RegistersExactlyOneGatewayInboundServerTlsConnectionCallback()
    {
        using var certificate = TestCertificateHelper.CreateSelfSignedCertificate("silo-gateway-registration.test");
        var builder = new RecordingSiloBuilder();

        builder.UseTls(certificate);

        Assert.Equal(1, builder.SiloConnectionOptionsConfigurationCount);
        using var provider = builder.BuildServiceProvider();
        var connectionOptions = provider.GetRequiredService<IOptions<SiloConnectionOptions>>().Value;
        var connectionBuilder = new RecordingConnectionBuilder(provider);
        connectionOptions.ApplyGatewayInboundTo(connectionBuilder);
        Assert.Equal(1, connectionBuilder.MiddlewareRegistrationCount);
    }

    [Fact]
    public void UseTls_RegistersExactlyOneSiloInboundServerTlsConnectionCallback()
    {
        using var certificate = TestCertificateHelper.CreateSelfSignedCertificate("silo-inbound-registration.test");
        var builder = new RecordingSiloBuilder();

        builder.UseTls(certificate);

        Assert.Equal(1, builder.SiloConnectionOptionsConfigurationCount);
        using var provider = builder.BuildServiceProvider();
        var connectionOptions = provider.GetRequiredService<IOptions<SiloConnectionOptions>>().Value;
        var connectionBuilder = new RecordingConnectionBuilder(provider);
        connectionOptions.ApplySiloInboundTo(connectionBuilder);
        Assert.Equal(1, connectionBuilder.MiddlewareRegistrationCount);
    }

    [Fact]
    public void UseTls_RegistersExactlyOneSiloOutboundClientTlsConnectionCallback()
    {
        using var certificate = TestCertificateHelper.CreateSelfSignedCertificate("silo-outbound-registration.test");
        var builder = new RecordingSiloBuilder();

        builder.UseTls(certificate);

        Assert.Equal(1, builder.SiloConnectionOptionsConfigurationCount);
        using var provider = builder.BuildServiceProvider();
        var connectionOptions = provider.GetRequiredService<IOptions<SiloConnectionOptions>>().Value;
        var connectionBuilder = new RecordingConnectionBuilder(provider);
        connectionOptions.ApplySiloOutboundTo(connectionBuilder);
        Assert.Equal(1, connectionBuilder.MiddlewareRegistrationCount);
    }

    [Fact]
    public void UseTls_AppendsAllTlsCallbacksWithoutReplacingExistingSiloConnectionConfiguration()
    {
        using var certificate = TestCertificateHelper.CreateSelfSignedCertificate("silo-composition.test");
        var builder = new RecordingSiloBuilder();
        var gatewayOrder = new List<string>();
        var siloInboundOrder = new List<string>();
        var siloOutboundOrder = new List<string>();
        var gatewayInvocations = 0;
        var siloInboundInvocations = 0;
        var siloOutboundInvocations = 0;
        builder.Configure<SiloConnectionOptions>(
            options =>
            {
                options.ConfigureGatewayInboundConnection(
                    _ =>
                    {
                        gatewayInvocations++;
                        gatewayOrder.Add("existing");
                    });
                options.ConfigureSiloInboundConnection(
                    _ =>
                    {
                        siloInboundInvocations++;
                        siloInboundOrder.Add("existing");
                    });
                options.ConfigureSiloOutboundConnection(
                    _ =>
                    {
                        siloOutboundInvocations++;
                        siloOutboundOrder.Add("existing");
                    });
            });

        builder.UseTls(certificate);

        Assert.Equal(2, builder.SiloConnectionOptionsConfigurationCount);
        using var provider = builder.BuildServiceProvider();
        var connectionOptions = provider.GetRequiredService<IOptions<SiloConnectionOptions>>().Value;
        var gatewayBuilder = new RecordingConnectionBuilder(provider, gatewayOrder);
        var siloInboundBuilder = new RecordingConnectionBuilder(provider, siloInboundOrder);
        var siloOutboundBuilder = new RecordingConnectionBuilder(provider, siloOutboundOrder);
        connectionOptions.ApplyGatewayInboundTo(gatewayBuilder);
        connectionOptions.ApplySiloInboundTo(siloInboundBuilder);
        connectionOptions.ApplySiloOutboundTo(siloOutboundBuilder);
        Assert.Equal(1, gatewayInvocations);
        Assert.Equal(1, siloInboundInvocations);
        Assert.Equal(1, siloOutboundInvocations);
        Assert.Equal(1, gatewayBuilder.MiddlewareRegistrationCount);
        Assert.Equal(1, siloInboundBuilder.MiddlewareRegistrationCount);
        Assert.Equal(1, siloOutboundBuilder.MiddlewareRegistrationCount);
        Assert.Equal(["existing", "tls"], gatewayOrder);
        Assert.Equal(["existing", "tls"], siloInboundOrder);
        Assert.Equal(["existing", "tls"], siloOutboundOrder);
    }

    [Fact]
    public void UseTls_CertificateWithoutPrivateKeyAndWithoutConfigureAction_ThrowsArgumentExceptionForCertificate()
    {
        using var certificate = TestCertificateHelper.CreateCertificateWithoutPrivateKey("silo-no-private-key-no-configure.test");
        var builder = new RecordingSiloBuilder();

        var exception = Assert.Throws<ArgumentException>(
            () => builder.UseTls(certificate));

        Assert.Equal("certificate", exception.ParamName);
        Assert.Contains("does not contain a private key", exception.Message);
        Assert.Equal(0, builder.SiloConnectionOptionsConfigurationCount);
    }
}
