using System.Net;
using System.Text;
using System.Text.Json;

namespace Orleans.DurableTasks.Http;

/// <summary>Records an HTTP response which a durable task received: its status code, headers, and content.</summary>
/// <param name="StatusCode">The response's status code.</param>
/// <param name="RequestUri">The URL of the request which the response answers.</param>
/// <param name="Headers">The response's headers and content headers, whose names compare case-insensitively.</param>
/// <param name="Content">The response's content.</param>
/// <remarks>
/// The task records the whole response, so replays read it without sending the request again. It suits API responses;
/// stream large content within a step of the task instead.
/// </remarks>
[GenerateSerializer, Immutable]
[Alias("Orleans.DurableTasks.Http.DurableHttpResponse")]
public sealed record DurableHttpResponse(
    HttpStatusCode StatusCode,
    Uri? RequestUri,
    IReadOnlyDictionary<string, string[]> Headers,
    ReadOnlyMemory<byte> Content)
{
    /// <summary>Gets whether <see cref="StatusCode"/> is in the range 200-299.</summary>
    public bool IsSuccessStatusCode => (int)StatusCode is >= 200 and <= 299;

    /// <summary>Throws an <see cref="HttpRequestException"/> when <see cref="IsSuccessStatusCode"/> is <see langword="false"/>.</summary>
    /// <returns>This response.</returns>
    /// <exception cref="HttpRequestException">The response doesn't have a success status code.</exception>
    public DurableHttpResponse EnsureSuccessStatusCode() =>
        IsSuccessStatusCode ? this : throw CreateException();

    /// <summary>Returns the first value of the header <paramref name="name"/>, or <see langword="null"/> when the response doesn't have it.</summary>
    /// <param name="name">The header's name.</param>
    /// <returns>The header's first value, or <see langword="null"/>.</returns>
    public string? GetHeader(string name) => Headers.TryGetValue(name, out var values) && values.Length > 0 ? values[0] : null;

    /// <summary>Returns the content decoded as UTF-8 text.</summary>
    /// <returns>The content as a string.</returns>
    public string ReadAsString() => Encoding.UTF8.GetString(Content.Span);

    /// <summary>Deserializes the content as JSON, or returns <see langword="default"/> for empty content.</summary>
    /// <typeparam name="T">The type to deserialize.</typeparam>
    /// <param name="options">The serializer options, or <see langword="null"/> for the <see cref="JsonSerializerDefaults.Web"/> defaults.</param>
    /// <returns>The deserialized value.</returns>
    public T? ReadFromJson<T>(JsonSerializerOptions? options = null) =>
        Content.IsEmpty ? default : JsonSerializer.Deserialize(Content.Span, (options ?? DurableHttpClientOptions.WebSerializerOptions).GetTypeInfo<T>());

    internal HttpRequestException CreateException() =>
        new($"'{RequestUri}' returned {(int)StatusCode} ({StatusCode}).", inner: null, StatusCode);

    internal static async Task<DurableHttpResponse> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in response.Headers.NonValidated)
        {
            headers[name] = [.. values];
        }

        foreach (var (name, values) in response.Content.Headers.NonValidated)
        {
            headers[name] = [.. values];
        }

        var content = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        return new(response.StatusCode, response.RequestMessage?.RequestUri, headers, content);
    }
}
