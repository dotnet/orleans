namespace Orleans.Runtime.Dissemination;

internal interface IDisseminationService
{
    ValueTask<bool> Publish(
        IDisseminationNamespace disseminationNamespace,
        DisseminationKey key,
        long version,
        CancellationToken cancellationToken);
}
