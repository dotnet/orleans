namespace Orleans.Serialization
{
    using System;

    /// <summary>
    /// Marker attribute for classes which should not be serialized.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public class NonSerializableAttribute : Attribute
    {
    }
}
