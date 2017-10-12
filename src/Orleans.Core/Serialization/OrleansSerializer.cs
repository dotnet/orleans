using System;

namespace Orleans.Serialization
{
    public static class OrleansSerializer
    {
        /// <summary>        
        /// Returns <see langword="true"/> if instances of the provided type can be safely shallow-copied;
        /// otherwise <see langword="false"/>, indicating that instances must instead be deep-copied.
        /// </summary>
        /// <param name="type">The type to check.</param>
        /// <returns>
        /// <see langword="true"/> if instances of the provided type can be safely shallow-copied; otherwise
        /// <see langword="false"/>, indicating that instances must instead be deep-copied.
        /// </returns>
        public static bool IsTypeShallowCopyable(Type type)
        {
            return type.IsOrleansShallowCopyable();
        }
    }
}