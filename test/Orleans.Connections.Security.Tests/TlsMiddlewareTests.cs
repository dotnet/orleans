using System.IO.Pipelines;
using System.Security.Authentication;
using Microsoft.AspNetCore.Connections;
using Orleans.Connections.Security;
using Xunit;

namespace Orleans.Connections.Security.Tests;

public class TlsMiddlewareTests
{
    [Fact]
    public async Task ClientMiddleware_InvokesAuthenticationCallbackAfterApplyingBaseOptions()
    {
        var context = CreateConnectionContext();
        var callbackInvoked = false;
        var nextInvoked = false;
        ConnectionContext? callbackContext = null;
        TlsClientAuthenticationOptions? callbackOptions = null;
        var options = new TlsOptions
        {
            HandshakeTimeout = Timeout.InfiniteTimeSpan,
            RemoteCertificateMode = RemoteCertificateMode.NoCertificate,
            SslProtocols = SslProtocols.Tls12,
            OnAuthenticateAsClient = (actualContext, authenticationOptions) =>
            {
                callbackInvoked = true;
                callbackContext = actualContext;
                callbackOptions = authenticationOptions;
                throw new ExpectedAuthenticationException();
            }
        };
        var middleware = new TlsClientConnectionMiddleware(options, loggerFactory: null);

        await middleware.OnConnectionAsync(
            context,
            _ =>
            {
                nextInvoked = true;
                return Task.CompletedTask;
            });

        Assert.True(callbackInvoked);
        Assert.False(nextInvoked);
        Assert.Same(context, callbackContext);
        var receivedOptions = Assert.IsType<TlsClientAuthenticationOptions>(callbackOptions);
        Assert.Equal(SslProtocols.Tls12, receivedOptions.EnabledSslProtocols);
        Assert.Null(receivedOptions.ClientCertificates);
    }

    [Fact]
    public async Task ServerMiddleware_InvokesAuthenticationCallbackAfterApplyingBaseOptions()
    {
        using var certificate = TestCertificateHelper.CreateSelfSignedCertificate("middleware-server.test");
        var context = CreateConnectionContext();
        var callbackInvoked = false;
        var nextInvoked = false;
        ConnectionContext? callbackContext = null;
        TlsServerAuthenticationOptions? callbackOptions = null;
        var options = new TlsOptions
        {
            HandshakeTimeout = Timeout.InfiniteTimeSpan,
            LocalCertificate = certificate,
            RemoteCertificateMode = RemoteCertificateMode.NoCertificate,
            SslProtocols = SslProtocols.Tls12,
            OnAuthenticateAsServer = (actualContext, authenticationOptions) =>
            {
                callbackInvoked = true;
                callbackContext = actualContext;
                callbackOptions = authenticationOptions;
                throw new ExpectedAuthenticationException();
            }
        };
        var middleware = new TlsServerConnectionMiddleware(options, loggerFactory: null);

        await middleware.OnConnectionAsync(
            context,
            _ =>
            {
                nextInvoked = true;
                return Task.CompletedTask;
            });

        Assert.True(callbackInvoked);
        Assert.False(nextInvoked);
        Assert.Same(context, callbackContext);
        var receivedOptions = Assert.IsType<TlsServerAuthenticationOptions>(callbackOptions);
        Assert.Equal(SslProtocols.Tls12, receivedOptions.EnabledSslProtocols);
        Assert.False(receivedOptions.ClientCertificateRequired);
        Assert.Null(receivedOptions.ServerCertificate);
        Assert.NotNull(receivedOptions.ServerCertificateSelectionCallback);
    }

    private static DefaultConnectionContext CreateConnectionContext() =>
        new()
        {
            Transport = new TestDuplexPipe(new Pipe().Reader, new Pipe().Writer)
        };

    private sealed class ExpectedAuthenticationException : Exception
    {
    }
}
