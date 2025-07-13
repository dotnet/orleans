using System;
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
        /// <typeparam name="TComponent">
        /// The type of the component to get.
        /// </typeparam>
        /// <param name="context">
        /// The grain context.
        /// </param>
        /// <returns>
        /// The grain extension.
        /// </returns>
        public static TComponent GetGrainExtension<TComponent>(this IGrainContext context)
            where TComponent : class, IGrainExtension
        {
            var binder = context.GetComponent<IGrainExtensionBinder>();
            if (binder is null)
            {
                throw new InvalidOperationException($"No {nameof(IGrainExtensionBinder)} is available on the current grain context.");
            }

            return binder.GetExtension<TComponent>();
        }
    }
}
