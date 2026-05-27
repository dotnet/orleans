#nullable disable
namespace Orleans.Dashboard.Model;

/// <summary>
/// Describes a single lifecycle stage along with the observers that participate in it.
/// </summary>
[GenerateSerializer]
[Immutable]
[Alias("Orleans.Dashboard.Model.LifecycleStageInfo")]
internal sealed class LifecycleStageInfo
{
    /// <summary>
    /// The numeric stage value.
    /// </summary>
    [Id(0)]
    public int Stage { get; set; }

    /// <summary>
    /// A human-readable stage name. When the stage corresponds to a named
    /// <see cref="ServiceLifecycleStage"/> field, this contains the field name
    /// and value (e.g. "RuntimeInitialize (2000)"). Otherwise this is the
    /// numeric stage value as a string.
    /// </summary>
    [Id(1)]
    public string StageName { get; set; }

    /// <summary>
    /// Indicates whether the stage corresponds to a named field on
    /// <see cref="ServiceLifecycleStage"/>.
    /// </summary>
    [Id(2)]
    public bool IsNamedStage { get; set; }

    /// <summary>
    /// The observers that participate in this stage.
    /// </summary>
    [Id(3)]
    public LifecycleObserverInfo[] Observers { get; set; }
}
