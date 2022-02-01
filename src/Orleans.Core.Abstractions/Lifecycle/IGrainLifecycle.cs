
namespace Orleans.Runtime
{
    /// <summary>
    /// The observable grain lifecycle.
    /// </summary>
    /// <remarks>
    /// This type is usually used as the generic parameter in <see cref="ILifecycleParticipant{IGrainLifecycle}"/> as
    /// a means of participating in the lifecycle stages of a grain activation.
    /// </remarks>
    public interface IGrainLifecycle : ILifecycleObservable
    {
    }
}
