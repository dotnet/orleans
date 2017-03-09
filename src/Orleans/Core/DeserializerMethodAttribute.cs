using System;

namespace Orleans.CodeGeneration
{
    /// <summary>
    /// Used to mark a method as providing a deserializer function for that type.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class DeserializerMethodAttribute : Attribute
    {
    }
}