using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
/// <param name="OperationHost">The grain which hosts the operation.</param>
/// <param name="OperationId">The identifier by which the caller knows the operation: its operation identifier or Nexus token.</param>
/// <param name="Url">The URL which receives the callback.</param>
/// <param name="Headers">
/// The headers which the caller asked each delivery to carry, by lowercase name. Their values can be credentials, so each
/// value is protected with ASP.NET Core Data Protection, and deliveries unprotect it.
/// </param>
/// <param name="OperationLocation">The URL of the operation's status monitor, for webhooks.</param>
[GenerateSerializer, Immutable]
[Alias("Orleans.DurableTasks.Http.DurableCallback")]
public sealed record DurableCallback(
    [property: Id(0)] DurableCallbackProtocol Protocol,
    [property: Id(1)] GrainId OperationHost,
    [property: Id(2)] string OperationId,
    [property: Id(3)] Uri Url,
    [property: Id(4)] IReadOnlyDictionary<string, string> Headers,
    [property: Id(5)] string? OperationLocation)
{
    /// <summary>The purpose of the Data Protection protector which protects callback header values.</summary>
    internal const string HeaderProtectionPurpose = "Orleans.DurableTasks.Http.DurableCallback.Headers";

    /// <summary>
    /// Returns the callback's identifier: a hash of the members which address its delivery, so every registration of a
    /// receiver for an operation, such as a retried start's, reaches one relay, whichever headers it carries.
    /// </summary>
    internal string GetCallbackId()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[sizeof(int)];
        foreach (var value in (ReadOnlySpan<string>)[Protocol.ToString(), OperationHost.ToString(), OperationId, Url.AbsoluteUri, OperationLocation ?? string.Empty])
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }

        return WebEncoders.Base64UrlEncode(hash.GetHashAndReset());
    }
}

/// <summary>Signs webhook deliveries, per Standard Webhooks.</summary>
/// <remarks>
/// The application owns its signing keys: register an implementation in the silos' service collection, such as one
/// which computes <c>v1,{base64(HMAC-SHA256(secret, "{id}.{timestamp}.{body}"))}</c> with a secret per receiver. A signer
/// which throws fails the delivery attempt, which delivery retries.
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

    /// <summary>
    /// Gets or sets the longest interval between retries, including an interval which a receiver's <c>Retry-After</c>
    /// requests. The default is five minutes.
    /// </summary>
    /// <remarks>
    /// Each retry waits for the longer of the doubled interval and the receiver's <c>Retry-After</c>, at most this
    /// interval, plus up to a quarter more, which spreads the retries of many callbacks to one receiver.
    /// </remarks>
    public TimeSpan MaxRetryInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets or sets the function which decides whether a delivery connects to an address, which a callback URL's host
    /// resolves to when the delivery connects, or <see langword="null"/> to leave connections to the client's primary
    /// handler.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The default, <see cref="IsPublicAddress"/>, accepts public addresses only, so a callback URL whose host resolves to a
    /// loopback, private, link-local, or other internal address, including through DNS which changes after the
    /// application accepted the URL, doesn't reach internal services. Deliveries connect to the address which the check
    /// accepted.
    /// </para>
    /// <para>
    /// The check runs in the <c>ConnectCallback</c> of the callback client's <see cref="SocketsHttpHandler"/>. A client
    /// whose primary handler connects otherwise, such as an <see cref="HttpClientHandler"/> or a
    /// <see cref="SocketsHttpHandler"/> with its own <c>ConnectCallback</c>, fails to build unless this property is
    /// <see langword="null"/>.
    /// </para>
    /// </remarks>
    public Func<IPAddress, bool>? AllowAddress { get; set; } = IsPublicAddress;

    /// <summary>
    /// Returns whether <paramref name="address"/> is a public unicast address, rather than a loopback, private, shared,
    /// link-local, multicast, documentation, or otherwise reserved one.
    /// </summary>
    /// <param name="address">The address.</param>
    /// <returns><see langword="true"/> for a public unicast address.</returns>
    public static bool IsPublicAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            Span<byte> bytes = stackalloc byte[4];
            address.TryWriteBytes(bytes, out _);
            return bytes switch
            {
                [0, ..] or [10, ..] or [127, ..] => false,
                [100, >= 64 and <= 127, ..] => false,
                [169, 254, ..] => false,
                [172, >= 16 and <= 31, ..] => false,
                [192, 0, 0 or 2, ..] or [192, 168, ..] => false,
                [198, 18 or 19, ..] or [198, 51, 100, _] or [203, 0, 113, _] => false,
                [>= 224, ..] => false,
                _ => true,
            };
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.Equals(IPAddress.IPv6None) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast
                || address.IsIPv6UniqueLocal || address.IsIPv6Teredo || address.IsIPv4MappedToIPv6)
            {
                return false;
            }

            Span<byte> bytes = stackalloc byte[16];
            address.TryWriteBytes(bytes, out _);

            // 2002::/16 (6to4) embeds an IPv4 address, 64:ff9b::/96 translates one, and 2001:db8::/32 documents examples.
            return bytes switch
            {
                [0x20, 0x02, ..] => false,
                [0x00, 0x64, 0xff, 0x9b, ..] => false,
                [0x20, 0x01, 0x0d, 0xb8, ..] => false,
                _ => true,
            };
        }

        return false;
    }
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
/// once <see cref="DurableCallbackOptions.RetryTimeout"/> elapses, which the relay retains for diagnostics and logs.
/// </remarks>
[GrainType("orleans-durable-http-callback")]
internal sealed partial class DurableCallbackRelayGrain(
    IGrainFactory grainFactory,
    IHttpClientFactory httpClients,
    IDataProtectionProvider dataProtection,
    IServiceProvider services,
    IOptions<DurableCallbackOptions> callbackOptions,
    IOptions<DurableHttpOptions> httpOptions,
    IOptions<HttpJsonOptions> jsonOptions,
    ILogger<DurableCallbackRelayGrain> logger) : DurableGrain, IDurableCallbackRelayGrain
{
    /// <summary>The root identifier of the delivery task on its relay.</summary>
    public const string DeliveryRootId = "deliver";

    private readonly IDataProtector _headers = dataProtection.CreateProtector(DurableCallback.HeaderProtectionPurpose);

    public async DurableTask DeliverAsync(DurableCallback callback)
    {
        var options = callbackOptions.Value;
        // The subscription records the operation's terminal response, whose result keeps its type, in this task's tree, and
        // logical time advances to when the operation completed.
        var subscription = await grainFactory.GetGrain(callback.OperationHost)
            .GetDurableTask<object?>(DurableOperationGrain.RootId)
            .WithId("observe")
            .ScheduleAsync();
        var response = DurableOperationService.Unwrap(await subscription.GetResponseAsync());
        DurableCallbackOutcome outcome;
        try
        {
            outcome = Format(callback, response, closedAt: DurableTask.Current.UtcNow);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or InvalidOperationException)
        {
            LogFormatFailed(logger, exception, callback.Url, callback.OperationId);
            throw new DurableCallbackDeliveryException($"The outcome of operation '{callback.OperationId}' couldn't be formatted for '{callback.Url}': {exception.Message}");
        }

        var deadline = DurableTask.Current.UtcNow + options.RetryTimeout;
        var retryInterval = options.InitialRetryInterval;
        for (var attemptNumber = 1; ; attemptNumber++)
        {
            var attempt = await DurableTask.Run(
                static (state, cancellationToken) => state.relay.SendAsync(state.callback, state.outcome, cancellationToken),
                (relay: this, callback, outcome));
            if (attempt.Delivered)
            {
                return;
            }

            var retryAt = DurableTask.Current.UtcNow + GetRetryDelay(attempt.RetryAfter, retryInterval, options.MaxRetryInterval, this.GetPrimaryKeyString(), attemptNumber);
            if (retryAt > deadline)
            {
                LogGaveUp(logger, callback.Url, callback.OperationId, attemptNumber, attempt.Status);
                throw new DurableCallbackDeliveryException(
                    $"The callback to '{callback.Url}' wasn't delivered within {options.RetryTimeout}; the last attempt received {attempt.Status}.");
            }

            await DurableTask.DelayUntil(retryAt);
            retryInterval = retryInterval * 2 < options.MaxRetryInterval ? retryInterval * 2 : options.MaxRetryInterval;
        }
    }

    /// <summary>
    /// Returns the delay before the next attempt: the longer of <paramref name="backoff"/> and the receiver's
    /// <paramref name="retryAfter"/>, at most <paramref name="maxInterval"/>, plus up to a quarter more, which derives from
    /// the callback and the attempt, so every replay waits the same delay.
    /// </summary>
    internal static TimeSpan GetRetryDelay(TimeSpan? retryAfter, TimeSpan backoff, TimeSpan maxInterval, string callbackId, int attemptNumber)
    {
        var delay = retryAfter > backoff ? retryAfter.Value : backoff;
        delay = delay < maxInterval ? delay : maxInterval;

        // FNV-1a over the callback identifier and the attempt number.
        var hash = 2166136261u;
        foreach (var character in callbackId)
        {
            hash = (hash ^ character) * 16777619u;
        }

        hash = (hash ^ (uint)attemptNumber) * 16777619u;
        return delay + delay * (hash / (double)uint.MaxValue / 4);
    }

    /// <summary>Returns the request body which delivers the operation's terminal <paramref name="response"/>.</summary>
    private DurableCallbackOutcome Format(DurableCallback callback, DurableTaskResponse response, DateTimeOffset closedAt)
    {
        var json = jsonOptions.Value.SerializerOptions;
        if (callback.Protocol == DurableCallbackProtocol.Nexus)
        {
            var body = response.Status == DurableTaskStatus.CompletedSuccessfully
                ? response.ResultType is { } type ? JsonSerializer.SerializeToUtf8Bytes(response.Result, json.GetTypeInfo(type)) : []
                : JsonSerializer.SerializeToUtf8Bytes(DurableOperationResponses.ToNexusFailure(response, httpOptions.Value), NexusJsonContext.Default.NexusFailure);
            return new(body, DurableOperationResponses.GetNexusState(response), closedAt);
        }

        var operation = DurableOperationResponses.ToOperation(callback.OperationId, response, json, httpOptions.Value);
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

        try
        {
            foreach (var (name, value) in callback.Headers)
            {
                request.Headers.TryAddWithoutValidation(name, _headers.Unprotect(value));
            }
        }
        catch (CryptographicException exception)
        {
            LogAttemptFailed(logger, exception, callback.Url, callback.OperationId);
            return new(false, "the callback's headers couldn't be unprotected, so the silo doesn't share the host's Data Protection keys", null);
        }

        if (callback.Protocol == DurableCallbackProtocol.Nexus)
        {
            request.Headers.TryAddWithoutValidation(NexusProtocol.OperationToken, callback.OperationId);
            request.Headers.TryAddWithoutValidation(NexusProtocol.OperationState, outcome.State);
            request.Headers.TryAddWithoutValidation(NexusProtocol.OperationCloseTime, outcome.ClosedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffK", CultureInfo.InvariantCulture));
        }
        else
        {
            var webhookId = this.GetPrimaryKeyString();
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            request.Headers.TryAddWithoutValidation("webhook-id", webhookId);
            request.Headers.TryAddWithoutValidation("webhook-timestamp", timestamp.ToString(CultureInfo.InvariantCulture));
            string? signature;
            try
            {
                signature = services.GetService<IDurableWebhookSigner>()?.Sign(callback, webhookId, timestamp, outcome.Body);
            }
            catch (Exception exception)
            {
                LogAttemptFailed(logger, exception, callback.Url, callback.OperationId);
                return new(false, $"the signer failed: {exception.Message}", null);
            }

            if (signature is not null)
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
            var delivered = response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Gone;
            var retryAfter = response.Headers.RetryAfter is { } header
                ? header.Delta ?? (header.Date is { } date ? date - DateTimeOffset.UtcNow : null)
                : null;
            var status = $"{(int)response.StatusCode} {response.ReasonPhrase}";
            if (!delivered)
            {
                LogAttemptRefused(logger, callback.Url, callback.OperationId, status);
            }

            return new(delivered, status, retryAfter > TimeSpan.Zero ? retryAfter : null);
        }
        catch (HttpRequestException exception)
        {
            LogAttemptFailed(logger, exception, callback.Url, callback.OperationId);
            return new(false, exception.Message, null);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            // The client's timeout elapsed.
            LogAttemptFailed(logger, exception, callback.Url, callback.OperationId);
            return new(false, exception.Message, null);
        }
    }

    [LoggerMessage(1, LogLevel.Debug, "The callback to '{Url}' for operation '{OperationId}' wasn't delivered; the receiver answered {Status}. Delivery retries it.")]
    private static partial void LogAttemptRefused(ILogger logger, Uri url, string operationId, string status);

    [LoggerMessage(2, LogLevel.Information, "The callback to '{Url}' for operation '{OperationId}' wasn't delivered. Delivery retries it.")]
    private static partial void LogAttemptFailed(ILogger logger, Exception exception, Uri url, string operationId);

    [LoggerMessage(3, LogLevel.Warning, "Delivery gave up on the callback to '{Url}' for operation '{OperationId}' after {Attempts} attempts; the last attempt received {Status}.")]
    private static partial void LogGaveUp(ILogger logger, Uri url, string operationId, int attempts, string status);

    [LoggerMessage(4, LogLevel.Error, "The outcome of operation '{OperationId}' couldn't be formatted for the callback to '{Url}', so delivery gave up. Configure the JSON options of the silos to serialize the operation's result type.")]
    private static partial void LogFormatFailed(ILogger logger, Exception exception, Uri url, string operationId);
}

/// <summary>Connects callback deliveries to the addresses which <see cref="DurableCallbackOptions.AllowAddress"/> accepts.</summary>
internal static class DurableCallbackConnections
{
    /// <summary>
    /// Resolves the host which a delivery connects to, and connects to the first address which <paramref name="allowAddress"/>
    /// accepts, so the address which the check accepted is the address which the delivery reaches.
    /// </summary>
    /// <exception cref="HttpRequestException">The host resolves to no address which <paramref name="allowAddress"/> accepts.</exception>
    public static async ValueTask<Stream> ConnectAsync(DnsEndPoint endpoint, Func<IPAddress, bool> allowAddress, CancellationToken cancellationToken)
    {
        var addresses = IPAddress.TryParse(endpoint.Host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(endpoint.Host, cancellationToken);
        var allowed = Array.FindAll(addresses, address => allowAddress(address));
        if (allowed.Length == 0)
        {
            throw new HttpRequestException($"The callback host '{endpoint.Host}' resolves to no address which deliveries connect to.");
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(allowed, endpoint.Port, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
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
