namespace Orleans.Diagnostics;

/// <summary>
/// Contains constants for Activity tag keys used throughout Orleans.
/// </summary>
internal static class ActivityTagKeys
{
    /// <summary>
    /// The request ID for an async enumerable operation.
    /// </summary>
    public const string AsyncEnumerableRequestId = "orleans.async_enumerable.request_id";

    /// <summary>
    /// The activation ID tag key.
    /// </summary>
    public const string ActivationId = "orleans.activation.id";

    /// <summary>
    /// The activation cause tag key (e.g., "new" or "rehydrate").
    /// </summary>
    public const string ActivationCause = "orleans.activation.cause";

    /// <summary>
    /// The deactivation reason tag key.
    /// </summary>
    public const string DeactivationReason = "orleans.deactivation.reason";

    /// <summary>
    /// The grain ID tag key.
    /// </summary>
    public const string GrainId = "orleans.grain.id";

    /// <summary>
    /// The grain type tag key.
    /// </summary>
    public const string GrainType = "orleans.grain.type";

    /// <summary>
    /// The grain type tag key.
    /// </summary>
    public const string GrainState = "orleans.grain.state";

    /// <summary>
    /// The silo ID tag key.
    /// </summary>
    public const string SiloId = "orleans.silo.id";

    /// <summary>
    /// The directory previous registration present tag key.
    /// </summary>
    public const string DirectoryPreviousRegistrationPresent = "orleans.directory.previousRegistration.present";

    /// <summary>
    /// The directory registered address tag key.
    /// </summary>
    public const string DirectoryRegisteredAddress = "orleans.directory.registered.address";

    /// <summary>
    /// The directory forwarding address tag key.
    /// </summary>
    public const string DirectoryForwardingAddress = "orleans.directory.forwarding.address";

    /// <summary>
    /// The exception type tag key.
    /// </summary>
    public const string ExceptionType = "exception.type";

    /// <summary>
    /// The exception message tag key.
    /// </summary>
    public const string ExceptionMessage = "exception.message";

    /// <summary>
    /// The placement filter type tag key.
    /// </summary>
    public const string PlacementFilterType = "orleans.placement.filter.type";

    /// <summary>
    /// The storage provider tag key.
    /// </summary>
    public const string StorageProvider = "orleans.storage.provider";

    /// <summary>
    /// The storage state name tag key.
    /// </summary>
    public const string StorageStateName = "orleans.storage.state.name";

    /// <summary>
    /// The storage state type tag key.
    /// </summary>
    public const string StorageStateType = "orleans.storage.state.type";

    /// <summary>
    /// The RPC system tag key.
    /// </summary>
    public const string RpcSystem = "rpc.system";

    /// <summary>
    /// The RPC service tag key.
    /// </summary>
    public const string RpcService = "rpc.service";

    /// <summary>
    /// The RPC method tag key.
    /// </summary>
    public const string RpcMethod = "rpc.method";

    /// <summary>
    /// The RPC Orleans target ID tag key.
    /// </summary>
    public const string RpcOrleansTargetId = "rpc.orleans.target_id";

    /// <summary>
    /// The RPC Orleans source ID tag key.
    /// </summary>
    public const string RpcOrleansSourceId = "rpc.orleans.source_id";

    /// <summary>
    /// The exception stacktrace tag key.
    /// </summary>
    public const string ExceptionStacktrace = "exception.stacktrace";

    /// <summary>
    /// The exception escaped tag key.
    /// </summary>
    public const string ExceptionEscaped = "exception.escaped";

    /// <summary>
    /// Indicates whether a rehydration attempt was ignored.
    /// </summary>
    public const string RehydrateIgnored = "orleans.rehydrate.ignored";

    /// <summary>
    /// The reason why a rehydration attempt was ignored.
    /// </summary>
    public const string RehydrateIgnoredReason = "orleans.rehydrate.ignored.reason";

    /// <summary>
    /// The previous registration address during rehydration.
    /// </summary>
    public const string RehydratePreviousRegistration = "orleans.rehydrate.previousRegistration";

    /// <summary>
    /// The target silo address for migration.
    /// </summary>
    public const string MigrationTargetSilo = "orleans.migration.target.silo";
}

