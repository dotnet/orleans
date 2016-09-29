using System;

namespace Orleans.CodeGeneration
{
    using Orleans.Runtime;

    /// <summary>
    /// For internal (run-time) use only.
    /// Base class of all the activation attributes 
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1813:AvoidUnsealedAttributes"), AttributeUsage(System.AttributeTargets.All)]
    public abstract class GeneratedAttribute : Attribute
    {
        /// <summary>
        /// Gets or sets the type which this implementation applies to.
        /// </summary>
        public string ForGrainType { get; protected set; }

        /// <summary>
        /// Gets or sets the type which this implementation applies to.
        /// </summary>
        public Type GrainType { get; protected set; }

        /// <summary>
        /// </summary>
        /// <param name="forGrainType">type argument</param>
        protected GeneratedAttribute(string forGrainType)
        {
            ForGrainType = forGrainType;
        }

        /// <summary>
        /// </summary>
        protected GeneratedAttribute() { }
    }

    /// <summary>
    /// Identifies a class that knows how to map the messages targeting a specifies interface ID to a grain (CLR) interface.
    /// </summary>
    [AttributeUsage(System.AttributeTargets.Class)]
    public sealed class MethodInvokerAttribute : GeneratedAttribute
    {
        /// <summary>Initializes a new instance of <see cref="MethodInvokerAttribute"/>.</summary>
        /// <param name="forGrainType">The type which this implementation applies to.</param>
        /// <param name="interfaceId">The ID assigned to the interface by Orleans</param>
        /// <param name="grainType">The grain implementation type</param>
        public MethodInvokerAttribute(string forGrainType, int interfaceId, Type grainType = null)
        {
            ForGrainType = forGrainType;
            InterfaceId = interfaceId;
            GrainType = grainType;
        }

        /// <summary>The ID assigned to the interface by Orleans</summary>
        public int InterfaceId { get; private set; }
    }

    /// <summary>Identifies a concrete grain reference to an interface ID</summary>
    [AttributeUsage(System.AttributeTargets.Class)]
    public sealed class GrainReferenceAttribute : GeneratedAttribute
    {
        /// <summary>
        /// </summary>
        /// <param name="forGrainType">type argument</param>
        public GrainReferenceAttribute(string forGrainType)
        {
            ForGrainType = forGrainType;
        }

        /// <summary>
        /// </summary>
        /// <param name="forGrainType">type argument</param>
        public GrainReferenceAttribute(Type forGrainType)
        {
            GrainType = forGrainType;
            ForGrainType = forGrainType.GetParseableName();
        }
    }

    /// <summary>
    /// Identifies a class that contains all the serializer methods for a type.
    /// </summary>
    [AttributeUsage(System.AttributeTargets.Class)]
    public sealed class SerializerAttribute : GeneratedAttribute
    {
        /// <summary>
        /// </summary>
        /// <param name="serializableType">The target type that these serializer methods are for.</param>
        public SerializerAttribute(Type serializableType)
        {
            GrainType = serializableType;
            ForGrainType = serializableType.GetParseableName();
        }
    }
}
