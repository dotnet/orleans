using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Orleans.Runtime;

namespace Orleans.DurableTasks.Http;

/// <summary>Reads callback registrations from requests which start operations, and starts their relays.</summary>
internal sealed class DurableCallbackRegistrar(IGrainFactory grainFactory, IOptions<DurableHttpOptions> options)
{
    /// <summary>The names, and name prefixes, of headers which deliveries set themselves or which address the connection.</summary>
    private static readonly string[] ReservedHeaders =
        ["host", "connection", "keep-alive", "te", "trailer", "transfer-encoding", "upgrade", "expect", "operation-location"];

    private static readonly string[] ReservedHeaderPrefixes = ["proxy-", "content-", "webhook-", "nexus-operation-"];

    /// <summary>
    /// Reads the callback which the request registers with <paramref name="url"/> and the headers which start with
    /// <paramref name="headerPrefix"/>, and returns <see langword="false"/> with a description when the application doesn't
    /// accept it.
    /// </summary>
    /// <remarks>
    /// A delivery carries the registered headers as request headers, so a header which addresses the connection, such as
    /// <c>Host</c>, which would send the delivery to another server than the URL which the application accepted, or which
    /// the delivery sets itself, such as <c>webhook-signature</c>, isn't accepted.
    /// </remarks>
    /// <param name="context">The request.</param>
    /// <param name="url">The callback URL, or <see langword="null"/> when the request registers none.</param>
    /// <param name="headerPrefix">The prefix of the headers which each delivery carries, without the prefix.</param>
    /// <param name="registration">The registration, or <see langword="null"/> when the request registers none.</param>
    /// <param name="error">A description of a callback which the application doesn't accept.</param>
    public bool TryRead(
        HttpContext context,
        string? url,
        string headerPrefix,
        out DurableCallbackRegistration? registration,
        out string? error)
    {
        registration = null;
        error = null;
        if (string.IsNullOrEmpty(url))
        {
            return true;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            error = "A callback URL is an absolute http or https URL.";
            return false;
        }

        if (options.Value.AllowCallback is not { } allow || !allow(context, uri))
        {
            error = "This server doesn't accept the callback URL.";
            return false;
        }

        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, values) in context.Request.Headers)
        {
            if (name.Length > headerPrefix.Length && name.StartsWith(headerPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var deliveryHeader = name[headerPrefix.Length..].ToLowerInvariant();
                if (Array.IndexOf(ReservedHeaders, deliveryHeader) >= 0
                    || Array.Exists(ReservedHeaderPrefixes, prefix => deliveryHeader.StartsWith(prefix, StringComparison.Ordinal)))
                {
                    error = $"A callback doesn't carry the '{deliveryHeader}' header, which addresses the connection or which deliveries set themselves.";
                    return false;
                }

                headers[deliveryHeader] = values.ToString();
            }
        }

        registration = new(uri, headers);
        return true;
    }

    /// <summary>
    /// Starts the relay which delivers the outcome of the operation which <paramref name="host"/> hosts to the registered
    /// callback; a repeated registration of the same callback reaches the same relay.
    /// </summary>
    public async ValueTask RegisterAsync(
        DurableCallbackRegistration registration,
        DurableCallbackProtocol protocol,
        GrainId host,
        string operationId,
        string? operationLocation,
        CancellationToken cancellationToken)
    {
        var callback = new DurableCallback(
            protocol,
            host,
            operationId,
            registration.Url,
            registration.Headers,
            operationLocation);
        await grainFactory.GetGrain<IDurableCallbackRelayGrain>(callback.GetCallbackId())
            .DeliverAsync(callback)
            .WithId(DurableCallbackRelayGrain.DeliveryRootId).ScheduleAsync(cancellationToken);
    }
}

/// <summary>A callback which a request registers.</summary>
/// <param name="Url">The URL which receives the callback.</param>
/// <param name="Headers">The headers which each delivery carries.</param>
internal sealed record DurableCallbackRegistration(Uri Url, SortedDictionary<string, string> Headers);
