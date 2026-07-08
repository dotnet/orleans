namespace Orleans.Runtime.Dissemination;

internal interface IDisseminationService
{
    ValueTask<bool> Publish(
        IDisseminationNamespace disseminationNamespace,
        DisseminationValue value,
        CancellationToken cancellationToken);
}
