namespace Orleans.Runtime.Dissemination;

// Publishing announces that a version is repairable; serialized payload ownership stays with the namespace.
internal interface IDisseminationService
{
    ValueTask<bool> Publish(
        IDisseminationNamespace disseminationNamespace,
        DisseminationKey key,
        long version,
        CancellationToken cancellationToken);

    IReadOnlyList<SiloAddress> GetUnconfirmedPeers(IDisseminationNamespace disseminationNamespace);
}
