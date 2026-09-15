namespace DurableJobsJournaling;

internal enum StorageBackend
{
    Azurite,
    AzuriteTable,
    StandardBlob,
    PremiumBlob,
    Table
}

internal static class StorageBackendConfiguration
{
    public static StorageBackend Parse(string value) => value.ToUpperInvariant() switch
    {
        "AZURITE" => StorageBackend.Azurite,
        "AZURITETABLE" => StorageBackend.AzuriteTable,
        "STANDARDBLOB" => StorageBackend.StandardBlob,
        "PREMIUMBLOB" or "AZURE" => StorageBackend.PremiumBlob,
        "TABLE" => StorageBackend.Table,
        _ => throw new InvalidOperationException("Playground:Storage:Provider must be Azurite, AzuriteTable, StandardBlob, PremiumBlob, Table, or Azure (PremiumBlob).")
    };

    public static bool IsEmulator(this StorageBackend backend) => backend is StorageBackend.Azurite or StorageBackend.AzuriteTable;
    public static bool UsesTableJournal(this StorageBackend backend) => backend is StorageBackend.Table or StorageBackend.AzuriteTable;
}
