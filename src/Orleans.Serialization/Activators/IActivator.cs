
namespace Orleans.Serialization.Activators
{
    /// <summary>
    /// Functionality for creating object instances.
    /// </summary>
    /// <typeparam name="T">The instance type which this implementation creates.</typeparam>
    /// <seealso cref="Orleans.Serialization.Activators.IActivator" />
    public interface IActivator<T> : IActivator
    {
        /// <summary>
        /// Creates an instance of type <typeparamref name="T"/>.
        /// </summary>
        /// <returns>An instance of type <typeparamref name="T"/>.</returns>
        T Create();
    }

    /// <summary>
    /// Marker type for activators.
    /// </summary>
    /// <seealso cref="Orleans.Serialization.Activators.IActivator" />
    public interface IActivator { }
}