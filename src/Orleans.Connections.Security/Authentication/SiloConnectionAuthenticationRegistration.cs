using System.Collections.Generic;
using System.Net.Security;

namespace Orleans.Connections.Security;

internal abstract class ConnectionAuthenticationRegistration
{
    protected ConnectionAuthenticationRegistration(
        string name,
        object serviceKey,
        SiloConnectionAuthenticationTarget target,
        SiloConnectionAuthenticationOptions options,
        TlsOptions tlsOptions,
        bool hasTokenProvider,
        bool hasTokenValidator,
        bool requiresTokenProvider,
        bool requiresTokenValidator)
    {
        Name = name;
        ServiceKey = serviceKey;
        Target = target;
        Options = options;
        TlsOptions = tlsOptions;
        HasTokenProvider = hasTokenProvider;
        HasTokenValidator = hasTokenValidator;
        RequiresTokenProvider = requiresTokenProvider;
        RequiresTokenValidator = requiresTokenValidator;
        WorkLimiter = new AuthenticationWorkLimiter(options);
    }

    public string Name { get; }

    public object ServiceKey { get; }

    public SiloConnectionAuthenticationTarget Target { get; }

    public SiloConnectionAuthenticationOptions Options { get; }

    public TlsOptions TlsOptions { get; }

    public bool HasTokenProvider { get; }

    public bool HasTokenValidator { get; }

    public bool RequiresTokenProvider { get; }

    public bool RequiresTokenValidator { get; }

    public AuthenticationWorkLimiter WorkLimiter { get; }

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
        var clientCallback = tlsOptions.OnAuthenticateAsClient;
        if (authenticationOptions.Mode == SiloConnectionAuthenticationMode.Required
            && (serverCallback is not null || clientCallback is not null))
        {
            throw new InvalidOperationException(
                "Required mode does not permit direct per-connection TLS authentication callbacks.");
        }

        tlsOptions.OnAuthenticateAsServer = (context, options) =>
        {
            serverCallback?.Invoke(context, options);
            var sslOptions = (SslServerAuthenticationOptions)options.SslServerAuthenticationOptions;
            sslOptions.ApplicationProtocols = CreateApplicationProtocols(authenticationOptions.Mode);
        };

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

internal sealed class SiloConnectionAuthenticationRegistration(
    string name,
    object serviceKey,
    SiloConnectionAuthenticationOptions options,
    TlsOptions tlsOptions,
    bool hasTokenProvider,
    bool hasTokenValidator)
    : ConnectionAuthenticationRegistration(
        name,
        serviceKey,
        SiloConnectionAuthenticationTarget.Silo,
        options,
        tlsOptions,
        hasTokenProvider,
        hasTokenValidator,
        requiresTokenProvider: true,
        requiresTokenValidator: true);

internal sealed class GatewayConnectionAuthenticationRegistration(
    string name,
    object serviceKey,
    SiloConnectionAuthenticationOptions options,
    TlsOptions tlsOptions,
    bool hasTokenProvider,
    bool hasTokenValidator)
    : ConnectionAuthenticationRegistration(
        name,
        serviceKey,
        SiloConnectionAuthenticationTarget.Client,
        options,
        tlsOptions,
        hasTokenProvider,
        hasTokenValidator,
        requiresTokenProvider: false,
        requiresTokenValidator: true);

internal sealed class ClientConnectionAuthenticationRegistration(
    string name,
    object serviceKey,
    SiloConnectionAuthenticationOptions options,
    TlsOptions tlsOptions,
    bool hasTokenProvider,
    bool hasTokenValidator)
    : ConnectionAuthenticationRegistration(
        name,
        serviceKey,
        SiloConnectionAuthenticationTarget.Client,
        options,
        tlsOptions,
        hasTokenProvider,
        hasTokenValidator,
        requiresTokenProvider: true,
        requiresTokenValidator: false);

internal static class ConnectionAuthenticationServiceKeys
{
    public static readonly object Silo = new();
    public static readonly object Gateway = new();
    public static readonly object Client = new();
}

internal sealed class SiloTlsRegistrationMarker;

internal sealed class GatewayTlsRegistrationMarker;

internal sealed class ClientTlsRegistrationMarker;
