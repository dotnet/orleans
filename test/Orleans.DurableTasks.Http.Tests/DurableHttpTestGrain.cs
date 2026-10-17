using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Http;
using Orleans.Journaling;

namespace Orleans.DurableTasks.Http.Tests;

public interface IDurableHttpTestGrain : IGrainWithStringKey
{
    DurableTask<string> EchoAsync(string value);

    DurableTask<string> WaitForApprovalAsync(string item);

    DurableTask<string> WaitForShipmentAsync(string item);

    DurableTask<string[]> SendIdempotencyKeyAsync();

    DurableTask<string> EchoLaterAsync(string value);

    DurableTask<string> FailLaterAsync(string value);

    DurableTask CompleteLaterAsync(string value);

    DurableTask<string> RunRemoteOperationAsync(string path, string value, DurableHttpWaitMode mode);

    DurableTask<string> ApproveRemoteOperationAsync(string orderId, string approver);

    DurableTask<string> CancelRemoteOperationAsync(string orderId, string reason);

    DurableTask<string[]> SendRemoteRequestsAsync();
}

/// <summary>Runs the durable tasks which the HTTP endpoints under test start.</summary>
[GrainType("durable-http-test")]
public sealed class DurableHttpTestGrain(
    IHttpClientFactory httpClients,
    [FromKeyedServices(DurableHttpFixture.ApiClientName)] IDurableHttpClient api) : DurableGrain, IDurableHttpTestGrain
{
    public static readonly DurableSignal<string> Approved = new("approved");
    public static readonly DurableSignal<string> Shipped = new("shipped");

    public DurableTask<string> EchoAsync(string value) => DurableTask.FromResult(value);

    /// <summary>Waits for an approver, failing when the approver is <c>reject</c>.</summary>
    public async DurableTask<string> WaitForApprovalAsync(string item)
    {
        string approver;
        try
        {
            approver = await Approved.WaitAsync();
        }
        catch (OperationCanceledException) when (DurableTask.Current.TaskCancellationReason is { } reason)
        {
            throw new OperationCanceledException($"{item} was canceled: {reason}");
        }

        return approver == "reject" ? throw new InvalidOperationException($"{item} was rejected.") : $"{item} approved by {approver}";
    }

    /// <summary>Waits for an approver, then for a carrier.</summary>
    public async DurableTask<string> WaitForShipmentAsync(string item)
    {
        var approver = await Approved.WaitAsync();
        var carrier = await Shipped.WaitAsync();
        return $"{item} approved by {approver}, shipped by {carrier}";
    }

    /// <summary>Returns <paramref name="value"/> after a durable delay, so an HTTP client observes a running operation.</summary>
    public async DurableTask<string> EchoLaterAsync(string value)
    {
        await DurableTask.Delay(TimeSpan.FromMilliseconds(100));
        return value;
    }

    /// <summary>Completes without a result after a durable delay.</summary>
    public async DurableTask CompleteLaterAsync(string value) => await DurableTask.Delay(TimeSpan.FromMilliseconds(100));

    /// <summary>Fails after a durable delay.</summary>
    public async DurableTask<string> FailLaterAsync(string value)
    {
        await DurableTask.Delay(TimeSpan.FromMilliseconds(100));
        throw new InvalidOperationException($"{value} failed.");
    }

    /// <summary>Starts the operation at <paramref name="path"/> and durably waits for its result.</summary>
    public DurableTask<string> RunRemoteOperationAsync(string path, string value, DurableHttpWaitMode mode) =>
        api.RunOperationAsync<string>(
            () => new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(new EchoRequest(value)) },
            new DurableHttpOperationOptions { Mode = mode, PollInterval = TimeSpan.FromMilliseconds(100) });

    /// <summary>
    /// Starts an approval of the order, reads its running status, approves it with <paramref name="approver"/>, and
    /// returns its result.
    /// </summary>
    public async DurableTask<string> ApproveRemoteOperationAsync(string orderId, string approver)
    {
        var operation = await StartApprovalAsync(orderId);
        var status = await api.GetOperationAsync(operation.Location!);
        await api.SignalOperationAsync(operation.Location!, Approved.Name, approver);
        var result = await api.WaitForOperationAsync<string>(operation, new() { Mode = DurableHttpWaitMode.Events });
        return $"{status.Status}: {result}";
    }

    /// <summary>
    /// Starts an approval of the order, cancels it with <paramref name="reason"/>, and returns the error with which its
    /// wait fails.
    /// </summary>
    public async DurableTask<string> CancelRemoteOperationAsync(string orderId, string reason)
    {
        var operation = await StartApprovalAsync(orderId);
        var cancel = await api.CancelOperationAsync(operation.Location!, reason);
        try
        {
            await api.WaitForOperationAsync<string>(operation, new() { PollInterval = TimeSpan.FromMilliseconds(100) });
            return "completed";
        }
        catch (DurableHttpOperationException exception)
        {
            return $"{cancel.Status}: {exception.Status} {exception.Code} {exception.Message}";
        }
    }

    /// <summary>
    /// Sends a <c>POST</c> and a <c>GET</c> request with <see cref="IDurableHttpClient.SendAsync"/>, and returns the
    /// idempotency keys which the server received.
    /// </summary>
    public async DurableTask<string[]> SendRemoteRequestsAsync()
    {
        var post = await api.SendAsync(() => new HttpRequestMessage(HttpMethod.Post, "/echo-idempotency-key")).WithId("post");
        var get = await api.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, "/echo-idempotency-key")).WithId("get");
        return [post.EnsureSuccessStatusCode().ReadAsString(), get.EnsureSuccessStatusCode().ReadAsString()];
    }

    private DurableTask<DurableHttpOperation> StartApprovalAsync(string orderId) =>
        api.StartOperationAsync(() => new HttpRequestMessage(HttpMethod.Post, $"/orders/{orderId}/approval")
        {
            Content = JsonContent.Create(new ApprovalRequest("widget")),
        });
    /// <summary>
    /// Sends a <c>POST</c> and a <c>GET</c> request from a step, and returns the step's identifier and the idempotency keys
    /// which the server received.
    /// </summary>
    public async DurableTask<string[]> SendIdempotencyKeyAsync() =>
        await DurableTask.Run(async cancellationToken =>
        {
            var client = httpClients.CreateClient(DurableHttpFixture.RawClientName);
            var taskId = DurableTask.Current.Id.ToString();
            using var post = await client.PostAsync("/echo-idempotency-key", content: null, cancellationToken);
            var postKey = await post.Content.ReadAsStringAsync(cancellationToken);
            var getKey = await client.GetStringAsync("/echo-idempotency-key", cancellationToken);
            return new[] { taskId, postKey, getKey };
        }).WithId("send");
}

public sealed record EchoRequest(string Value);

public sealed record ApprovalRequest(string Item);

public sealed record NexusApprovalInput(string OrderId, string Item);
