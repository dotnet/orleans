using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

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
    
    [AttributeUsage(System.AttributeTargets.Class)]
    public sealed class GrainStateAttribute : GeneratedAttribute
    {
        /// <summary>
        /// </summary>
        /// <param name="forGrainType">type argument</param>
        public GrainStateAttribute(string forGrainType)
        {
            ForGrainType = forGrainType;
        }

        /// <summary>
        /// </summary>
        /// <param name="forGrainType">type argument</param>
        public GrainStateAttribute(Type forGrainType)
        {
            GrainType = forGrainType;
            ForGrainType = forGrainType.GetParseableName();
        }
    }

    [AttributeUsage(System.AttributeTargets.Class)]
    public sealed class MethodInvokerAttribute : GeneratedAttribute
    {
        /// <summary>
        /// </summary>
        /// <param name="forGrainType">type argument</param>
        public MethodInvokerAttribute(string forGrainType, int interfaceId, Type grainType = null)
        {
            ForGrainType = forGrainType;
            InterfaceId = interfaceId;
            GrainType = grainType;
        }

        public int InterfaceId { get; private set; }
    }

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

    [AttributeUsage(System.AttributeTargets.Class)]
    public sealed class SerializerAttribute : GeneratedAttribute
    {
        /// <summary>
        /// </summary>
        /// <param name="forGrainType">type argument</param>
        public SerializerAttribute(Type forGrainType)
        {
            GrainType = forGrainType;
            ForGrainType = forGrainType.GetParseableName();
        }
    }
}
