using System.Diagnostics;

namespace Orleans.Diagnostics;

public static class ActivitySources
{
    /// <summary>
    /// Spans triggered from application level code
    /// </summary>
    public const string ApplicationGrainActivitySourceName = "Microsoft.Orleans.Application";
    /// <summary>
    /// Spans triggered from Orleans runtime code
    /// </summary>
    public const string RuntimeActivitySourceName = "Microsoft.Orleans.Runtime";
    /// <summary>
    /// Spans tied to lifecycle operations such as activation, migration, and deactivation.
    /// </summary>
    public const string LifecycleActivitySourceName = "Microsoft.Orleans.Lifecycle";
    /// <summary>
    /// Spans tied to persistent storage operations.
    /// </summary>
    public const string StorageActivitySourceName = "Microsoft.Orleans.Storage";
    /// <summary>
    /// A wildcard name to match all Orleans activity sources.
    /// </summary>
    public const string AllActivitySourceName = "Microsoft.Orleans.*";

    internal static readonly ActivitySource ApplicationGrainSource = new(ApplicationGrainActivitySourceName, "1.1.0");
    internal static readonly ActivitySource RuntimeGrainSource = new(RuntimeActivitySourceName, "2.0.0");
    internal static readonly ActivitySource LifecycleGrainSource = new(LifecycleActivitySourceName, "1.0.0");
    internal static readonly ActivitySource StorageGrainSource = new(StorageActivitySourceName, "1.0.0");
}
