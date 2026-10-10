namespace Orleans.Serialization.Invocation;

/// <summary>
/// Coordinates the lifetime of independently owned arguments of an invokable request.
/// </summary>
/// <remarks>
/// Requests with explicitly owned arguments implement this interface. A request starts with one
/// owner. Serialization and invocation retain temporary uses before accessing its arguments.
/// Completion ends the initial ownership and prevents new uses, but resources remain valid until
/// all previously retained uses have been released. Implementations must be thread-safe, completion
/// must be idempotent, and cleanup must not clear arguments while a retained use is active.
/// Cleanup must attempt to release every owned argument even when releasing one of them throws.
/// </remarks>
public interface IInvokableArgumentOwner
{
    /// <summary>
    /// Retains a temporary use of the arguments, unless ownership has already completed.
    /// </summary>
    /// <returns><see langword="true"/> if a use was retained; otherwise <see langword="false"/>.</returns>
    bool TryRetainArgumentResources();

    /// <summary>
    /// Releases one successfully retained temporary use, preserving the initial ownership until completion.
    /// </summary>
    void ReleaseArgumentResources();

    /// <summary>
    /// Completes the initial ownership once, releasing resources after the last temporary use exits.
    /// </summary>
    void CompleteArgumentResources();
}
