namespace Orleans.Serialization.Invocation
{
    /// <summary>
    /// Represents an object which holds an invocation target as well as target extensions.
    /// </summary>
    public interface ITargetHolder
    {
        /// <summary>
        /// Gets the target.
        /// </summary>
        /// <typeparam name="TTarget">The target type.</typeparam>
        /// <returns>The target.</returns>
        TTarget GetTarget<TTarget>() where TTarget : class;

        /// <summary>
        /// Gets the component with the specified type.
        /// </summary>
        /// <typeparam name="TComponent">The component type.</typeparam>
        /// <returns>The component with the specified type.</returns>
        TComponent GetComponent<TComponent>() where TComponent : class;
    }
}