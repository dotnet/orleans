namespace Orleans.Runtime;

public static class ActivityNames
{
    public const string PlaceGrain = "place grain";
    public const string FilterPlacementCandidates = "filter placement candidates";
    public const string ActivateGrain = "activate grain";
    public const string DeactivateGrain = "deactivate grain";
    public const string OnActivate = "execute OnActivateAsync";
    public const string OnDeactivate = "execute OnDeactivateAsync";
    public const string RegisterDirectoryEntry = "register directory entry";
    public const string StorageRead = "read storage";
    public const string StorageWrite = "write storage";
    public const string StorageClear = "clear storage";
    public const string ActivationDehydrate = "dehydrate activation";
    public const string ActivationRehydrate = "rehydrate activation";
    public const string WaitMigration = "wait migration";
}
