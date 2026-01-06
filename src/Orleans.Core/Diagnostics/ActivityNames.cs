namespace Orleans.Runtime;

public static class ActivityNames
{
    public static string PlaceGrain = "place grain";
    public static string FilterPlacementCandidates = "filter placement candidates";
    public static string ActivateGrain = "activate grain";
    public static string OnActivate = "execute OnActivateAsync";
    public static string RegisterDirectoryEntry = "register directory entry";
    public static string StorageRead = "read storage";
    public static string StorageWrite = "write storage";
    public static string StorageClear = "clear storage";
    public static string ActivationDehydrate = "dehydrate activation";
    public static string ActivationRehydrate = "rehydrate activation";
}
