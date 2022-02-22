
namespace Orleans
{
    /// <summary>
    /// Provides hook to take part in lifecycle.
    /// Also may act as a signal interface indicating that an object can take part in lifecycle.
    /// </summary>
    /// <typeparam name="TLifecycleObservable">
    /// The type of lifecycle being observed.
    /// </typeparam>
    public interface ILifecycleParticipant<TLifecycleObservable>
        where TLifecycleObservable : ILifecycleObservable
    {
        /// <summary>
        /// Adds the provided observer as a participant in the lifecycle.
        /// </summary>
        /// <param name="observer">
        /// The observer.
        /// </param>
        void Participate(TLifecycleObservable observer);
    }
}
