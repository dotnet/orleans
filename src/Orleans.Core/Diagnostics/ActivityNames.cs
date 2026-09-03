namespace Orleans.Runtime;

/// <summary>
/// Defines names for activities emitted by the Orleans runtime.
/// </summary>
public static class ActivityNames
{
    /// <summary>
    /// The name of the activity emitted while selecting a silo for a grain.
    /// </summary>
    public const string PlaceGrain = "place grain";

    /// <summary>
    /// The name of the activity emitted while filtering placement candidates.
    /// </summary>
    public const string FilterPlacementCandidates = "filter placement candidates";

    /// <summary>
    /// The name of the activity emitted while activating a grain.
    /// </summary>
    public const string ActivateGrain = "activate grain";

    /// <summary>
    /// The name of the activity emitted while deactivating a grain.
    /// </summary>
    public const string DeactivateGrain = "deactivate grain";

    /// <summary>
    /// The name of the activity emitted while invoking a grain's activation callback.
    /// </summary>
    public const string OnActivate = "execute OnActivateAsync";

    /// <summary>
    /// The name of the activity emitted while invoking a grain's deactivation callback.
    /// </summary>
    public const string OnDeactivate = "execute OnDeactivateAsync";

    /// <summary>
    /// The name of the activity emitted while registering a grain activation with the grain directory.
    /// </summary>
    public const string RegisterDirectoryEntry = "register directory entry";

    /// <summary>
    /// The name of the activity emitted while reading grain state from storage.
    /// </summary>
    public const string StorageRead = "read storage";

    /// <summary>
    /// The name of the activity emitted while writing grain state to storage.
    /// </summary>
    public const string StorageWrite = "write storage";

    /// <summary>
    /// The name of the activity emitted while clearing grain state from storage.
    /// </summary>
    public const string StorageClear = "clear storage";

    /// <summary>
    /// The name of the activity emitted while dehydrating a grain activation for migration.
    /// </summary>
    public const string ActivationDehydrate = "dehydrate activation";

    /// <summary>
    /// The name of the activity emitted while rehydrating a migrated grain activation.
    /// </summary>
    public const string ActivationRehydrate = "rehydrate activation";

    /// <summary>
    /// The name of the activity emitted while waiting for a grain migration to complete.
    /// </summary>
    public const string WaitMigration = "wait migration";
}
