using System.Net;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using Orleans.DurableTasks.Http;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the services which serve durable tasks as HTTP operations.</summary>
public static class DurableHttpServiceCollectionExtensions
{
    /// <summary>
    /// Adds the services which the durable operation endpoints use, which
    /// <see cref="Microsoft.AspNetCore.Builder.DurableHttpEndpointRouteBuilderExtensions"/> and
    /// <see cref="Microsoft.AspNetCore.Builder.NexusEndpointRouteBuilderExtensions"/> map.
    /// </summary>
    /// <param name="services">The service collection, which also provides an Orleans <see cref="Orleans.IGrainFactory"/>.</param>
    /// <param name="configureOptions">Configures the operations, or <see langword="null"/> for the defaults.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddDurableHttp(this IServiceCollection services, Action<DurableHttpOptions>? configureOptions = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = services.AddOptions<DurableHttpOptions>();
        if (configureOptions is not null)
        {
            options.Configure(configureOptions);
        }

        services.AddDataProtection();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<DurableOperationService>();
        services.TryAddSingleton<DurableCallbackRegistrar>();
        services.TryAddSingleton<DurableHttpOperations>();
        services.TryAddSingleton<INexusOperationTokenFormat, PlainTextNexusOperationTokenFormat>();
        services.TryAddSingleton<DurableNexusOperations>();
        return services;
    }

    /// <summary>
    /// Adds an <see cref="IDurableHttpClient"/>, which durable tasks inject as a keyed service named
    /// <paramref name="name"/>, and returns the builder of the named <see cref="HttpClient"/> which it sends requests with.
    /// </summary>
    /// <param name="services">The silo's service collection.</param>
    /// <param name="name">The client's name, which keys the service and names the <see cref="HttpClient"/>.</param>
    /// <param name="configureOptions">Configures the client, or <see langword="null"/> for the defaults.</param>
    /// <returns>
    /// The builder of the named client, whose first handler is a <see cref="DurableIdempotencyKeyHandler"/>, so handlers
    /// which the builder adds, such as a resilience handler, retry requests with their keys.
    /// </returns>
    /// <example>
    /// <code>
    /// services.AddDurableHttpClient("payments")
    ///     .ConfigureHttpClient(client =&gt; client.BaseAddress = new("https://payments.example"))
    ///     .AddStandardResilienceHandler();
    ///
    /// public sealed class OrderGrain([FromKeyedServices("payments")] IDurableHttpClient payments) : DurableGrain, IOrderGrain
    /// </code>
    /// </example>
    public static IHttpClientBuilder AddDurableHttpClient(this IServiceCollection services, string name, Action<DurableHttpClientOptions>? configureOptions = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrEmpty(name);
        var options = services.AddOptions<DurableHttpClientOptions>(name);
        if (configureOptions is not null)
        {
            options.Configure(configureOptions);
        }

        services.TryAddKeyedSingleton<IDurableHttpClient>(name, static (provider, key) =>
        {
            var name = (string)key!;
            return new DurableHttpClient(
                provider.GetRequiredService<IHttpClientFactory>(),
                name,
                provider.GetRequiredService<IOptionsMonitor<DurableHttpClientOptions>>().Get(name));
        });
        return services.AddHttpClient(name).AddDurableIdempotencyKey();
    }

    /// <summary>
    /// Adds the services with which silos deliver callbacks, which requests register with an <c>Operation-Callback</c>
    /// header or a Nexus <c>callback</c>, and returns the builder of the <see cref="HttpClient"/> which delivers them.
    /// </summary>
    /// <param name="services">The silo's service collection.</param>
    /// <param name="configureOptions">Configures delivery, or <see langword="null"/> for the defaults.</param>
    /// <returns>
    /// The builder of the client which delivers callbacks, which configures its handlers, such as a proxy. The client
    /// doesn't follow redirects, which retries as undelivered, and connects only to the addresses which
    /// <see cref="DurableCallbackOptions.AllowAddress"/> accepts.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Each callback is a detached durable task on a relay grain, which waits for its operation's outcome and delivers it
    /// with durable retries. Deliveries describe failures with <see cref="DurableHttpOptions.CreateError"/>, so configure
    /// <see cref="DurableHttpOptions"/> in the silos as in the hosts which serve the operations; a host which runs both
    /// shares one configuration. Register an <see cref="IDurableWebhookSigner"/> to sign webhooks.
    /// </para>
    /// <para>
    /// The hosts protect the values of the headers which callbacks carry, which can be credentials, with ASP.NET Core Data
    /// Protection before the silos store them, and deliveries unprotect them, so hosts and silos share a Data Protection
    /// key ring and application name, such as with <c>AddDataProtection().PersistKeysTo...().SetApplicationName(...)</c>.
    /// </para>
    /// <para>
    /// The client's primary handler is a <see cref="SocketsHttpHandler"/>, whose <c>ConnectCallback</c> applies
    /// <see cref="DurableCallbackOptions.AllowAddress"/>. A primary handler which the application configures is a
    /// <see cref="SocketsHttpHandler"/> without a <c>ConnectCallback</c>, or, when <c>AllowAddress</c> is
    /// <see langword="null"/>, a <see cref="SocketsHttpHandler"/> or an <see cref="HttpClientHandler"/>; building the client
    /// with another handler throws <see cref="InvalidOperationException"/>, since the package can't keep it from following
    /// redirects or connecting to internal addresses.
    /// </para>
    /// </remarks>
    public static IHttpClientBuilder AddDurableHttpCallbacks(this IServiceCollection services, Action<DurableCallbackOptions>? configureOptions = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = services.AddOptions<DurableCallbackOptions>();
        if (configureOptions is not null)
        {
            options.Configure(configureOptions);
        }

        services.AddOptions<DurableHttpOptions>();
        services.AddDataProtection();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHttpMessageHandlerBuilderFilter, DurableCallbackHandlerFilter>());
        return services.AddHttpClient(DurableCallbackOptions.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(static () => new SocketsHttpHandler { MaxConnectionsPerServer = DurableCallbackHandlerFilter.MaxConnectionsPerServer });
    }
}

/// <summary>
/// Keeps the callback client from following redirects and from connecting to addresses which
/// <see cref="DurableCallbackOptions.AllowAddress"/> doesn't accept, whichever primary handler the application configures.
/// </summary>
/// <remarks>
/// A redirect, or a host name which resolves to another address than when the application accepted the URL, would send a
/// delivery to a receiver which <see cref="DurableHttpOptions.AllowCallback"/> didn't accept. The filter runs after every
/// configuration of the client's handlers, and fails to build a client whose primary handler it can't constrain.
/// </remarks>
internal sealed class DurableCallbackHandlerFilter(Microsoft.Extensions.Options.IOptions<DurableCallbackOptions> options) : IHttpMessageHandlerBuilderFilter
{
    /// <summary>The most connections which the default primary handler opens to one receiver.</summary>
    internal const int MaxConnectionsPerServer = 16;

    public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) => builder =>
    {
        next(builder);
        if (builder.Name != DurableCallbackOptions.HttpClientName)
        {
            return;
        }

        var allowAddress = options.Value.AllowAddress;
        switch (builder.PrimaryHandler)
        {
            case SocketsHttpHandler sockets when sockets.ConnectCallback is null && allowAddress is not null:
                sockets.AllowAutoRedirect = false;
                sockets.ConnectCallback = (context, cancellationToken) =>
                    DurableCallbackConnections.ConnectAsync(context.DnsEndPoint, allowAddress, cancellationToken);
                break;
            case SocketsHttpHandler sockets when allowAddress is null:
                sockets.AllowAutoRedirect = false;
                break;
            case HttpClientHandler client when allowAddress is null:
                client.AllowAutoRedirect = false;
                break;
            default:
                throw new InvalidOperationException(
                    $"The callback client's primary handler, '{builder.PrimaryHandler.GetType()}', can't be kept from following redirects or connecting to addresses which DurableCallbackOptions.AllowAddress doesn't accept. Use a SocketsHttpHandler without a ConnectCallback, or set AllowAddress to null to leave connections to the handler.");
        }
    };
}