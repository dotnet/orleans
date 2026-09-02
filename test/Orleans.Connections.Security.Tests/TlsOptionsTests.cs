using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Connections;
using Orleans.Connections.Security;
using Xunit;

namespace Orleans.Connections.Security.Tests;

public class TlsOptionsTests
{
    [Fact]
    public void Constructor_SetsEveryPublicDefaultToItsDocumentedValue()
    {
        var options = new TlsOptions();

        Assert.Equal(TimeSpan.FromSeconds(10), options.HandshakeTimeout);
        Assert.Null(options.LocalCertificate);
        Assert.Null(options.LocalServerCertificateSelector);
        Assert.Null(options.LocalClientCertificateSelector);
        Assert.Equal(RemoteCertificateMode.RequireCertificate, options.RemoteCertificateMode);
        Assert.Equal(RemoteCertificateMode.AllowCertificate, options.ClientCertificateMode);
        Assert.Null(options.RemoteCertificateValidation);
        Assert.Equal(SslProtocols.Tls12 | SslProtocols.Tls13, options.SslProtocols);
        Assert.False(options.CheckCertificateRevocation);
        Assert.Null(options.OnAuthenticateAsServer);
        Assert.Null(options.OnAuthenticateAsClient);
    }

    [Fact]
    public void HandshakeTimeout_PositiveValue_IsStoredExactly()
    {
        var options = new TlsOptions
        {
            HandshakeTimeout = TimeSpan.FromMilliseconds(1_237)
        };

        Assert.Equal(TimeSpan.FromMilliseconds(1_237), options.HandshakeTimeout);
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
        Assert.Null(exception.ActualValue);
        Assert.Contains("must be positive and no greater than", exception.Message);
        Assert.Equal(TimeSpan.FromSeconds(10), options.HandshakeTimeout);
    }

    [Fact]
    public void HandshakeTimeout_Zero_ThrowsArgumentOutOfRangeException()
    {
        var options = new TlsOptions();

        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => options.HandshakeTimeout = TimeSpan.Zero);

        Assert.Equal("value", exception.ParamName);
        Assert.Null(exception.ActualValue);
        Assert.Contains("HandshakeTimeout must be positive", exception.Message);
        Assert.Equal(TimeSpan.FromSeconds(10), options.HandshakeTimeout);
    }

    [Fact]
    public void HandshakeTimeout_NegativeFiniteValue_ThrowsArgumentOutOfRangeException()
    {
        var options = new TlsOptions();
        var rejectedValue = TimeSpan.FromTicks(-2);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => options.HandshakeTimeout = rejectedValue);

        Assert.Equal("value", exception.ParamName);
        Assert.Null(exception.ActualValue);
        Assert.Contains("HandshakeTimeout must be positive", exception.Message);
        Assert.Equal(TimeSpan.FromSeconds(10), options.HandshakeTimeout);
    }

    [Fact]
    public void AllowAnyRemoteCertificate_ReplacesExistingValidatorAndAcceptsEverySslPolicyError()
    {
        using var certificate = TestCertificateHelper.CreateSelfSignedCertificate("tls-options.test");
        RemoteCertificateValidator rejectingValidator = static (_, _, _) => false;
        var options = new TlsOptions
        {
            RemoteCertificateValidation = rejectingValidator
        };

        options.AllowAnyRemoteCertificate();

        var validator = Assert.IsType<RemoteCertificateValidator>(options.RemoteCertificateValidation);
        Assert.NotSame(rejectingValidator, validator);
        Assert.True(validator(certificate, null, SslPolicyErrors.None));
        Assert.True(validator(certificate, null, SslPolicyErrors.RemoteCertificateNotAvailable));
        Assert.True(validator(certificate, null, SslPolicyErrors.RemoteCertificateNameMismatch));
        Assert.True(validator(certificate, null, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.True(
            validator(
                certificate,
                null,
                SslPolicyErrors.RemoteCertificateNotAvailable
                    | SslPolicyErrors.RemoteCertificateNameMismatch
                    | SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public void ClientAuthenticationConfiguration_PropagatesMutationsToSslClientAuthenticationOptions()
    {
        var connection = new DefaultConnectionContext();
        var authenticationOptions = new TlsClientAuthenticationOptions();
        var underlyingOptions = Assert.IsType<SslClientAuthenticationOptions>(
            authenticationOptions.SslClientAuthenticationOptions);
        ConnectionContext? receivedConnection = null;
        TlsClientAuthenticationOptions? receivedOptions = null;
        Action<ConnectionContext, TlsClientAuthenticationOptions> configure = (context, options) =>
        {
            receivedConnection = context;
            receivedOptions = options;
            options.TargetHost = "silo.tls-options.test";
            options.EnabledSslProtocols = SslProtocols.Tls12;
            options.CertificateRevocationCheckMode = X509RevocationMode.Online;
        };
        var options = new TlsOptions
        {
            OnAuthenticateAsClient = configure
        };

        options.OnAuthenticateAsClient(connection, authenticationOptions);

        Assert.Same(configure, options.OnAuthenticateAsClient);
        Assert.Same(connection, receivedConnection);
        Assert.Same(authenticationOptions, receivedOptions);
        Assert.Same(underlyingOptions, authenticationOptions.SslClientAuthenticationOptions);
        Assert.Equal("silo.tls-options.test", underlyingOptions.TargetHost);
        Assert.Equal(SslProtocols.Tls12, underlyingOptions.EnabledSslProtocols);
        Assert.Equal(X509RevocationMode.Online, underlyingOptions.CertificateRevocationCheckMode);
    }

    [Fact]
    public void ServerAuthenticationConfiguration_PropagatesMutationsToSslServerAuthenticationOptions()
    {
        var connection = new DefaultConnectionContext();
        var authenticationOptions = new TlsServerAuthenticationOptions();
        var underlyingOptions = Assert.IsType<SslServerAuthenticationOptions>(
            authenticationOptions.SslServerAuthenticationOptions);
        ConnectionContext? receivedConnection = null;
        TlsServerAuthenticationOptions? receivedOptions = null;
        Action<ConnectionContext, TlsServerAuthenticationOptions> configure = (context, options) =>
        {
            receivedConnection = context;
            receivedOptions = options;
            options.ClientCertificateRequired = true;
            options.EnabledSslProtocols = SslProtocols.Tls13;
            options.CertificateRevocationCheckMode = X509RevocationMode.Offline;
        };
        var options = new TlsOptions
        {
            OnAuthenticateAsServer = configure
        };

        options.OnAuthenticateAsServer(connection, authenticationOptions);

        Assert.Same(configure, options.OnAuthenticateAsServer);
        Assert.Same(connection, receivedConnection);
        Assert.Same(authenticationOptions, receivedOptions);
        Assert.Same(underlyingOptions, authenticationOptions.SslServerAuthenticationOptions);
        Assert.True(underlyingOptions.ClientCertificateRequired);
        Assert.Equal(SslProtocols.Tls13, underlyingOptions.EnabledSslProtocols);
        Assert.Equal(X509RevocationMode.Offline, underlyingOptions.CertificateRevocationCheckMode);
    }
}
