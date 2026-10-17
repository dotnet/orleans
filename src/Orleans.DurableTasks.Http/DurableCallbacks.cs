using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Journaling;
using Orleans.Runtime;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace Orleans.DurableTasks.Http;

/// <summary>Identifies the protocol of a callback, which decides its request's headers and body.</summary>
[GenerateSerializer]
public enum DurableCallbackProtocol
{
    /// <summary>
    /// A webhook which carries the operation's status monitor, with Standard Webhooks headers: <c>webhook-id</c>, which
    /// is equal for every attempt, <c>webhook-timestamp</c>, and <c>webhook-signature</c> when an
    /// <see cref="IDurableWebhookSigner"/> signs deliveries.
    /// </summary>
    Webhook,

    /// <summary>
    /// A Nexus completion callback, which carries the result or a <c>Failure</c>, with the <c>Nexus-Operation-Token</c>,
    /// <c>Nexus-Operation-State</c>, and <c>Nexus-Operation-Close-Time</c> headers. It omits
    /// <c>Nexus-Operation-Start-Time</c>, so the caller uses the time it receives the completion.
    /// </summary>
    Nexus,
}

/// <summary>Describes a callback which delivers an operation's outcome to a caller's URL.</summary>
/// <param name="Protocol">The protocol of the callback's request.</param>
/// <param name="Operation">The grain which hosts the operation.</param>
/// <param name="OperationId">The identifier by which the caller knows the operation: its operation identifier or Nexus token.</param>
/// <param name="Url">The URL which receives the callback.</param>
/// <param name="Headers">The headers which the caller asked each delivery to carry, by lowercase name.</param>
/// <param name="OperationLocation">The URL of the operation's status monitor, for webhooks.</param>
/// <remarks>
/// A callback carries only what its registration determines, so a retried registration, such as of a retried start,
/// describes an equal callback.
/// </remarks>
[GenerateSerializer, Immutable]
[Alias("Orleans.DurableTasks.Http.DurableCallback")]
public sealed record DurableCallback(
    [property: Id(0)] DurableCallbackProtocol Protocol,
    [property: Id(1)] GrainId Operation,
    [property: Id(2)] string OperationId,
    [property: Id(3)] Uri Url,
    [property: Id(4)] SortedDictionary<string, string> Headers,
    [property: Id(5)] string? OperationLocation)
{
    /// <summary>
    /// Returns the callback's identifier: a hash of every member, so a repeated registration, such as a retried start,
    /// registers the same callback, and a registration which differs in any member registers another one.
    /// </summary>
    public string GetCallbackId()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var value in Values())
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            hash.AppendData(BitConverter.GetBytes(bytes.Length));
            hash.AppendData(bytes);
        }

        return WebEncoders.Base64UrlEncode(hash.GetHashAndReset());

        IEnumerable<string> Values()
        {
            yield return Protocol.ToString();
            yield return Operation.ToString();
            yield return OperationId;
            yield return Url.AbsoluteUri;
            yield return OperationLocation ?? string.Empty;
            foreach (var (name, value) in Headers)
            {
                yield return name;
                yield return value;
            }
        }
    }
}

/// <summary>Signs webhook deliveries, per Standard Webhooks.</summary>
/// <remarks>
/// The application owns its signing keys: register an implementation in the silos' service collection, such as one
/// which computes <c>v1,{base64(HMAC-SHA256(secret, "{id}.{timestamp}.{body}"))}</c> with a secret per receiver.
/// </remarks>
public interface IDurableWebhookSigner
{
    /// <summary>Returns the <c>webhook-signature</c> header value of a delivery, or <see langword="null"/> to send none.</summary>
    /// <param name="callback">The callback which the delivery belongs to.</param>
    /// <param name="webhookId">The delivery's <c>webhook-id</c>.</param>
    /// <param name="timestamp">The delivery attempt's <c>webhook-timestamp</c>, in seconds since the Unix epoch.</param>
    /// <param name="body">The delivery's body, byte for byte.</param>
    string? Sign(DurableCallback callback, string webhookId, long timestamp, ReadOnlySpan<byte> body);
}

/// <summary>Configures callback delivery.</summary>
public sealed class DurableCallbackOptions
{
    /// <summary>The name of the <see cref="HttpClient"/> which delivers callbacks.</summary>
    public const string HttpClientName = "Orleans.DurableTasks.Http.Callbacks";

    /// <summary>
    /// Gets or sets how long delivery retries a callback, from when delivery begins, before it gives up. The default is
    /// three days.
    /// </summary>
    public TimeSpan RetryTimeout { get; set; } = TimeSpan.FromDays(3);

    /// <summary>Gets or sets the interval before the first retry, which doubles for each later retry. The default is one second.</summary>
    public TimeSpan InitialRetryInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Gets or sets the longest interval between retries. The default is five minutes.</summary>
    public TimeSpan MaxRetryInterval { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>Represents a callback which delivery gave up on.</summary>
[GenerateSerializer]
[Alias("Orleans.DurableTasks.Http.DurableCallbackDeliveryException")]
public sealed class DurableCallbackDeliveryException : Exception
{
    private DurableCallbackDeliveryException()
    {
    }

    /// <summary>Initializes an exception for a callback which delivery gave up on.</summary>
    /// <param name="message">A description of the last attempt.</param>
    public DurableCallbackDeliveryException(string message)
        : base(message)
    {
    }
}

/// <summary>Delivers a callback once its operation is terminal, retrying until the receiver accepts it.</summary>
[Alias("Orleans.DurableTasks.Http.IDurableCallbackRelayGrain")]
internal interface IDurableCallbackRelayGrain : IGrainWithStringKey
{
    [Alias("Deliver")]
    DurableTask DeliverAsync(DurableCallback callback);
}

/// <summary>
/// Relays callbacks: each callback is a detached durable task on its own relay, keyed by the callback's identifier, which
/// waits for the operation's terminal outcome and delivers it with durable retries.
/// </summary>
/// <remarks>
/// Deliveries are at least once: a receiver deduplicates by <c>webhook-id</c> or <c>Nexus-Operation-Token</c>. Delivery
/// ends when the receiver answers <c>2xx</c> or <c>410 Gone</c>, or fails with <see cref="DurableCallbackDeliveryException"/>
/// once <see cref="DurableCallbackOptions.RetryTimeout"/> elapses, which the relay retains for diagnostics.
/// </remarks>
[GrainType("orleans-durable-http-callback")]
internal sealed class DurableCallbackRelayGrain(
    IGrainFactory grainFactory,
    IHttpClientFactory httpClients,
    IServiceProvider services,
    IOptions<DurableCallbackOptions> callbackOptions,
    IOptions<DurableHttpOptions> httpOptions,
    IOptions<HttpJsonOptions> jsonOptions) : DurableGrain, IDurableCallbackRelayGrain
{
    /// <summary>The root identifier of the delivery task on its relay.</summary>
    public const string DeliveryRootId = "deliver";

    public async DurableTask DeliverAsync(DurableCallback callback)
    {
        var options = callbackOptions.Value;
        // The subscription records the operation's terminal response, whose result keeps its type, in this task's tree, and
        // logical time advances to when the operation completed.
        var subscription = await grainFactory.GetGrain(callback.Operation)
            .GetDurableTask<object?>(DurableOperationGrain.RootId)
            .WithId("observe")
            .ScheduleAsync();
        var response = DurableOperationService.Unwrap(await subscription.GetResponseAsync());
        var outcome = Format(callback, response, closedAt: DurableTask.Current.UtcNow);

        var deadline = DurableTask.Current.UtcNow + options.RetryTimeout;
        var retryInterval = options.InitialRetryInterval;
        while (true)
        {
            var attempt = await DurableTask.Run(
                static (state, cancellationToken) => state.Item1.SendAsync(state.callback, state.outcome, cancellationToken),
                (Item1: this, callback, outcome));
            if (attempt.Delivered)
            {
                return;
            }

            var retryAt = DurableTask.Current.UtcNow + (attempt.RetryAfter ?? retryInterval);
            if (retryAt > deadline)
            {
                throw new DurableCallbackDeliveryException(
                    $"The callback to '{callback.Url}' wasn't delivered within {options.RetryTimeout}; the last attempt received {attempt.Status}.");
            }

            await DurableTask.DelayUntil(retryAt);
            retryInterval = retryInterval * 2 < options.MaxRetryInterval ? retryInterval * 2 : options.MaxRetryInterval;
        }
    }

    /// <summary>Returns the request body which delivers the operation's terminal <paramref name="response"/>.</summary>
    private DurableCallbackOutcome Format(DurableCallback callback, DurableTaskResponse response, DateTimeOffset closedAt)
    {
        var json = jsonOptions.Value.SerializerOptions;
        byte[] Result() => response.ResultType is { } type ? JsonSerializer.SerializeToUtf8Bytes(response.Result, json.GetTypeInfo(type)) : [];
        var error = response.Status is DurableTaskStatus.CompletedSuccessfully ? null : httpOptions.Value.CreateError(response.Exception!);
        if (callback.Protocol == DurableCallbackProtocol.Nexus)
        {
            var state = response.Status switch
            {
                DurableTaskStatus.CompletedSuccessfully => NexusProtocol.Succeeded,
                DurableTaskStatus.Failed => NexusProtocol.Failed,
                _ => NexusProtocol.Canceled,
            };
            var body = error is null
                ? Result()
                : JsonSerializer.SerializeToUtf8Bytes(
                    new NexusFailure(error.Message, new() { ["type"] = NexusProtocol.OperationErrorType }, new() { ["state"] = state, ["code"] = error.Code }),
                    NexusJsonContext.Default.NexusFailure);
            return new(body, state, closedAt);
        }

        var operation = response.Status switch
        {
            DurableTaskStatus.CompletedSuccessfully => new DurableOperation(
                callback.OperationId,
                DurableOperationStatus.Succeeded,
                Result: response.ResultType is { } resultType ? JsonSerializer.SerializeToElement(response.Result, json.GetTypeInfo(resultType)) : null),
            DurableTaskStatus.Failed => new DurableOperation(callback.OperationId, DurableOperationStatus.Failed, Error: error),
            _ => new DurableOperation(callback.OperationId, DurableOperationStatus.Canceled, Error: error),
        };
        return new(JsonSerializer.SerializeToUtf8Bytes(operation, DurableHttpJsonContext.Default.DurableOperation), operation.Status.ToString(), closedAt);
    }

    /// <summary>Sends one delivery attempt, and returns whether the receiver accepted it.</summary>
    private async Task<DurableCallbackAttempt> SendAsync(DurableCallback callback, DurableCallbackOutcome outcome, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, callback.Url) { Content = new ByteArrayContent(outcome.Body) };
        if (outcome.Body.Length > 0)
        {
            request.Content.Headers.ContentType = new("application/json");
        }

        foreach (var (name, value) in callback.Headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        if (callback.Protocol == DurableCallbackProtocol.Nexus)
        {
            request.Headers.TryAddWithoutValidation(NexusProtocol.OperationToken, callback.OperationId);
            request.Headers.TryAddWithoutValidation(NexusProtocol.OperationState, outcome.State);
            request.Headers.TryAddWithoutValidation("Nexus-Operation-Close-Time", outcome.ClosedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffK", CultureInfo.InvariantCulture));
        }
        else
        {
            var webhookId = this.GetPrimaryKeyString();
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            request.Headers.TryAddWithoutValidation("webhook-id", webhookId);
            request.Headers.TryAddWithoutValidation("webhook-timestamp", timestamp.ToString(CultureInfo.InvariantCulture));
            if (services.GetService<IDurableWebhookSigner>()?.Sign(callback, webhookId, timestamp, outcome.Body) is { } signature)
            {
                request.Headers.TryAddWithoutValidation("webhook-signature", signature);
            }

            if (callback.OperationLocation is { } location)
            {
                request.Headers.TryAddWithoutValidation(DurableHttpHeaders.OperationLocation, location);
            }
        }

        try
        {
            using var response = await httpClients.CreateClient(DurableCallbackOptions.HttpClientName).SendAsync(request, cancellationToken);
            var delivered = response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.Gone;
            var retryAfter = response.Headers.RetryAfter is { } header
                ? header.Delta ?? (header.Date is { } date ? date - DateTimeOffset.UtcNow : null)
                : null;
            return new(delivered, $"{(int)response.StatusCode} {response.ReasonPhrase}", retryAfter > TimeSpan.Zero ? retryAfter : null);
        }
        catch (HttpRequestException exception)
        {
            return new(false, exception.Message, null);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            // The client's timeout elapsed.
            return new(false, exception.Message, null);
        }
    }
}

/// <summary>Records the request which delivers an operation's terminal outcome.</summary>
/// <param name="Body">The request's body.</param>
/// <param name="State">The operation's terminal state, as the callback's protocol names it.</param>
/// <param name="ClosedAt">When the operation completed, as the observation recorded it.</param>
[GenerateSerializer, Immutable]
internal sealed record DurableCallbackOutcome(byte[] Body, string State, DateTimeOffset ClosedAt);

/// <summary>Records a delivery attempt.</summary>
/// <param name="Delivered">Whether the receiver accepted the delivery, or retired the callback with <c>410 Gone</c>.</param>
/// <param name="Status">The response's status, or the failure which prevented one.</param>
/// <param name="RetryAfter">The interval which the response's <c>Retry-After</c> requests before the next attempt.</param>
[GenerateSerializer, Immutable]
internal sealed record DurableCallbackAttempt(bool Delivered, string Status, TimeSpan? RetryAfter);
