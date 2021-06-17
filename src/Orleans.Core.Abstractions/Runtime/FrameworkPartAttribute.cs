using System;

namespace Orleans
{
    /// <summary>
    /// Indicates that an assembly is a framework component, rather than an application component.
    /// </summary>
    [AttributeUsage(AttributeTargets.Assembly)]
    public sealed class FrameworkPartAttribute : Attribute
    {
    }
}
