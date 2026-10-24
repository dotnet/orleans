using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.DurableTasks.Http;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Configures HTTP clients which durable tasks use.</summary>
public static class DurableHttpClientBuilderExtensions
{
    /// <summary>
    /// Adds a <see cref="DurableIdempotencyKeyHandler"/>, which sends each request of a durable task with an
    /// <c>Idempotency-Key</c> that identifies the task, to the client's handlers.
    /// </summary>
    /// <param name="builder">The HTTP client builder, in a silo which runs durable tasks.</param>
    /// <returns>The HTTP client builder.</returns>
    /// <remarks>Call this method before adding handlers which retry requests, so their retries carry the key.</remarks>
    public static IHttpClientBuilder AddDurableIdempotencyKey(this IHttpClientBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.TryAddTransient<DurableIdempotencyKeyHandler>();
        return builder.AddHttpMessageHandler<DurableIdempotencyKeyHandler>();
    }
}
