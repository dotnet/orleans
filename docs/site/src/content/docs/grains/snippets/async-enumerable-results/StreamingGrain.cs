using System.Runtime.CompilerServices;

namespace Orleans.Docs.Snippets.AsyncEnumerableResults;

// <streaming_contract>
public interface IExportGrain : IGrainWithStringKey
{
    IAsyncEnumerable<ExportRow> ExportRows(
        CancellationToken cancellationToken = default);
}

[GenerateSerializer]
public sealed record ExportRow(
    [property: Id(0)] int RowNumber,
    [property: Id(1)] string Payload);
// </streaming_contract>

// <streaming_implementation>
public sealed class ExportGrain : Grain, IExportGrain
{
    public async IAsyncEnumerable<ExportRow> ExportRows(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (var rowNumber = 0; rowNumber < 1_000; rowNumber++)
        {
            await Task.Delay(
                TimeSpan.FromMilliseconds(10),
                cancellationToken);
            yield return new ExportRow(rowNumber, $"row-{rowNumber}");
        }
    }
}
// </streaming_implementation>
