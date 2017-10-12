namespace Orleans.Runtime
{
    /// <summary>
    /// Provides methods to create a grain.
    /// Note:  Custom grain activator should only be used to create application grains.  All non-application
    /// grains should be passed through to the DefaultGrainActivator for creation.
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