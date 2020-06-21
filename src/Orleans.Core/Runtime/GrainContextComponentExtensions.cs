using Orleans.Runtime;

namespace Orleans
{
    /// <summary>
    /// Extensions for <see cref="IGrainContext"/> related to <see cref="IGrainExtension"/>.
    /// </summary>
    public static class GrainContextComponentExtensions
    {
        /// <summary>
        /// Used by generated code for <see cref="IGrainExtension" /> interfaces.
        /// </summary>
        public static TComponent GetGrainExtension<TComponent>(this IGrainContext context)
            where TComponent : IGrainExtension
        {
            var binder = context.GetComponent<IGrainExtensionBinder>();
            return binder.GetExtension<TComponent>();
        }
    }
}