using System;

namespace Orleans.Serialization.TypeSystem
{
    /// <summary>
    /// Type which allows any exception type to be resolved.
    /// </summary>
    public sealed class DefaultTypeFilter : ITypeFilter
    {
        /// <inheritdoc/>
        public bool? IsTypeNameAllowed(string typeName, string assemblyName)
        {
#if !NETCOREAPP3_1_OR_GREATER
            if (assemblyName is { } && assemblyName.Contains("Orleans.Serialization"))
#else
            if (assemblyName is { } && assemblyName.Contains("Orleans.Serialization", StringComparison.Ordinal))
#endif
            {
                return true;
            }

            if (typeName.EndsWith(nameof(Exception), StringComparison.Ordinal))
            {
                return true;
            }

            if (typeName.StartsWith("System.", StringComparison.Ordinal))
            {
                if (typeName.EndsWith("Comparer", StringComparison.Ordinal))
                {
                    return true;
                }

                if (typeName.StartsWith("System.Collections.", StringComparison.Ordinal))
                {
                    return true;
                }

                if (typeName.StartsWith("System.Net.IP", StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return null;
        }
    }
}