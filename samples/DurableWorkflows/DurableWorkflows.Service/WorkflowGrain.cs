using Orleans.DurableTasks;
using Orleans.Journaling;
using Orleans.Runtime;

namespace DurableWorkflows;

// Each workflow method is a durable task. Callers schedule it under a root ID, such as "approval-{id}", and every child
// which it awaits with WithId has a stable ID beneath that root. After a deactivation or a silo failure, the runtime
// replays the method from the start: completed children return their recorded results, so the workflow resumes where
// it stopped.
public sealed class WorkflowGrain(ILocalSiloDetails localSiloDetails) : DurableGrain, IWorkflowGrain
{
    private readonly Guid _activationId = Guid.NewGuid();

    private string WorkflowId => this.GetPrimaryKeyString();

    public async DurableTask<BasicWorkflowResult> RunBasicAsync(string input)
    {
        var workflowId = WorkflowId;
        var validated = await Activity($"{workflowId}-validate")
            .ExecuteAsync(new($"{workflowId}:validate", input))
            .WithId("validate");
        var stored = await Activity($"{workflowId}-store")
            .ExecuteAsync(new($"{workflowId}:store", validated.Output))
            .WithId("store");

        return new(workflowId, stored.Output);
    }

    // Runs one activity per item concurrently, as the children branches/0, branches/1, and so on, and returns their
    // results in item order once every branch has completed.
    public async DurableTask<FanOutWorkflowResult> RunFanOutAsync(string[] items)
    {
        ArgumentOutOfRangeException.ThrowIfZero(items.Length);
        var workflowId = WorkflowId;
        var branches = await DurableTask.WhenAll(items.Select((item, index) =>
                Activity($"{workflowId}-fanout-{index}").ExecuteAsync(new($"{workflowId}:fanout:{index}", item))))
            .WithId("branches");

        return new(workflowId, branches);
    }

    // Waits for a reviewer's decision until the request's timeout elapses. The deadline derives from the workflow's
    // durable clock, so a replay waits for the same deadline, and the task records the first decision it receives.
    public async DurableTask<ApprovalWorkflowResult> RunApprovalAsync(ApprovalRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Subject);
        var deadline = DurableTask.Current.UtcNow + request.Timeout;
        var (received, decision) = await WorkflowSignals.ApprovalDecided.TryWaitAsync(deadline).WithId("decision");
        if (!received)
        {
            decision = new(Approved: false, "No decision arrived before the deadline.");
        }

        var runtime = CreateRuntimeInfo();
        return new(WorkflowId, request.Subject, decision.Approved, decision.Reason, runtime.SiloAddress, runtime.ActivationId);
    }

    // Reserves stock and holds it for the hold period. A cancellation request during the period releases the
    // reservation and records the request's reason; the runtime persists the request, so a replay takes the same path.
    public async DurableTask<CancellationWorkflowResult> RunCancellationAsync(TimeSpan holdPeriod)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(holdPeriod, TimeSpan.Zero);
        var workflowId = WorkflowId;
        var inventory = GrainFactory.GetGrain<IInventoryGrain>(workflowId);
        try
        {
            await inventory.ReserveAsync($"{workflowId}:reserve").WithId("reserve");
            await DurableTask.Delay(holdPeriod).WithId("hold");
            return new(workflowId, Canceled: false, Reason: null);
        }
        catch (OperationCanceledException) when (DurableTask.Current.IsTaskCancellationRequested)
        {
            // A shielded child runs to completion although the workflow was canceled.
            await inventory.ReleaseAsync($"{workflowId}:release").WithId("release").Shielded();

            return new(workflowId, Canceled: true, DurableTask.Current.TaskCancellationReason);
        }
    }

    // Reserves stock and charges payment concurrently, then ships. When a step fails, the saga undoes exactly the steps
    // which succeeded. Each participant applies an operation ID once, so a replay or a repeated saga changes nothing.
    public async DurableTask<OrderWorkflowResult> RunOrderSagaAsync(OrderRequest request)
    {
        var orderId = request.OrderId;
        var inventory = GrainFactory.GetGrain<IInventoryGrain>(orderId);
        var payment = GrainFactory.GetGrain<IPaymentGrain>(orderId);

        // WhenAllCompleted waits for both steps and returns their handles, each with its own outcome.
        var prepared = await DurableTask.WhenAllCompleted(
            [inventory.ReserveAsync($"{orderId}:reserve"), payment.ChargeAsync($"{orderId}:charge")])
            .WithId("prepare");
        var (reserve, charge) = (prepared[0], prepared[1]);

        // The result names the step which failed; each step's task retains its exception for diagnostics.
        var failedStep = reserve.Exception is not null ? "reserve" : charge.Exception is not null ? "charge" : null;
        if (failedStep is null)
        {
            try
            {
                await Activity($"{orderId}-shipping")
                    .ExecuteAsync(new($"{orderId}:ship", "ship", request.FailShipping))
                    .WithId("ship");
                return new(orderId, OrderWorkflowStatus.Completed, [], null);
            }
            catch (Exception)
            {
                failedStep = "ship";
            }
        }

        List<DurableTask<SagaEffect>> compensations = [];
        if (charge.Status is DurableTaskStatus.CompletedSuccessfully)
        {
            compensations.Add(payment.RefundAsync($"{orderId}:refund"));
        }

        if (reserve.Status is DurableTaskStatus.CompletedSuccessfully)
        {
            compensations.Add(inventory.ReleaseAsync($"{orderId}:release"));
        }

        var undone = await DurableTask.WhenAll(compensations).WithId("compensate");
        return new(orderId, OrderWorkflowStatus.Compensated, [.. undone.Select(effect => effect.Kind)], $"The {failedStep} step failed.");
    }

    public Task<WorkflowRuntimeInfo> GetRuntimeInfoAsync() => Task.FromResult(CreateRuntimeInfo());

    private IActivityGrain Activity(string key) => GrainFactory.GetGrain<IActivityGrain>(key);

    private WorkflowRuntimeInfo CreateRuntimeInfo() =>
        new(localSiloDetails.SiloAddress.ToParsableString(), _activationId);
}
