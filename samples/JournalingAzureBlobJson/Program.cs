using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Journaling;
using Orleans.Journaling.Json;

namespace JournalingAzureBlobJson;

internal static class Program
{
    private const string ConnectionStringEnvironmentVariable = "ORLEANS_AZURE_STORAGE_CONNECTION_STRING";
    private const string AzureWebJobsStorageEnvironmentVariable = "AzureWebJobsStorage";
    private const string DefaultContainerName = "orleans-journaling-json-sample";
    private const string DefaultBlobName = "journaled-json-sample.jsonl";

    public static async Task<int> Main(string[] args)
    {
        var settings = SampleSettings.Parse(args);
        if (string.IsNullOrWhiteSpace(settings.ConnectionString))
        {
            Console.Error.WriteLine($"Set {ConnectionStringEnvironmentVariable}, set {AzureWebJobsStorageEnvironmentVariable}, or pass --connection-string <value>.");
            return 1;
        }

        var blobServiceClient = new BlobServiceClient(settings.ConnectionString);
        var container = blobServiceClient.GetBlobContainerClient(settings.ContainerName);
        await container.CreateIfNotExistsAsync();

        var blob = container.GetAppendBlobClient(settings.BlobName);
        if (settings.ResetBlob)
        {
            await blob.DeleteIfExistsAsync();
        }

        using var host = Host.CreateDefaultBuilder(args)
            .ConfigureLogging(logging => logging.AddConsole())
            .UseOrleans(siloBuilder =>
            {
                siloBuilder
                    .UseLocalhostClustering()
                    .Configure<ClusterOptions>(options =>
                    {
                        options.ClusterId = "journaling-azure-blob-json-sample";
                        options.ServiceId = "journaling-azure-blob-json-sample";
                    })
                    .Configure<EndpointOptions>(options => options.AdvertisedIPAddress = IPAddress.Loopback)
                    .AddAzureAppendBlobLogStorage(options =>
                    {
                        options.BlobServiceClient = blobServiceClient;
                        options.ContainerName = settings.ContainerName;
                        options.GetBlobName = _ => settings.BlobName;
                        options.LogFormatKey = LogFormatKeys.Json;
                    })
                    .UseJsonCodec(JournalingSampleJsonContext.Default);
            })
            .Build();

        await host.StartAsync();

        var client = host.Services.GetRequiredService<IClusterClient>();
        var grain = client.GetGrain<IJournaledSampleGrain>("azure-json-codec");

        Console.WriteLine("Writing durable grain state...");
        var written = await grain.RunScenario();
        Console.WriteLine(JsonSerializer.Serialize(written, JournalingSampleJsonContext.Default.JournaledSampleSummary));

        Console.WriteLine("Deactivating and reactivating the grain to force recovery...");
        await grain.Deactivate();
        await Task.Delay(TimeSpan.FromMilliseconds(500));

        var recovered = await grain.GetSummary();
        ValidateRecovery(written, recovered);
        Console.WriteLine(JsonSerializer.Serialize(recovered, JournalingSampleJsonContext.Default.JournaledSampleSummary));

        Console.WriteLine();
        Console.WriteLine($"Raw JSONL blob contents from {settings.ContainerName}/{settings.BlobName}:");
        Console.WriteLine(await DownloadBlobAsText(blob));

        await host.StopAsync();
        return 0;
    }

    private static async Task<string> DownloadBlobAsText(AppendBlobClient blob)
    {
        var result = await blob.DownloadContentAsync();
        return Encoding.UTF8.GetString(result.Value.Content.ToArray());
    }

    private static void ValidateRecovery(JournaledSampleSummary written, JournaledSampleSummary recovered)
    {
        if (written.ActivationId == recovered.ActivationId)
        {
            throw new InvalidOperationException("The grain did not reactivate after DeactivateOnIdle.");
        }

        if (written.Inventory.Length != recovered.Inventory.Length
            || written.Events.Length != recovered.Events.Length
            || written.WorkQueue.Length != recovered.WorkQueue.Length
            || written.Tags.Length != recovered.Tags.Length)
        {
            throw new InvalidOperationException("Recovered collection counts do not match the state that was written.");
        }

        if (!written.Inventory.SequenceEqual(recovered.Inventory)
            || !written.Events.SequenceEqual(recovered.Events)
            || !written.WorkQueue.SequenceEqual(recovered.WorkQueue)
            || !written.Tags.SequenceEqual(recovered.Tags)
            || written.Balance != recovered.Balance
            || written.Profile != recovered.Profile
            || written.CompletionStatus != recovered.CompletionStatus
            || written.Receipt != recovered.Receipt)
        {
            throw new InvalidOperationException("Recovered state does not match the state that was written.");
        }
    }

    private sealed record SampleSettings(string? ConnectionString, string ContainerName, string BlobName, bool ResetBlob)
    {
        public static SampleSettings Parse(string[] args)
        {
            var connectionString = GetOptionValue(args, "--connection-string")
                ?? Environment.GetEnvironmentVariable(ConnectionStringEnvironmentVariable)
                ?? Environment.GetEnvironmentVariable(AzureWebJobsStorageEnvironmentVariable);

            var containerName = GetOptionValue(args, "--container") ?? DefaultContainerName;
            var blobName = GetOptionValue(args, "--blob") ?? DefaultBlobName;
            var resetBlob = !args.Contains("--no-reset", StringComparer.OrdinalIgnoreCase);

            return new(connectionString, containerName, blobName, resetBlob);
        }

        private static string? GetOptionValue(string[] args, string name)
        {
            for (var i = 0; i < args.Length; i++)
            {
                if (!string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (i + 1 == args.Length)
                {
                    throw new ArgumentException($"Missing value for {name}.");
                }

                return args[i + 1];
            }

            return null;
        }
    }
}

public interface IJournaledSampleGrain : IGrainWithStringKey
{
    Task<JournaledSampleSummary> RunScenario();
    Task<JournaledSampleSummary> GetSummary();
    Task Deactivate();
}

public sealed class JournaledSampleGrain(
    [FromKeyedServices("inventory")] IDurableDictionary<string, InventoryItem> inventory,
    [FromKeyedServices("events")] IDurableList<JournalEvent> events,
    [FromKeyedServices("work")] IDurableQueue<WorkItem> workQueue,
    [FromKeyedServices("tags")] IDurableSet<string> tags,
    [FromKeyedServices("balance")] IDurableValue<AccountBalance> balance,
    [FromKeyedServices("profile")] IPersistentState<ProfileState> profile,
    [FromKeyedServices("receipt")] IDurableTaskCompletionSource<Receipt> receipt) : DurableGrain, IJournaledSampleGrain
{
    private readonly Guid _activationId = Guid.NewGuid();

    public async Task<JournaledSampleSummary> RunScenario()
    {
        inventory.Clear();
        inventory["sku-apple"] = new InventoryItem("sku-apple", 12, 1.25m, ["fresh", "fruit"]);
        inventory["sku-orange"] = new InventoryItem("sku-orange", 9, 1.10m, ["citrus", "fruit"]);
        inventory.Remove("sku-orange");
        inventory["sku-coffee"] = new InventoryItem("sku-coffee", 3, 12.99m, ["beans", "dark-roast"]);

        events.Clear();
        events.Add(new JournalEvent("started", DateTimeOffset.UtcNow, "scenario", ["initial write"]));
        events.Add(new JournalEvent("inventory-loaded", DateTimeOffset.UtcNow, "inventory", ["dictionary set", "dictionary remove"]));
        events.Insert(1, new JournalEvent("audit-inserted", DateTimeOffset.UtcNow, "audit", ["list insert"]));
        events[0] = new JournalEvent("started-updated", DateTimeOffset.UtcNow, "scenario", ["list set"]);
        events.RemoveAt(2);
        events.Add(new JournalEvent("ready", DateTimeOffset.UtcNow, "scenario", ["list add"]));

        workQueue.Clear();
        workQueue.Enqueue(new WorkItem(Guid.NewGuid(), "ship", 10, true));
        workQueue.Enqueue(new WorkItem(Guid.NewGuid(), "email", 2, false));
        _ = workQueue.Dequeue();
        workQueue.Enqueue(new WorkItem(Guid.NewGuid(), "reconcile", 7, true));

        tags.Clear();
        tags.Add("json");
        tags.Add("azure-append-blob");
        tags.Add("temporary");
        tags.Remove("temporary");
        tags.UnionWith(["journaling", "recovery"]);

        balance.Value = new AccountBalance(
            "USD",
            42.75m,
            [
                new LedgerEntry("opening", 50.00m, "initial balance"),
                new LedgerEntry("coffee", -7.25m, "inventory adjustment")
            ]);

        profile.State = new ProfileState(
            "json-codec-sample",
            2,
            DateTimeOffset.UtcNow,
            balance.Value);

        receipt.TrySetResult(new Receipt("receipt-001", OperationCount: 24, CompletedAt: DateTimeOffset.UtcNow));

        await WriteStateAsync();
        return CreateSummary();
    }

    public Task<JournaledSampleSummary> GetSummary() => Task.FromResult(CreateSummary());

    public Task Deactivate()
    {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }

    private JournaledSampleSummary CreateSummary()
    {
        var completion = receipt.State;
        return new JournaledSampleSummary(
            _activationId,
            inventory.OrderBy(static item => item.Key, StringComparer.Ordinal)
                .Select(static item => new InventoryEntry(item.Key, item.Value))
                .ToArray(),
            events.ToArray(),
            workQueue.ToArray(),
            tags.Order(StringComparer.Ordinal).ToArray(),
            balance.Value ?? throw new InvalidOperationException("Balance has not been written."),
            profile.State,
            completion.Status,
            completion.Value);
    }
}

[GenerateSerializer]
public sealed record JournaledSampleSummary(
    [property: Id(0)] Guid ActivationId,
    [property: Id(1)] InventoryEntry[] Inventory,
    [property: Id(2)] JournalEvent[] Events,
    [property: Id(3)] WorkItem[] WorkQueue,
    [property: Id(4)] string[] Tags,
    [property: Id(5)] AccountBalance Balance,
    [property: Id(6)] ProfileState Profile,
    [property: Id(7)] DurableTaskCompletionSourceStatus CompletionStatus,
    [property: Id(8)] Receipt? Receipt);

[GenerateSerializer]
public sealed record InventoryEntry(
    [property: Id(0)] string Key,
    [property: Id(1)] InventoryItem Value);

[GenerateSerializer]
public sealed record InventoryItem(
    [property: Id(0)] string Sku,
    [property: Id(1)] int Quantity,
    [property: Id(2)] decimal UnitPrice,
    [property: Id(3)] string[] Attributes);

[GenerateSerializer]
public sealed record JournalEvent(
    [property: Id(0)] string EventId,
    [property: Id(1)] DateTimeOffset Timestamp,
    [property: Id(2)] string Kind,
    [property: Id(3)] string[] Notes);

[GenerateSerializer]
public sealed record WorkItem(
    [property: Id(0)] Guid WorkId,
    [property: Id(1)] string Kind,
    [property: Id(2)] int Priority,
    [property: Id(3)] bool Urgent);

[GenerateSerializer]
public sealed record AccountBalance(
    [property: Id(0)] string Currency,
    [property: Id(1)] decimal Amount,
    [property: Id(2)] LedgerEntry[] Ledger);

[GenerateSerializer]
public sealed record LedgerEntry(
    [property: Id(0)] string Id,
    [property: Id(1)] decimal Amount,
    [property: Id(2)] string Reason);

[GenerateSerializer]
public sealed record ProfileState(
    [property: Id(0)] string Owner,
    [property: Id(1)] int Revision,
    [property: Id(2)] DateTimeOffset UpdatedAt,
    [property: Id(3)] AccountBalance LastBalance);

[GenerateSerializer]
public sealed record Receipt(
    [property: Id(0)] string ReceiptId,
    [property: Id(1)] int OperationCount,
    [property: Id(2)] DateTimeOffset CompletedAt);

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(ulong))]
[JsonSerializable(typeof(DateTime))]
[JsonSerializable(typeof(JournaledSampleSummary))]
[JsonSerializable(typeof(InventoryEntry))]
[JsonSerializable(typeof(InventoryItem))]
[JsonSerializable(typeof(JournalEvent))]
[JsonSerializable(typeof(WorkItem))]
[JsonSerializable(typeof(AccountBalance))]
[JsonSerializable(typeof(LedgerEntry))]
[JsonSerializable(typeof(ProfileState))]
[JsonSerializable(typeof(Receipt))]
[JsonSerializable(typeof(DurableTaskCompletionSourceStatus))]
internal sealed partial class JournalingSampleJsonContext : JsonSerializerContext;
