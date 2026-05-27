#nullable disable
namespace Orleans.Dashboard.Model;

/// <summary>
/// Describes a single observer/participant registered for a lifecycle stage.
/// </summary>
[GenerateSerializer]
[Immutable]
[Alias("Orleans.Dashboard.Model.LifecycleObserverInfo")]
internal sealed class LifecycleObserverInfo
{
    /// <summary>
    /// The observer name supplied when the observer subscribed to the lifecycle.
    /// </summary>
    [Id(0)]
    public string Name { get; set; }

    /// <summary>
    /// The type of the underlying observer (typically the concrete
    /// <see cref="ILifecycleObserver"/> implementation, or the type that owns
    /// the registered delegate when subscribing via delegate-based extensions).
    /// </summary>
    [Id(1)]
    public string ObserverType { get; set; }

    /// <summary>
    /// True when the observer has a non-default <c>OnStart</c> action.
    /// </summary>
    [Id(2)]
    public bool HasOnStart { get; set; }

    /// <summary>
    /// True when the observer has a non-default <c>OnStop</c> action.
    /// </summary>
    [Id(3)]
    public bool HasOnStop { get; set; }

    /// <summary>
    /// The fully-qualified method invoked on start (when known).
    /// </summary>
    [Id(4)]
    public string OnStartMethod { get; set; }

    /// <summary>
    /// The fully-qualified method invoked on stop (when known).
    /// </summary>
    [Id(5)]
    public string OnStopMethod { get; set; }
}
