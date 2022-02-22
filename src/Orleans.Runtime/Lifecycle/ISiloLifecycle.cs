
namespace Orleans.Runtime
{
    /// <summary>
    /// The observable silo lifecycle.
    /// </summary>
    /// <remarks>
    /// This type is usually used as the generic parameter in <see cref="ILifecycleParticipant{ISiloLifecycle}"/> as
    /// a means of participating in the lifecycle stages of a silo.
    /// </remarks>
    /// <seealso cref="Orleans.ILifecycleObservable" />
    public interface ISiloLifecycle : ILifecycleObservable
    {
        /// <summary>
        /// The highest lifecycle stage which has completed starting.
        /// </summary>
        int HighestCompletedStage { get; }

        /// <summary>
        /// The lowest lifecycle stage which has completed stopping.
        /// </summary>
        int LowestStoppedStage { get; }
    }
}
