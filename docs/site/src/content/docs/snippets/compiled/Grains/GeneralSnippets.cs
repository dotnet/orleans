using Microsoft.Extensions.Hosting;
using Orleans;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Serialization;

// <generate_external_contract>
[assembly: GenerateCodeForDeclaringAssembly(
    typeof(ExternalContract))]
// </generate_external_contract>

public sealed class ExternalContract;

namespace Documentation.Grains.CodeGeneration
{
    // <serializable_purchase_order>
[GenerateSerializer]
public sealed class PurchaseOrder
{
    [Id(0)]
    public required string OrderId { get; init; }

    [Id(1)]
    public decimal Total { get; init; }
}
    // </serializable_purchase_order>
}

namespace Documentation.Grains.ExternalTasks
{
    internal sealed class Item;

    internal interface IRepository
    {
        Task<Item> Load();
    }

    internal sealed class ExternalTaskExamples(IRepository repository)
    {
        private Item? _cachedItem;
        private int _lastCompressedSize;

        // <refresh_from_repository>
public async Task Refresh()
{
    Item value = await repository.Load();
    _cachedItem = value;
}
        // </refresh_from_repository>

        // <compress_on_thread_pool>
public async Task<int> Compress(byte[] input)
{
    byte[] copy = input.ToArray();

    int size = await Task.Run(
        () => CompressSynchronously(copy));

    _lastCompressedSize = size;
    return size;
}
        // </compress_on_thread_pool>

        private static int CompressSynchronously(byte[] input) => input.Length;

        private static Task WorkerAsync() => Task.CompletedTask;

        internal static async Task StartWorker()
        {
            // <start_async_worker>
Task work = Task.Factory
    .StartNew(WorkerAsync)
    .Unwrap();

await work;
            // </start_async_worker>
        }
    }
}

namespace Documentation.Grains.Extensions
{
    // <diagnostics_extension>
public interface IDiagnosticsExtension : IGrainExtension
{
    ValueTask<string> GetStatus();
}

public sealed class DiagnosticsExtension(
    IGrainContext grainContext) : IDiagnosticsExtension
{
    public ValueTask<string> GetStatus()
    {
        return ValueTask.FromResult(
            $"Active grain: {grainContext.GrainId}");
    }
}
    // </diagnostics_extension>

    public interface IUserGrain : IGrainWithStringKey;

    internal static class ExtensionExamples
    {
        internal static void Configure(ISiloBuilder siloBuilder)
        {
            // <register_diagnostics_extension>
siloBuilder.AddGrainExtension<
    IDiagnosticsExtension,
    DiagnosticsExtension>();
            // </register_diagnostics_extension>
        }

        internal static async Task Use(
            IGrainFactory grainFactory)
        {
            // <use_diagnostics_extension>
IUserGrain user =
    grainFactory.GetGrain<IUserGrain>("user-42");

IDiagnosticsExtension diagnostics =
    user.AsReference<IDiagnosticsExtension>();

string status = await diagnostics.GetStatus();
            // </use_diagnostics_extension>
        }
    }
}
