using System;

namespace Orleans.CodeGeneration
{
    /// <summary>
    /// For internal (run-time) use only.
    /// Base class of all the activation attributes 
    /// </summary>
    [AttributeUsage(System.AttributeTargets.All)]
    public abstract class GeneratedAttribute : Attribute
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="GeneratedAttribute"/> class.
        /// </summary>
        /// <param name="targetType">The type which this implementation applies to.</param>
        protected GeneratedAttribute(Type targetType)
        {
            this.TargetType = targetType;
        }

        /// <summary>
        /// Gets the type which this implementation applies to.
        /// </summary>
        public Type TargetType { get; }
    }

    /// <summary>
    /// Identifies a class that knows how to map the messages targeting a specifies interface ID to a grain (CLR) interface.
    /// </summary>
    [AttributeUsage(System.AttributeTargets.Class)]
    public sealed class MethodInvokerAttribute : GeneratedAttribute
    {
        /// <summary>Initializes a new instance of the <see cref="MethodInvokerAttribute"/> class.</summary>
        /// <param name="targetType">The grain implementation type</param>
        /// <param name="interfaceId">The ID assigned to the interface by Orleans</param>
        public MethodInvokerAttribute(Type targetType, int interfaceId)
            : base(targetType)
        {
            InterfaceId = interfaceId;
        }

        /// <summary>Gets the ID assigned to the interface by Orleans</summary>
        public int InterfaceId { get; }
    }

    /// <summary>Identifies a concrete grain reference to an interface ID</summary>
    [AttributeUsage(System.AttributeTargets.Class)]
    public sealed class GrainReferenceAttribute : GeneratedAttribute
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="GrainReferenceAttribute"/> class.
        /// </summary>
        /// <param name="targetType">The type which this implementation applies to.</param>
        public GrainReferenceAttribute(Type targetType)
            : base(targetType)
        {
        }
    }

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
