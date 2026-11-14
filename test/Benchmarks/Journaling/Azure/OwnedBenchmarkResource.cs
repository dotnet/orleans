namespace Benchmarks.Journaling.Azure;

internal sealed class OwnedBenchmarkResource(
    AzureJournalReport report,
    Func<CancellationToken, Task> create,
    Func<CancellationToken, Task> delete)
{
    private bool _owned;

    public async Task CreateAsync(CancellationToken cancellationToken)
    {
        report.Ownership = "creation-unconfirmed";
        report.Cleanup = "manual-check-required";
        // A retried create can return 409 after the original success response was lost.
        // Only an acknowledged success establishes ownership; every failure requires inspection.
        await create(cancellationToken);

        _owned = true;
        report.Ownership = "owned";
        report.Cleanup = "pending";
    }

    public async Task CleanupAsync(CancellationToken cancellationToken)
    {
        if (_owned)
        {
            report.Cleanup = "deleting";
            await delete(cancellationToken);
            _owned = false;
            report.Cleanup = "deleted";
        }
    }
}
