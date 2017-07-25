
namespace Orleans.Runtime
{
    /// <summary>
    /// Stages of a grains lifecycle.
    /// TODO: Add more later, see ActivationInitializationStage
    /// Full grain lifecycle, including register, state setup, and 
    ///   stream cleanup should all eventually be triggered by the 
    ///   grain lifecycle.
    /// </summary>
    public enum GrainLifecyleStage
    {
        //None,
        //Register,
        SetupState,  // Setup grain state prior to activation
        //InvokeActivate,
        //Completed
    }

    /// <summary>
    /// Grain life cycle
    /// </summary>
    public interface IGrainLifeCycle : ILifecycleObservable<GrainLifecyleStage>
    {
    }

    /// <summary>
    /// Provides hook to take part in grain lifecycle.
    /// Also may act as a signal interface indicating that an object can take part in grain lifecycle.
    /// </summary>
    public interface IGrainLifecycleParticipant
    {
        void Participate(IGrainLifeCycle lifecycle);
    }

    public static class GrainLifecyleExtensions
    {
        public static void ParticipateInGrainLifecycle(this object obj, IGrainLifeCycle lifecycle)
        {
            (obj as IGrainLifecycleParticipant)?.Participate(lifecycle);
        }
    }
}
