using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Runtime.Utilities
{
    /// <summary>
    /// Utility methods for aiding debugger sessions.
    /// </summary>
    public static class OrleansDebuggerHelper
    {
        /// <summary>
        /// Returns the grain instance corresponding to the provided <paramref name="grainReference"/> if it is activated on current silo.
        /// </summary>
        /// <param name="grainReference">The grain reference.</param>
        /// <returns>
        /// The grain instance corresponding to the provided <paramref name="grainReference"/> if it is activated on current silo, or <see langword="null"/> otherwise.
        /// </returns>
        public static object GetGrainInstance(object grainReference)
        {
            switch (grainReference)
            {
                case Grain:
                case IGrainBase:
                case ISystemTargetBase:
                    return grainReference;
                case GrainReference reference:
                    {
                        var runtime = (reference.Runtime as GrainReferenceRuntime)?.RuntimeClient;
                        var activations = runtime?.ServiceProvider.GetService<ActivationDirectory>();
                        var grains = activations?.FindTarget(reference.GrainId);
                        return grains?.GrainInstance;
                    }
                default:
                    return null;
            }
        }
    }
}
