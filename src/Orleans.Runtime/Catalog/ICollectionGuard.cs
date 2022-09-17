namespace Orleans.Runtime;

/// <summary>
/// The guard(s) against deactivating grains.
///
/// These will be run by the <see cref="ActivationCollector"/> and if any return false,
/// the global grain deactivation will be stopped.
///
/// If they all return true, the grain eviction strategy will be run.
/// </summary>
public interface ICollectionGuard
{
    /// <summary>
    /// Decide whether the grain should be deactivated.
    /// </summary>
    /// <returns></returns>
    bool ShouldCollect();
}