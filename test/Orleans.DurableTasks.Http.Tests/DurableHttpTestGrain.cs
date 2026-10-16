using Orleans.DurableTasks;
using Orleans.Journaling;

namespace Orleans.DurableTasks.Http.Tests;

public interface IDurableHttpTestGrain : IGrainWithStringKey
{
    DurableTask<string> EchoAsync(string value);

    DurableTask<string> WaitForApprovalAsync(string item);

    DurableTask<string> WaitForShipmentAsync(string item);

    DurableTask<string> EchoLaterAsync(string value);

    DurableTask<string> FailLaterAsync(string value);

    DurableTask CompleteLaterAsync(string value);
}

/// <summary>Runs the durable tasks which the HTTP endpoints under test start.</summary>
[GrainType("durable-http-test")]
public sealed class DurableHttpTestGrain : DurableGrain, IDurableHttpTestGrain
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
}

public sealed record EchoRequest(string Value);

public sealed record ApprovalRequest(string Item);

public sealed record NexusApprovalInput(string OrderId, string Item);
