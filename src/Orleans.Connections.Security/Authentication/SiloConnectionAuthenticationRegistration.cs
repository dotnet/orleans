using System.Collections.Generic;
using System.Net.Security;

namespace Orleans.Connections.Security;

internal sealed class SiloConnectionAuthenticationRegistration
{
    public required SiloConnectionAuthenticationOptions Options { get; init; }

    public required TlsOptions TlsOptions { get; init; }

    public required bool HasTokenProvider { get; init; }

    public required bool HasTokenValidator { get; init; }

    public static SiloConnectionAuthenticationOptions CloneOptions(SiloConnectionAuthenticationOptions source)
    {
        var result = new SiloConnectionAuthenticationOptions();
        CopyOptions(source, result);
        return result;
    }

    public static TlsOptions CloneTlsOptions(TlsOptions source) => new()
    {
        LocalCertificate = source.LocalCertificate,
        LocalServerCertificateSelector = source.LocalServerCertificateSelector,
        LocalClientCertificateSelector = source.LocalClientCertificateSelector,
        RemoteCertificateMode = source.RemoteCertificateMode,
        ClientCertificateMode = source.ClientCertificateMode,
        RemoteCertificateValidation = source.RemoteCertificateValidation,
        SslProtocols = source.SslProtocols,
        CheckCertificateRevocation = source.CheckCertificateRevocation,
        OnAuthenticateAsServer = source.OnAuthenticateAsServer,
        OnAuthenticateAsClient = source.OnAuthenticateAsClient,
        HandshakeTimeout = source.HandshakeTimeout,
    };

    public void CopyOptionsTo(SiloConnectionAuthenticationOptions options)
    {
        CopyOptions(Options, options);
    }

    private static void CopyOptions(
        SiloConnectionAuthenticationOptions source,
        SiloConnectionAuthenticationOptions destination)
    {
        destination.Mode = source.Mode;
        destination.TokenExchangeTimeout = source.TokenExchangeTimeout;
        destination.MaxTokenSize = source.MaxTokenSize;
        destination.MaxConcurrentInboundAuthentications = source.MaxConcurrentInboundAuthentications;
        destination.MaxConcurrentOutboundAuthentications = source.MaxConcurrentOutboundAuthentications;
        destination.MaxPendingInboundAuthentications = source.MaxPendingInboundAuthentications;
        destination.MaxPendingOutboundAuthentications = source.MaxPendingOutboundAuthentications;
        destination.MinimumRemainingTokenLifetime = source.MinimumRemainingTokenLifetime;
        destination.ExpirationSafetyMargin = source.ExpirationSafetyMargin;
        destination.ExpirationJitter = source.ExpirationJitter;
        destination.AllowNonExpiringCredentials = source.AllowNonExpiringCredentials;
        destination.TargetHost = source.TargetHost;
        destination.TimeProvider = source.TimeProvider;
    }

    public static void ConfigureApplicationProtocols(
        TlsOptions tlsOptions,
        SiloConnectionAuthenticationOptions authenticationOptions)
    {
        var serverCallback = tlsOptions.OnAuthenticateAsServer;
        tlsOptions.OnAuthenticateAsServer = (context, options) =>
        {
            serverCallback?.Invoke(context, options);
            var sslOptions = (SslServerAuthenticationOptions)options.SslServerAuthenticationOptions;
            sslOptions.ApplicationProtocols = CreateApplicationProtocols(authenticationOptions.Mode);
        };

        var clientCallback = tlsOptions.OnAuthenticateAsClient;
        tlsOptions.OnAuthenticateAsClient = (context, options) =>
        {
            clientCallback?.Invoke(context, options);
            var sslOptions = (SslClientAuthenticationOptions)options.SslClientAuthenticationOptions;
            sslOptions.ApplicationProtocols = CreateApplicationProtocols(authenticationOptions.Mode);
            if (!string.IsNullOrWhiteSpace(authenticationOptions.TargetHost))
            {
                sslOptions.TargetHost = authenticationOptions.TargetHost;
            }
        };
    }

    private static List<SslApplicationProtocol> CreateApplicationProtocols(SiloConnectionAuthenticationMode mode) => mode switch
    {
        SiloConnectionAuthenticationMode.Disabled => [OrleansApplicationProtocol.Orleans1],
        SiloConnectionAuthenticationMode.Audit => [OrleansApplicationProtocol.Orleans1TokenAuth2, OrleansApplicationProtocol.Orleans1],
        SiloConnectionAuthenticationMode.Required => [OrleansApplicationProtocol.Orleans1TokenAuth2],
        _ => [],
    };
}

internal sealed class SiloTlsRegistrationMarker;

internal sealed class GatewayTlsRegistrationMarker;
