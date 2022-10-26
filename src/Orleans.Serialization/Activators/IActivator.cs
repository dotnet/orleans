
namespace Orleans.Serialization.Activators
{
    /// <summary>
    /// Functionality for creating object instances.
    /// </summary>
    /// <typeparam name="T">The instance type which this implementation creates.</typeparam>
    public interface IActivator<T>
    {
        /// <summary>
        /// Creates an instance of type <typeparamref name="T"/>.
        /// </summary>
        /// <returns>An instance of type <typeparamref name="T"/>.</returns>
        T Create();
    }
}