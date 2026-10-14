namespace Orleans.Runtime;

/// <summary>
/// An activation which components can keep alive while they have pending work.
/// </summary>
internal interface IActivationKeepAlive
{
    /// <summary>
    /// Prevents idle collection of the activation until a matching call to <see cref="ReleaseKeepAlive"/>.
    /// </summary>
    /// <remarks>
    /// Holds are counted. Memory-pressure collection and explicit deactivation do not observe holds.
    /// </remarks>
    void AcquireKeepAlive();

    /// <summary>
    /// Releases a hold acquired by <see cref="AcquireKeepAlive"/>. Releasing the last hold restarts the activation's idle period.
    /// </summary>
    void ReleaseKeepAlive();
}
