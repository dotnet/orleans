using Microsoft.Extensions.DependencyInjection.Extensions;
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

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<DurableOperationService>();
        services.TryAddSingleton<DurableCallbackRegistrar>();
        services.TryAddSingleton<DurableHttpOperations>();
        services.TryAddSingleton<INexusOperationTokenFormat, PlainTextNexusOperationTokenFormat>();
        services.TryAddSingleton<DurableNexusOperations>();
        return services;
    }

    /// <summary>
    /// Adds the services with which silos deliver callbacks, which requests register with an <c>Operation-Callback</c>
    /// header or a Nexus <c>callback</c>, and returns the builder of the <see cref="HttpClient"/> which delivers them.
    /// </summary>
    /// <param name="services">The silo's service collection.</param>
    /// <param name="configureOptions">Configures delivery, or <see langword="null"/> for the defaults.</param>
    /// <returns>
    /// The builder of the client which delivers callbacks, which configures its handlers, such as a proxy. The client
    /// doesn't follow redirects, which retries as undelivered.
    /// </returns>
    /// <remarks>
    /// Each callback is a detached durable task on a relay grain, which waits for its operation's outcome and delivers it
    /// with durable retries. Deliveries describe failures with <see cref="DurableHttpOptions.CreateError"/>, so configure
    /// <see cref="DurableHttpOptions"/> in the silos as in the hosts which serve the operations; a host which runs both
    /// shares one configuration. Register an <see cref="IDurableWebhookSigner"/> to sign webhooks.
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

        services.TryAddEnumerable(ServiceDescriptor.Singleton<Microsoft.Extensions.Http.IHttpMessageHandlerBuilderFilter, DurableCallbackRedirectFilter>());
        return services.AddHttpClient(DurableCallbackOptions.HttpClientName);
    }
}

/// <summary>Keeps the callback client from following redirects, whichever primary handler the application configures.</summary>
/// <remarks>
/// A redirect would send a delivery to a URL which <see cref="Orleans.DurableTasks.Http.DurableHttpOptions.AllowCallback"/>
/// didn't accept. The filter runs after every configuration of the client's handlers.
/// </remarks>
internal sealed class DurableCallbackRedirectFilter : Microsoft.Extensions.Http.IHttpMessageHandlerBuilderFilter
{
    public Action<Microsoft.Extensions.Http.HttpMessageHandlerBuilder> Configure(Action<Microsoft.Extensions.Http.HttpMessageHandlerBuilder> next) => builder =>
    {
        next(builder);
        if (builder.Name == DurableCallbackOptions.HttpClientName)
        {
            switch (builder.PrimaryHandler)
            {
                case SocketsHttpHandler sockets:
                    sockets.AllowAutoRedirect = false;
                    break;
                case HttpClientHandler client:
                    client.AllowAutoRedirect = false;
                    break;
            }
        }
    };
}
