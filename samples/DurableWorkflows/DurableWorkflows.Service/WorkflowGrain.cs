using System.Distributed.DurableTasks;
using Orleans.Journaling;
using Orleans.Runtime;

namespace DurableWorkflows;

public sealed class WorkflowGrain(ILocalSiloDetails localSiloDetails) : DurableGrain, IWorkflowGrain
{
    private readonly Guid _activationId = Guid.NewGuid();

    public async DurableTask<BasicWorkflowResult> RunBasicAsync(string input)
    {
        var workflowId = this.GetPrimaryKeyString();
        var validated = await GrainFactory.GetGrain<IActivityGrain>($"{workflowId}-validate")
            .ExecuteAsync(new($"{workflowId}:validate", input, false))
            .WithId("validate");
        var stored = await GrainFactory.GetGrain<IActivityGrain>($"{workflowId}-store")
            .ExecuteAsync(new($"{workflowId}:store", validated.Output, false))
            .WithId("store");

        return new(workflowId, stored.Output);
    }

    public async DurableTask<FanOutWorkflowResult> RunFanOutAsync(string[] items)
    {
        ArgumentOutOfRangeException.ThrowIfZero(items.Length);
        var workflowId = this.GetPrimaryKeyString();
        var branches = new ScheduledTask<ActivityResult>[items.Length];
        for (var index = 0; index < items.Length; index++)
        {
            branches[index] = await GrainFactory.GetGrain<IActivityGrain>($"{workflowId}-fanout-{index}")
                .ExecuteAsync(new($"{workflowId}:fanout:{index}", items[index], false))
                .WithId($"branch-{index}")
                .ScheduleAsync();
        }

        await ScheduledTask.WhenAll(branches);
        return new(workflowId, items.Length, branches.Select(static task => task.Id.ToString()).ToArray());
    }

    public async DurableTask<ApprovalWorkflowResult> RunApprovalAsync(ApprovalRequest request)
    {
        if (!string.Equals(request.CorrelationId, this.GetPrimaryKeyString(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The approval correlation id must match the workflow grain id.");
        }

        var decision = await GrainFactory.GetGrain<IApprovalGrain>(request.CorrelationId)
            .WaitForDecisionAsync(request.CorrelationId)
            .WithId("human-approval");
        var runtime = CreateRuntimeInfo();
        return new(
            this.GetPrimaryKeyString(),
            request.CorrelationId,
            request.Subject,
            decision.Approved,
            decision.Reason,
            runtime.SiloAddress,
            runtime.ActivationId);
    }

    public async DurableTask<CancellationWorkflowResult> RunCancellationAsync(string cancellationId)
    {
        if (!string.Equals(cancellationId, this.GetPrimaryKeyString(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The cancellation id must match the workflow grain id.");
        }

        var signal = await GrainFactory.GetGrain<ICancellationGrain>(cancellationId)
            .WaitForCancellationAsync(cancellationId)
            .WithId("cancellation-signal");
        var runtime = CreateRuntimeInfo();
        return new(
            this.GetPrimaryKeyString(),
            signal.CancellationId,
            Canceled: true,
            signal.Reason,
            runtime.SiloAddress,
            runtime.ActivationId);
    }

    public async DurableTask<OrderWorkflowResult> RunOrderSagaAsync(OrderRequest request)
    {
        var inventory = GrainFactory.GetGrain<IInventoryGrain>(request.OrderId);
        var payment = GrainFactory.GetGrain<IPaymentGrain>(request.OrderId);
        var reserved = false;
        var charged = false;

        try
        {
            _ = await inventory.ReserveAsync($"{request.OrderId}:reserve").WithId("reserve-inventory");
            reserved = true;
            _ = await payment.ChargeAsync($"{request.OrderId}:charge").WithId("charge-payment");
            charged = true;
            _ = await GrainFactory.GetGrain<IActivityGrain>($"{request.OrderId}-shipping")
                .ExecuteAsync(new($"{request.OrderId}:ship", "ship", request.FailShipping))
                .WithId("ship");

            return new(request.OrderId, OrderWorkflowStatus.Completed, [], null);
        }
        catch (Exception exception)
        {
            var compensations = new List<string>(2);
            if (charged)
            {
                _ = await payment.RefundAsync($"{request.OrderId}:refund").WithId("refund-payment");
                compensations.Add("refund-payment");
            }

            if (reserved)
            {
                _ = await inventory.ReleaseAsync($"{request.OrderId}:release").WithId("release-inventory");
                compensations.Add("release-inventory");
            }

            return new(request.OrderId, OrderWorkflowStatus.Compensated, compensations.ToArray(), exception.Message);
        }
    }

    public Task<WorkflowRuntimeInfo> GetRuntimeInfoAsync() => Task.FromResult(CreateRuntimeInfo());

    private WorkflowRuntimeInfo CreateRuntimeInfo() =>
        new(localSiloDetails.SiloAddress.ToParsableString(), _activationId);
}
