namespace Orleans.Journaling;

internal static class AzureBlobJournalStorageLayout
{
    private const string WalPrefix = "wal/";

    public static string GetWalBlobName(string journalId) => WalPrefix + journalId;

    public static string GetCheckpointBlobName(string journalId, string snapshotId)
        => $"checkpoints/{journalId}/{snapshotId}";

    public static bool TryGetJournalId(string blobName, out JournalId journalId)
    {
        if (blobName.StartsWith(WalPrefix, StringComparison.Ordinal)
            && blobName[WalPrefix.Length..] is { } value
            && !string.IsNullOrWhiteSpace(value))
        {
            journalId = new JournalId(value);
            return true;
        }

        journalId = default;
        return false;
    }
}
