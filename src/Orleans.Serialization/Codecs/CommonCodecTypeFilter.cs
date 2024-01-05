using System;
using System.CodeDom.Compiler;
using System.Linq;
using System.Reflection;

using Orleans.Metadata;

namespace Orleans.Serialization.Codecs;

/// <summary>
/// Defines common type filtering operations.
/// </summary>
public class CommonCodecTypeFilter
{
    /// <summary>
    /// Returns true if the provided type is a framework or abstract type.
    /// </summary>
    /// <param name="type">The type to check.</param>
    /// <returns><see langword="true"/> if the type is a framework or abstract type, otherwise <see langword="false"/>.</returns>
    public static bool IsAbstractOrFrameworkType(Type type)
    {
        if (type.IsAbstract
            || type.GetCustomAttributes<GeneratedCodeAttribute>().Any(a => a.Tool.Equals("OrleansCodeGen"))
            || type.Assembly.GetCustomAttribute<FrameworkPartAttribute>() is not null)
        {
            return true;
        }

        return false;
    }
}
