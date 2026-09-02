using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Connections.Security;
using Orleans.Hosting;
using Xunit;

namespace Orleans.Connections.Security.Tests;

public class ClientHostingExtensionsTests
{
    [Fact]
    public void UseTls_NullCertificateWithConfigureAction_ThrowsArgumentNullExceptionForCertificate()
    {
        var builder = new RecordingClientBuilder();

        var exception = Assert.Throws<ArgumentNullException>(
            () => builder.UseTls((X509Certificate2)null!, static _ => { }));

        Assert.Equal("certificate", exception.ParamName);
        Assert.Equal(0, builder.ClientConnectionOptionsConfigurationCount);
    }

    [Fact]
    public void UseTls_NullCertificateWithoutConfigureAction_ThrowsArgumentNullExceptionForCertificate()
    {
        var builder = new RecordingClientBuilder();

        var exception = Assert.Throws<ArgumentNullException>(
            () => builder.UseTls((X509Certificate2)null!));

        Assert.Equal("certificate", exception.ParamName);
        Assert.Equal(0, builder.ClientConnectionOptionsConfigurationCount);
    }

    [Fact]
    public void UseTls_NullConfigureAction_ThrowsArgumentNullExceptionForConfigureOptions()
    {
        var builder = new RecordingClientBuilder();

        var exception = Assert.Throws<ArgumentNullException>(
            () => builder.UseTls((Action<TlsOptions>)null!));

        Assert.Equal("configureOptions", exception.ParamName);
        Assert.Equal(0, builder.ClientConnectionOptionsConfigurationCount);
    }

    [Fact]
    public void UseTls_CertificateOverloadWithNullConfigureAction_ThrowsArgumentNullExceptionForConfigureOptions()
    {
        using var certificate = TestCertificateHelper.CreateSelfSignedCertificate("client-null-configure.test");
        var builder = new RecordingClientBuilder();

        var exception = Assert.Throws<ArgumentNullException>(
            () => builder.UseTls(certificate, null!));

        Assert.Equal("configureOptions", exception.ParamName);
        Assert.Equal(0, builder.ClientConnectionOptionsConfigurationCount);
    }

    [Fact]
    public void UseTls_StoreOverloadWithNullConfigureAction_ThrowsBeforeAccessingCertificateStore()
    {
        var builder = new RecordingClientBuilder();

        var exception = Assert.Throws<ArgumentNullException>(
            () => builder.UseTls(
                StoreName.My,
                "unused.test",
                allowInvalid: false,
                StoreLocation.CurrentUser,
                null!));

        Assert.Equal("configureOptions", exception.ParamName);
        Assert.Equal(0, builder.ClientConnectionOptionsConfigurationCount);
    }

    [Fact]
    public void UseTls_WithoutLocalCertificate_WhenClientCertificateIsRequired_ThrowsConfigurationFailure()
    {
        var builder = new RecordingClientBuilder();

        var exception = Assert.Throws<InvalidOperationException>(
            () => builder.UseTls(options =>
                options.ClientCertificateMode = RemoteCertificateMode.RequireCertificate));

        Assert.Equal("No certificate specified", exception.Message);
        Assert.Equal(0, builder.ClientConnectionOptionsConfigurationCount);
    }

    [Fact]
    public void UseTls_WithoutLocalCertificate_DefaultClientCertificateMode_RegistersOutboundTls()
    {
        var builder = new RecordingClientBuilder();

        var result = builder.UseTls(static _ => { });

        Assert.Same(builder, result);
        Assert.Equal(1, builder.ClientConnectionOptionsConfigurationCount);
        using var provider = builder.BuildServiceProvider();
        var connectionOptions = provider.GetRequiredService<IOptions<ClientConnectionOptions>>().Value;
        var connectionBuilder = new RecordingConnectionBuilder(provider);
        connectionOptions.ApplyTo(connectionBuilder);
        Assert.Equal(1, connectionBuilder.MiddlewareRegistrationCount);
    }

    [Fact]
    public void UseTls_CertificateWithoutPrivateKey_ThrowsArgumentExceptionForCertificate()
    {
        using var certificate = TestCertificateHelper.CreateCertificateWithoutPrivateKey("client-no-private-key.test");
        var builder = new RecordingClientBuilder();

        var exception = Assert.Throws<ArgumentException>(
            () => builder.UseTls(certificate, static _ => { }));

        Assert.Equal("certificate", exception.ParamName);
        Assert.Contains("does not contain a private key", exception.Message);
        Assert.Equal(0, builder.ClientConnectionOptionsConfigurationCount);
    }

    [Fact]
    public void UseTls_ConfiguredCertificateWithoutPrivateKey_ThrowsArgumentExceptionForTlsOptionsLocalCertificate()
    {
        using var certificate = TestCertificateHelper.CreateCertificateWithoutPrivateKey("configured-no-private-key.test");
        var builder = new RecordingClientBuilder();

        var exception = Assert.Throws<ArgumentException>(
            () => builder.UseTls(options => options.LocalCertificate = certificate));

        Assert.Equal("TlsOptions.LocalCertificate", exception.ParamName);
        Assert.Contains("does not contain a private key", exception.Message);
        Assert.Equal(0, builder.ClientConnectionOptionsConfigurationCount);
    }

    [Fact]
    public void UseTls_CertificateOverload_AssignsCertificateBeforeInvokingConfigureAction()
    {
        using var certificate = TestCertificateHelper.CreateSelfSignedCertificate("client-order.test");
        var builder = new RecordingClientBuilder();
        X509Certificate2? certificateObservedByConfigureAction = null;

        var result = builder.UseTls(
            certificate,
            options => certificateObservedByConfigureAction = options.LocalCertificate);

        Assert.Same(builder, result);
        Assert.Same(certificate, certificateObservedByConfigureAction);
        Assert.Equal(1, builder.ClientConnectionOptionsConfigurationCount);
    }

    [Fact]
    public void UseTls_ConfigureActionMutations_AppearExactlyOnCapturedTlsOptions()
    {
        using var certificate = TestCertificateHelper.CreateSelfSignedCertificate("client-options.test");
        var builder = new RecordingClientBuilder();
        var expectedTimeout = TimeSpan.FromMilliseconds(1_873);
        RemoteCertificateValidator expectedValidator = static (_, _, errors) =>
            errors == System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch;
        TlsOptions? configuredOptions = null;

        builder.UseTls(
            certificate,
            options =>
            {
                configuredOptions = options;
                options.HandshakeTimeout = expectedTimeout;
                options.RemoteCertificateMode = RemoteCertificateMode.AllowCertificate;
                options.SslProtocols = SslProtocols.Tls13;
                options.RemoteCertificateValidation = expectedValidator;
            });

        var actual = Assert.IsType<TlsOptions>(configuredOptions);
        Assert.Same(certificate, actual.LocalCertificate);
        Assert.Equal(expectedTimeout, actual.HandshakeTimeout);
        Assert.Equal(RemoteCertificateMode.AllowCertificate, actual.RemoteCertificateMode);
        Assert.Equal(SslProtocols.Tls13, actual.SslProtocols);
        Assert.Same(expectedValidator, actual.RemoteCertificateValidation);
        using var provider = builder.BuildServiceProvider();
        var connectionOptions = provider.GetRequiredService<IOptions<ClientConnectionOptions>>().Value;
        var connectionBuilder = new RecordingConnectionBuilder(provider);
        connectionOptions.ApplyTo(connectionBuilder);
        Assert.Equal(1, connectionBuilder.MiddlewareRegistrationCount);
    }

    [Fact]
    public void UseTls_RegistersExactlyOneOutboundClientTlsConnectionCallback()
    {
        using var certificate = TestCertificateHelper.CreateSelfSignedCertificate("client-registration.test");
        var builder = new RecordingClientBuilder();

        builder.UseTls(certificate);

        Assert.Equal(1, builder.ClientConnectionOptionsConfigurationCount);
        using var provider = builder.BuildServiceProvider();
        var connectionOptions = provider.GetRequiredService<IOptions<ClientConnectionOptions>>().Value;
        var connectionBuilder = new RecordingConnectionBuilder(provider);
        connectionOptions.ApplyTo(connectionBuilder);
        Assert.Equal(1, connectionBuilder.MiddlewareRegistrationCount);
    }

    [Fact]
    public void UseTls_AppendsOutboundTlsWithoutReplacingExistingClientConnectionConfiguration()
    {
        using var certificate = TestCertificateHelper.CreateSelfSignedCertificate("client-composition.test");
        var builder = new RecordingClientBuilder();
        var callOrder = new List<string>();
        var existingCallbackInvocations = 0;
        builder.Configure<ClientConnectionOptions>(
            options => options.ConfigureConnection(
                _ =>
                {
                    existingCallbackInvocations++;
                    callOrder.Add("existing");
                }));

        builder.UseTls(certificate);

        Assert.Equal(2, builder.ClientConnectionOptionsConfigurationCount);
        using var provider = builder.BuildServiceProvider();
        var connectionOptions = provider.GetRequiredService<IOptions<ClientConnectionOptions>>().Value;
        var connectionBuilder = new RecordingConnectionBuilder(provider, callOrder);
        connectionOptions.ApplyTo(connectionBuilder);
        Assert.Equal(1, existingCallbackInvocations);
        Assert.Equal(1, connectionBuilder.MiddlewareRegistrationCount);
        Assert.Equal(["existing", "tls"], callOrder);
    }
}
