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
    /// Configures TLS and provider-neutral bearer-token authentication for silo-to-silo connections.
    /// Gateway connections are not modified.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="configureTls">Configures TLS for silo connections.</param>
    /// <param name="configureAuthentication">Configures authentication policy and providers.</param>
    /// <returns>The silo builder.</returns>
    public static ISiloBuilder UseAuthenticatedSiloConnections(
        this ISiloBuilder builder,
        Action<TlsOptions> configureTls,
        Action<SiloConnectionAuthenticationBuilder> configureAuthentication)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configureTls);
        ArgumentNullException.ThrowIfNull(configureAuthentication);

        if (builder.Services.Any(descriptor =>
            descriptor.ServiceType == typeof(SiloConnectionAuthenticationRegistration)
            || descriptor.ServiceType == typeof(SiloTlsRegistrationMarker)))
        {
            throw new InvalidOperationException("Silo TLS or connection authentication has already been configured.");
        }

        var tlsOptions = new TlsOptions();
        configureTls(tlsOptions);
        if (tlsOptions.LocalCertificate is null && tlsOptions.LocalServerCertificateSelector is null)
        {
            throw new InvalidOperationException("No silo TLS certificate was specified.");
        }

        if (tlsOptions.LocalCertificate is { } certificate && !certificate.HasPrivateKey)
        {
            TlsConnectionBuilderExtensions.ThrowNoPrivateKey(
                certificate,
                $"{nameof(TlsOptions)}.{nameof(TlsOptions.LocalCertificate)}");
        }

        const string registrationName = "Orleans.SiloConnections";
        var authenticationOptions = new SiloConnectionAuthenticationOptions();
        var authenticationBuilder = new SiloConnectionAuthenticationBuilder(
            registrationName,
            ConnectionAuthenticationServiceKeys.Silo,
            authenticationOptions,
            builder.Services);
        configureAuthentication(authenticationBuilder);
        var tlsSnapshot = SiloConnectionAuthenticationRegistration.CloneTlsOptions(tlsOptions);
        var authenticationSnapshot = SiloConnectionAuthenticationRegistration.CloneOptions(authenticationOptions);
        SiloConnectionAuthenticationRegistration.ConfigureApplicationProtocols(tlsSnapshot, authenticationSnapshot);

        var registration = new SiloConnectionAuthenticationRegistration(
            registrationName,
            ConnectionAuthenticationServiceKeys.Silo,
            authenticationSnapshot,
            tlsSnapshot,
            authenticationBuilder.HasTokenProvider,
            authenticationBuilder.HasTokenValidator);

        RegisterAuthentication(builder.Services, registration);
        builder.Services.AddSingleton<InboundSiloConnectionAuthenticationMiddleware>();
        builder.Services.AddSingleton<OutboundSiloConnectionAuthenticationMiddleware>();

        return builder.Configure<SiloConnectionOptions>(connectionOptions =>
        {
            connectionOptions.ConfigureSiloInboundConnection(connectionBuilder =>
            {
                connectionBuilder.UseServerTls(tlsSnapshot);
                connectionBuilder.UseMiddleware<InboundSiloConnectionAuthenticationMiddleware>();
            });

            connectionOptions.ConfigureSiloOutboundConnection(connectionBuilder =>
            {
                connectionBuilder.UseClientTls(tlsSnapshot);
                connectionBuilder.UseMiddleware<OutboundSiloConnectionAuthenticationMiddleware>();
            });
        });
    }
}
