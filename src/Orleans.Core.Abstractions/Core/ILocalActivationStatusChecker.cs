using Orleans.Runtime;

namespace Orleans;

/// <summary>
/// Provides a way to check whether a grain is locally activated.
/// </summary>
public interface ILocalActivationStatusChecker
{
    /// <summary>
    /// Returns <see langword="true"/> if the provided grain is locally activated; otherwise, <see langword="false"/>.
    /// </summary>
    /// <param name="grainId">The identifier of the grain to check.</param>
    /// <returns><see langword="true"/> if the provided grain is locally activated; otherwise, <see langword="false"/>.</returns>
    bool IsLocallyActivated(GrainId grainId);
}
