namespace Orleans.Runtime
{
    /// <summary>
    /// Provides methods to create a grain.
    /// </summary>
    public interface IGrainActivator
    {
        /// <summary>
        /// Creates a grain.
        /// </summary>
        /// <param name="context">The <see cref="IGrainActivationContext"/> for the executing action.</param>
        /// <returns>An instantiated grain.</returns>
        object Create(IGrainActivationContext context);

        /// <summary>
        /// Releases a controller.
        /// </summary>
        /// <param name="context">The <see cref="IGrainActivationContext"/> for the executing action.</param>
        /// <param name="grain">The grain to release.</param>
        void Release(IGrainActivationContext context, object grain);
    }
}