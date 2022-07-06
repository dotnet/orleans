using System;
using System.Runtime.CompilerServices;

namespace Orleans.Runtime
{
    /// <summary>
    /// Functionality for managing the current grain context.
    /// </summary>
    internal static class RuntimeContext
    {
        /// <summary>
        /// Gets the current grain context.
        /// </summary>
        public static IGrainContext Current => OrleansSynchronizationContext.Current?.GrainContext;
    }
}
