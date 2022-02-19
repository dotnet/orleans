using System;

namespace Orleans.Runtime
{
    /// <summary>
    /// Identifies a position on a hash ring.
    /// </summary>
    /// <typeparam name="T">The id type.</typeparam>
    internal interface IRingIdentifier<T> : IEquatable<T>
    {
        /// <summary>
        /// Gets the uniform hash code for this instance.
        /// </summary>
        /// <returns>The uniform hash code for this instance.</returns>
        uint GetUniformHashCode();
    }
}
