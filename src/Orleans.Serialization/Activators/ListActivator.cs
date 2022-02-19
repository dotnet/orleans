using System.Collections.Generic;

namespace Orleans.Serialization.Activators
{
    /// <summary>
    /// Creates <see cref="List{T}"/> instances.
    /// </summary>
    /// <typeparam name="T">The list element type.</typeparam>
    public class ListActivator<T>
    {
        /// <summary>
        /// Creates a new list.
        /// </summary>
        /// <param name="initialCapacity">The initial capacity.</param>
        /// <returns>A new list.</returns>
        public List<T> Create(int initialCapacity) => new List<T>(initialCapacity);
    }
}