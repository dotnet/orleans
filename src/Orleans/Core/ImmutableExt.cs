namespace Orleans.Concurrency
{
    /// <summary>
    /// Utility class to add the .AsImmutable method to all objects.
    /// </summary>
    public static class ImmutableExt
    {
        /// <summary>
        /// Extension method to return this value wrapped in <c>Immutable</c>.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="value">Value to be wrapped.</param>
        /// <returns>Immutable wrapper around the original object.</returns>
        /// <seealso cref="Immutable{T}"/>"/>
        public static Immutable<T> AsImmutable<T>(this T value)
        {
            return new Immutable<T>(value);
        }
    }
}