
namespace Orleans
{
    /// <summary>
    /// Provides hook to take part in grain lifecycle.
    /// Also may act as a signal interface indicating that an object can take part in lifecycle.
    /// </summary>
    public interface ILifecycleParticipant<in TLifeCycle>
    {
        void Participate(TLifeCycle lifecycle);
    }

    public static class GrainLifecyleExtensions
    {
        public static void ParticipateInLifecycle<TLifeCycle>(this object obj, TLifeCycle lifecycle)
        {
            (obj as ILifecycleParticipant<TLifeCycle>)?.Participate(lifecycle);
        }
    }
}
