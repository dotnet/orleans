using System.Collections.Generic;
using System.Reflection;

namespace Orleans.ApplicationParts
{
    /// <summary>
    /// Represents an <see cref="IApplicationPartManager"/> scoped to a set of included assembly parts.
    /// </summary>
    public interface IApplicationPartManagerWithAssemblies : IApplicationPartManager
    {
        /// <summary>
        /// Gets the assemblies which in the scope of this instance.
        /// </summary>
        IEnumerable<Assembly> Assemblies { get; }
    }
}