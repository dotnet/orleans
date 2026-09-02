using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Connections.Security;
using Orleans.Runtime.Messaging;

namespace Orleans.Hosting;

public static partial class OrleansConnectionSecurityHostingExtensions
{
    /// <summary>
    /// Configures TLS and provider-neutral bearer-token authentication for connections from Orleans clients.
    /// Silo-to-silo connections are not modified.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="configureTls">Configures TLS for gateway connections.</param>
    /// <param name="configureAuthentication">Configures authentication policy and token validation.</param>
    /// <returns>The silo builder.</returns>
    public static ISiloBuilder UseAuthenticatedClientConnections(
        this ISiloBuilder builder,
        Action<TlsOptions> configureTls,
        Action<SiloConnectionAuthenticationBuilder> configureAuthentication)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configureTls);
        ArgumentNullException.ThrowIfNull(configureAuthentication);

        if (builder.Services.Any(descriptor =>
            descriptor.ServiceType == typeof(GatewayConnectionAuthenticationRegistration)
            || descriptor.ServiceType == typeof(GatewayTlsRegistrationMarker)))
        {
            throw new InvalidOperationException("Gateway TLS or client connection authentication has already been configured.");
        }

        var tlsOptions = new TlsOptions();
        configureTls(tlsOptions);
        ValidateServerTlsOptions(tlsOptions, "gateway");

        const string registrationName = "Orleans.GatewayConnections";
        var authenticationOptions = new SiloConnectionAuthenticationOptions();
        var authenticationBuilder = new SiloConnectionAuthenticationBuilder(
            registrationName,
            ConnectionAuthenticationServiceKeys.Gateway,
            authenticationOptions,
            builder.Services);
        configureAuthentication(authenticationBuilder);

        var tlsSnapshot = ConnectionAuthenticationRegistration.CloneTlsOptions(tlsOptions);
        var authenticationSnapshot = ConnectionAuthenticationRegistration.CloneOptions(authenticationOptions);
        ConnectionAuthenticationRegistration.ConfigureApplicationProtocols(tlsSnapshot, authenticationSnapshot);
        var registration = new GatewayConnectionAuthenticationRegistration(
            registrationName,
            ConnectionAuthenticationServiceKeys.Gateway,
            authenticationSnapshot,
            tlsSnapshot,
            authenticationBuilder.HasTokenProvider,
            authenticationBuilder.HasTokenValidator);

        RegisterAuthentication(builder.Services, registration);
        builder.Services.AddSingleton<InboundGatewayConnectionAuthenticationMiddleware>();

        return builder.Configure<SiloConnectionOptions>(connectionOptions =>
            connectionOptions.ConfigureGatewayInboundConnection(connectionBuilder =>
            {
                connectionBuilder.UseServerTls(tlsSnapshot);
                connectionBuilder.UseMiddleware<InboundGatewayConnectionAuthenticationMiddleware>();
            }));
    }

    /// <summary>
    /// Configures TLS and provider-neutral bearer-token authentication for connections to Orleans gateways.
    /// </summary>
    /// <param name="builder">The client builder.</param>
    /// <param name="configureTls">Configures TLS for gateway connections.</param>
    /// <param name="configureAuthentication">Configures authentication policy and token acquisition.</param>
    /// <returns>The client builder.</returns>
    public static IClientBuilder UseAuthenticatedClientConnections(
        this IClientBuilder builder,
        Action<TlsOptions> configureTls,
        Action<SiloConnectionAuthenticationBuilder> configureAuthentication)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configureTls);
        ArgumentNullException.ThrowIfNull(configureAuthentication);

        if (builder.Services.Any(descriptor =>
            descriptor.ServiceType == typeof(ClientConnectionAuthenticationRegistration)
            || descriptor.ServiceType == typeof(ClientTlsRegistrationMarker)))
        {
            throw new InvalidOperationException("Client TLS or connection authentication has already been configured.");
        }

        var tlsOptions = new TlsOptions();
        configureTls(tlsOptions);
        ValidateClientTlsOptions(tlsOptions);

        const string registrationName = "Orleans.ClientConnections";
        var authenticationOptions = new SiloConnectionAuthenticationOptions();
        var authenticationBuilder = new SiloConnectionAuthenticationBuilder(
            registrationName,
            ConnectionAuthenticationServiceKeys.Client,
            authenticationOptions,
            builder.Services);
        configureAuthentication(authenticationBuilder);

        var tlsSnapshot = ConnectionAuthenticationRegistration.CloneTlsOptions(tlsOptions);
        var authenticationSnapshot = ConnectionAuthenticationRegistration.CloneOptions(authenticationOptions);
        ConnectionAuthenticationRegistration.ConfigureApplicationProtocols(tlsSnapshot, authenticationSnapshot);
        var registration = new ClientConnectionAuthenticationRegistration(
            registrationName,
            ConnectionAuthenticationServiceKeys.Client,
            authenticationSnapshot,
            tlsSnapshot,
            authenticationBuilder.HasTokenProvider,
            authenticationBuilder.HasTokenValidator);

        RegisterAuthentication(builder.Services, registration);
        builder.Services.AddSingleton<OutboundClientConnectionAuthenticationMiddleware>();

        return builder.Configure<ClientConnectionOptions>(connectionOptions =>
            connectionOptions.ConfigureConnection(connectionBuilder =>
            {
                connectionBuilder.UseClientTls(tlsSnapshot);
                connectionBuilder.UseMiddleware<OutboundClientConnectionAuthenticationMiddleware>();
            }));
    }

    private static void RegisterAuthentication(
        IServiceCollection services,
        ConnectionAuthenticationRegistration registration)
    {
        switch (registration)
        {
            case SiloConnectionAuthenticationRegistration silo:
                services.AddSingleton(silo);
                break;
            case GatewayConnectionAuthenticationRegistration gateway:
                services.AddSingleton(gateway);
                break;
            case ClientConnectionAuthenticationRegistration client:
                services.AddSingleton(client);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(registration));
        }

        services.AddSingleton<IValidateOptions<SiloConnectionAuthenticationOptions>>(
            new SiloConnectionAuthenticationOptionsValidator(registration));
        services
            .AddOptions<SiloConnectionAuthenticationOptions>(registration.Name)
            .Configure(registration.CopyOptionsTo)
            .ValidateOnStart();

        if (registration is SiloConnectionAuthenticationRegistration)
        {
            services
                .AddOptions<SiloConnectionAuthenticationOptions>()
                .Configure(registration.CopyOptionsTo);
        }
    }

    private static void ValidateServerTlsOptions(TlsOptions options, string connectionKind)
    {
        if (options.LocalCertificate is null && options.LocalServerCertificateSelector is null)
        {
            throw new InvalidOperationException($"No {connectionKind} TLS certificate was specified.");
        }

        if (options.LocalCertificate is { } certificate && !certificate.HasPrivateKey)
        {
            TlsConnectionBuilderExtensions.ThrowNoPrivateKey(
                certificate,
                $"{nameof(TlsOptions)}.{nameof(TlsOptions.LocalCertificate)}");
        }
    }

    private static void ValidateClientTlsOptions(TlsOptions options)
    {
        if (options.LocalCertificate is null
            && options.LocalClientCertificateSelector is null
            && options.ClientCertificateMode == RemoteCertificateMode.RequireCertificate)
        {
            throw new InvalidOperationException("No client TLS certificate or certificate selector was specified.");
        }

        if (options.LocalCertificate is { } certificate && !certificate.HasPrivateKey)
        {
            TlsConnectionBuilderExtensions.ThrowNoPrivateKey(
                certificate,
                $"{nameof(TlsOptions)}.{nameof(TlsOptions.LocalCertificate)}");
        }
    }
}
