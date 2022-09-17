namespace Orleans.Runtime;

/// <summary>
/// The guard(s) against deactivating grains.
///
/// These will be run by the <see cref="ActivationCollector"/> and if any return false,
/// the global grain deactivation will be stopped.
///
/// If they all return true, the grain collection will be run.
/// </summary>
public interface IGrainCollectionGuard
{
    /// <summary>
    /// Decide whether the grain collection should be run.
    /// </summary>
    /// <returns></returns>
    bool ShouldCollect();
}