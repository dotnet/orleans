
namespace Orleans
{
    /// <summary>
    /// Provides hook to take part in lifecycle.
    /// Also may act as a signal interface indicating that an object can take part in lifecycle.
    /// </summary>
    public interface ILifecycleParticipant<TLifecycleObservable>
        where TLifecycleObservable : ILifecycleObservable
    {
        void Participate(TLifecycleObservable lifecycle);
    }
}
