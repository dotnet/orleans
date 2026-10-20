using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;

namespace Orleans.DurableTasks.Http;

/// <summary>
/// Adds an <c>Idempotency-Key</c> header which identifies the running durable task to each request that a durable task
/// sends, so every attempt and replay of the task sends the same key.
/// </summary>
/// <remarks>
/// <para>
/// The key is a hash of the service, the grain which runs the task, the task's identifier, and its
/// <see cref="DurableTaskContext.Incarnation"/>: 43 URL-safe base64 characters which are equal for every attempt and
/// replay of one run of the task, and distinct for every other task, including a later run of the same task identifier,
/// whose earlier keys a server may still hold. A step which sends one request,
/// such as <c>DurableTask.Run(ct =&gt; client.PostAsJsonAsync(...))</c>, therefore makes that request idempotent at
/// servers which honor the header, including a retry of the step after a failure or a deactivation. A step which sends
/// several requests to different operations gives each its own step, such as with
/// <see cref="DurableTaskExtensions.WithId{TResult}(DurableTask{TResult}, string)"/>, or sets their keys itself.
/// </para>
/// <para>
/// A request with a safe method, such as <c>GET</c>, a request which already carries the header, and a request which code
/// outside a durable task sends are unchanged. Register the
/// handler before handlers which retry requests, so their retries carry the key.
/// </para>
/// </remarks>
public sealed class DurableIdempotencyKeyHandler(IGrainContextAccessor grainContextAccessor, IOptions<ClusterOptions> clusterOptions)
    : DelegatingHandler
{
    private readonly string _serviceId = clusterOptions.Value.ServiceId;

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        AddKey(request);
        return base.SendAsync(request, cancellationToken);
    }

    /// <inheritdoc />
    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        AddKey(request);
        return base.Send(request, cancellationToken);
    }

    private void AddKey(HttpRequestMessage request)
    {
        // Safe methods are idempotent by definition, so their requests carry no key.
        if (request.Method == HttpMethod.Get || request.Method == HttpMethod.Head || request.Method == HttpMethod.Options || request.Method == HttpMethod.Trace
            || request.Headers.Contains(DurableHttpHeaders.IdempotencyKey)
            || !DurableTaskContext.TryGetCurrent(out var task))
        {
            return;
        }

        // Task identifiers are unique within their grain, so the key covers the grain which runs the task.
        var grainId = grainContextAccessor.GrainContext.GrainId;
        if (grainId.IsClient())
        {
            throw new InvalidOperationException(
                $"Durable task '{task.Id}' sent a request after leaving its grain's scheduler, for example after ConfigureAwait(false), so the request's idempotency key can't identify the grain which runs the task. Send the request from the task's step before leaving the scheduler.");
        }

        request.Headers.TryAddWithoutValidation(DurableHttpHeaders.IdempotencyKey, CreateKey(_serviceId, grainId, task.Id, task.Incarnation));
    }

    internal static string CreateKey(string serviceId, GrainId grainId, TaskId taskId, Guid incarnation)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, serviceId);
        Append(hash, grainId.ToString());
        Append(hash, taskId.ToString());
        Append(hash, incarnation.ToString("N"));
        return WebEncoders.Base64UrlEncode(hash.GetHashAndReset());

        static void Append(IncrementalHash hash, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            Span<byte> length = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
    }
}
