using Orleans.Runtime;

namespace Orleans.Docs.Snippets.AsyncEnumerableResults;

public static class StreamingConsumer
{
    // <consume_stream>
    public static async Task ConsumeStream(IExportGrain grain)
    {
        await foreach (var row in grain.ExportRows())
        {
            await ProcessRow(row);
        }
    }
    // </consume_stream>

    // <configure_batch_size>
    public static async Task ConsumeInBatches(IExportGrain grain)
    {
        await foreach (var row in grain.ExportRows().WithBatchSize(25))
        {
            await ProcessRow(row);
        }
    }
    // </configure_batch_size>

    // <cancel_stream>
    public static async Task ConsumeWithCancellation(
        IExportGrain grain,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var row in grain
                .ExportRows(cancellationToken)
                .WithBatchSize(25)
                .WithCancellation(cancellationToken))
            {
                await ProcessRow(row);
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            // The caller requested cancellation.
        }
    }
    // </cancel_stream>

    // <handle_interruption>
    public static async Task ConsumeWithInterruptionHandling(
        IExportGrain grain)
    {
        try
        {
            await foreach (var row in grain.ExportRows())
            {
                await ProcessRow(row);
            }
        }
        catch (EnumerationAbortedException)
        {
            // The grain deactivated or Orleans removed an idle enumerator.
        }
    }
    // </handle_interruption>

    private static Task ProcessRow(ExportRow row) => Task.CompletedTask;
}
