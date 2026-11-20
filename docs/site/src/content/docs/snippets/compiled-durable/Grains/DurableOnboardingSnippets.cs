using Azure.Storage.Blobs;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Configuration;
using Orleans.DurableTasks;
using Orleans.Hosting;
using Orleans.Journaling;

namespace Documentation.Grains.DurableOnboarding;

// These contracts mirror samples/DurableWorkflows/DurableWorkflows.Service/Contracts.cs.
// <durable_onboarding_contract>
public interface IWorkflowGrain : IGrainWithStringKey
{
    DurableTask<OrderWorkflowResult> RunOrderSagaAsync(OrderRequest request);
}

[GenerateSerializer, Immutable]
public sealed record OrderRequest(
    [property: Id(0)] string OrderId,
    [property: Id(1)] bool FailShipping);

[GenerateSerializer]
public enum OrderWorkflowStatus
{
    Completed,
    Compensated
}

[GenerateSerializer, Immutable]
public sealed record OrderWorkflowResult(
    [property: Id(0)] string OrderId,
    [property: Id(1)] OrderWorkflowStatus Status,
    [property: Id(2)] string[] Compensations,
    [property: Id(3)] string? Failure);
// </durable_onboarding_contract>

internal static class OrderClient
{
    internal static async Task<OrderWorkflowResult> SubmitAsync(
        IClusterClient client, string orderId, CancellationToken requestAborted)
    {
        // <durable_onboarding_submit>
        var workflow = client.GetGrain<IWorkflowGrain>(orderId);
        var scheduled = await workflow
            .RunOrderSagaAsync(new(orderId, FailShipping: false))
            .WithId($"order-{orderId}")
            .ScheduleAsync(requestAborted);

        return await scheduled.WaitAsync(requestAborted);
        // </durable_onboarding_submit>
    }

    internal static async Task<OrderWorkflowResult> ObserveAsync(
        IClusterClient client, string orderId, CancellationToken requestAborted)
    {
        // <durable_onboarding_observe>
        var workflow = client.GetGrain<IWorkflowGrain>(orderId);
        var retained = workflow.GetDurableTask<OrderWorkflowResult>($"order-{orderId}");
        return await retained.WaitAsync(requestAborted);
        // </durable_onboarding_observe>
    }

    internal static void ConfigureClient(IClientBuilder clientBuilder)
    {
        // <durable_onboarding_client>
        clientBuilder.AddDurableTasks();
        // </durable_onboarding_client>
    }
}

internal static class OrderHosting
{
    internal static void ConfigureSilo(ISiloBuilder siloBuilder)
    {
        // <durable_onboarding_hosting>
        siloBuilder
            .AddJournaling()
            .UseAzureBlobDurableJobs(options => options.ContainerName = "durable-workflows")
            .Configure<JournaledStateManagerOptions>(options => options.JournalFormatKey = "orleans-binary")
            .AddDurableTasks(options => options.ResultRetentionPeriod = TimeSpan.FromHours(24));

        siloBuilder.Services.AddOptions<AzureBlobJournalStorageOptions>()
            .Configure<BlobServiceClient>((options, client) => options.BlobServiceClient = client);
        // </durable_onboarding_hosting>
    }
}
