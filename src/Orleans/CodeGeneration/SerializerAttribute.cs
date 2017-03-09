using System;

namespace Orleans.CodeGeneration
{
    /// <summary>
    /// Identifies a class that contains all the serializer methods for a type.
    /// </summary>
    [AttributeUsage(System.AttributeTargets.Class, AllowMultiple = true)]
    public sealed class SerializerAttribute : GeneratedAttribute
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="SerializerAttribute"/> class.
        /// </summary>
        /// <param name="targetType">The type that this implementation can serialize.</param>
        public SerializerAttribute(Type targetType)
            : base(targetType)
        {
        }
    }
}