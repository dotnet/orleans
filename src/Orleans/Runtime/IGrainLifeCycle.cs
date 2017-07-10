
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
}
