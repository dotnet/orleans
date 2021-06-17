using System;

namespace Orleans.Serialization.TypeSystem
{
    /// <summary>
    /// Type which allows any exception type to be resolved.
    /// </summary>
    public sealed class DefaultTypeFilter : ITypeFilter
    {
        public bool? IsTypeNameAllowed(string typeName, string assemblyName)
        {
            if (assemblyName is { } && assemblyName.Contains("Orleans.Serialization"))
            {
                return true;
            }

            if (typeName.EndsWith(nameof(Exception)))
            {
                return true;
            }

            if (typeName.StartsWith("System."))
            {
                if (typeName.EndsWith("Comparer"))
                {
                    return true;
                }

                if (typeName.StartsWith("System.Collections."))
                {
                    return true;
                }

                if (typeName.StartsWith("System.Net.IP"))
                {
                    return true;
                }
            }

            return null;
        }
    }
}