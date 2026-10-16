using System.Net;
using System.Text.Json;

namespace Orleans.DurableTasks.Http;

/// <summary>Identifies an HTTP operation which a durable task started with <see cref="IDurableHttpClient.StartOperationAsync"/>.</summary>
/// <param name="Location">
/// The URL which reports the operation: its status monitor, or the <c>Location</c> of a server which answers
/// <c>202 Accepted</c> until it returns the result. It is <see langword="null"/> for an operation which the start
/// response completed without one.
/// </param>
/// <param name="IsStatusMonitor">
/// Whether <paramref name="Location"/> is a status monitor, which <c>Operation-Location</c> or <c>Azure-AsyncOperation</c>
/// names and which reports the operation's <c>status</c>, <c>result</c>, and <c>error</c>.
/// </param>
/// <param name="Response">The response which started the operation.</param>
[GenerateSerializer, Immutable]
[Alias("Orleans.DurableTasks.Http.DurableHttpOperation")]
public sealed record DurableHttpOperation(Uri? Location, bool IsStatusMonitor, DurableHttpResponse Response)
{
    /// <summary>Gets whether the start response completed the operation, with its result or its failure.</summary>
    /// <remarks>
    /// A <c>202 Accepted</c> response reports a running operation, as does any response with a status monitor other than
    /// <c>200 OK</c>, <c>204 No Content</c>, and <c>424 Failed Dependency</c>, such as an Azure <c>201 Created</c>
    /// response whose resource the operation still provisions.
    /// </remarks>
    public bool IsCompleted => Response.StatusCode switch
    {
        HttpStatusCode.Accepted => false,
        HttpStatusCode.OK or HttpStatusCode.NoContent or HttpStatusCode.FailedDependency => true,
        _ => !IsStatusMonitor,
    };

    /// <summary>Reads the operation which a start response identifies.</summary>
    internal static DurableHttpOperation FromStart(DurableHttpResponse response)
    {
        // 424 Failed Dependency reports an operation which failed or was canceled with its status monitor.
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.FailedDependency)
        {
            throw response.CreateException();
        }

        if (Locate(response, DurableHttpHeaders.OperationLocation) is { } monitor)
        {
            return new(monitor, IsStatusMonitor: true, response);
        }

        if (Locate(response, "Azure-AsyncOperation") is { } azureMonitor)
        {
            return new(azureMonitor, IsStatusMonitor: true, response);
        }

        if (response.StatusCode != HttpStatusCode.Accepted)
        {
            return new(Location: null, IsStatusMonitor: false, response);
        }

        // A Location header names the operation only in a 202 Accepted response; in a 201 Created response it names the
        // created resource.
        return new(
            Locate(response, "Location") ?? throw new HttpRequestException(
                $"'{response.RequestUri}' accepted the request without an Operation-Location, Azure-AsyncOperation, or Location header which locates its operation.",
                inner: null,
                response.StatusCode),
            IsStatusMonitor: false,
            response);

        static Uri? Locate(DurableHttpResponse response, string header) =>
            response.GetHeader(header) is { Length: > 0 } value
                ? response.RequestUri is { } requestUri ? new Uri(requestUri, value) : new Uri(value, UriKind.Absolute)
                : null;
    }
}

/// <summary>Selects how <see cref="IDurableHttpClient.WaitForOperationAsync{TResult}"/> observes a running operation.</summary>
public enum DurableHttpWaitMode
{
    /// <summary>
    /// Polls the operation, with a durable delay before each poll. Each poll records a delay and a step, and no request
    /// stays open between polls, which suits operations of any duration.
    /// </summary>
    Poll,

    /// <summary>
    /// Reads the server-sent events of the operation's status monitor, at <c>{status monitor}/events</c>, within one step
    /// which reconnects until the operation is terminal. The task records one step and learns the outcome as soon as the
    /// server reports it, while a request stays open for the operation's duration. An operation without a status monitor
    /// is polled.
    /// </summary>
    Events,
}

/// <summary>Configures how a durable task waits for an HTTP operation.</summary>
public sealed record DurableHttpOperationOptions
{
    internal static DurableHttpOperationOptions Default { get; } = new();

    /// <summary>Gets how the task observes the running operation. The default is <see cref="DurableHttpWaitMode.Poll"/>.</summary>
    public DurableHttpWaitMode Mode { get; init; }

    /// <summary>
    /// Gets the interval between polls of a running operation whose response doesn't advertise one with
    /// <c>Retry-After</c>. The default is five seconds.
    /// </summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets the wait which each poll requests with a <c>Prefer: wait</c> header, so a server which long-polls answers once
    /// the operation completes; <see cref="TimeSpan.Zero"/>, the default, requests none.
    /// </summary>
    public TimeSpan Wait { get; init; }
}

/// <summary>Configures an <see cref="IDurableHttpClient"/>.</summary>
public sealed class DurableHttpClientOptions
{
    internal static JsonSerializerOptions WebSerializerOptions { get; } = new Microsoft.AspNetCore.Http.Json.JsonOptions().SerializerOptions;

    /// <summary>
    /// Gets or sets the options which serialize request values and deserialize results. The default is ASP.NET Core's
    /// Minimal API JSON options: <see cref="JsonSerializerDefaults.Web"/>, which serialize by reflection where the
    /// application allows it.
    /// </summary>
    /// <remarks>
    /// The client resolves each type's contract from the options' <see cref="JsonSerializerOptions.TypeInfoResolver"/>. A
    /// trimmed or NativeAOT application sets options whose resolver, such as a source-generated
    /// <see cref="System.Text.Json.Serialization.JsonSerializerContext"/>, handles its request and result types.
    /// </remarks>
    public JsonSerializerOptions SerializerOptions { get; set; } = WebSerializerOptions;
}

/// <summary>Represents an HTTP operation which failed or was canceled.</summary>
[GenerateSerializer]
[Alias("Orleans.DurableTasks.Http.DurableHttpOperationException")]
public sealed class DurableHttpOperationException : Exception
{
    private DurableHttpOperationException()
    {
    }

    /// <summary>Initializes an exception for an operation which ended with <paramref name="status"/>.</summary>
    /// <param name="status">The operation's terminal status, <see cref="DurableOperationStatus.Failed"/> or <see cref="DurableOperationStatus.Canceled"/>.</param>
    /// <param name="code">The error code which the operation reported, if any.</param>
    /// <param name="message">The error message which the operation reported, if any.</param>
    public DurableHttpOperationException(DurableOperationStatus status, string? code, string? message)
        : base(string.IsNullOrEmpty(message) ? $"The operation ended as {status}." : message)
    {
        Status = status;
        Code = code;
    }

    /// <summary>Gets the operation's terminal status.</summary>
    [Id(0)]
    public DurableOperationStatus Status { get; private set; }

    /// <summary>Gets the error code which the operation reported, if any.</summary>
    [Id(1)]
    public string? Code { get; private set; }
}
