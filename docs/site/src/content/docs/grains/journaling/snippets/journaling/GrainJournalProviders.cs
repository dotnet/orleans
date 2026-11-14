using Azure.Storage.Blobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;
using Orleans.Journaling;
using Orleans.Journaling.Json;

namespace Orleans.Docs.Snippets.Journaling;

public static class GrainJournalProviders
{
    public static IHost Configure(BlobServiceClient client)
    {
        // <configure_grain_providers>
        var builder = Host.CreateApplicationBuilder();
        builder.UseOrleans(silo =>
        {
            silo.UseLocalhostClustering()
                .AddAzureBlobJournalStorage(options =>
                {
                    options.BlobServiceClient = client;
                    options.ContainerName = "default-journals";
                })
                .AddAzureBlobJournalStorage("orders", options =>
                {
                    options.BlobServiceClient = client;
                    options.ContainerName = "order-journals";
                })
                .AddAzureBlobJournalStorage("audit", options =>
                {
                    options.BlobServiceClient = client;
                    options.ContainerName = "audit-journals";
                })
                .UseJsonJournalFormat(JournalJsonContext.Default);
        });

        var host = builder.Build();
        // </configure_grain_providers>
        return host;
    }
}

// <select_grain_providers>
public interface IOrderJournalGrain : IGrainWithStringKey
{
    Task<int> Increment();
}

[JournalStorageProvider("orders")]
public sealed class OrderJournalGrain : DurableGrain, IOrderJournalGrain
{
    private readonly IDurableValue<int> _count;

    public OrderJournalGrain()
    {
        _count = StateManager.GetOrAddValue<int>("count");
    }

    public async Task<int> Increment()
    {
        _count.Value++;
        await WriteStateAsync();
        return _count.Value;
    }
}

public interface IAuditJournalGrain : IGrainWithStringKey
{
    Task<int> Increment();
}

[JournalStorageProvider("audit")]
public sealed class AuditJournalGrain(
    IDurableStateManager manager,
    [FromKeyedServices("count")] IDurableValue<int> count)
    : Grain, IAuditJournalGrain
{
    public async Task<int> Increment()
    {
        count.Value++;
        await manager.WriteStateAsync();
        return count.Value;
    }
}
// </select_grain_providers>
