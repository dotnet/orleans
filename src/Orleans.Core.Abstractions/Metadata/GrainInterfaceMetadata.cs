using System;
using System.Diagnostics;

namespace Orleans.Metadata
{
    /// <summary>
    /// Describes a grain interface.
    /// </summary>
    [DebuggerDisplay("{" + nameof(InterfaceType) + "}")]
    public class GrainInterfaceMetadata
    {
        /// <summary>
        /// Initializes an instance of the <see cref="GrainInterfaceMetadata"/> class.
        /// </summary>
        /// <param name="interfaceType">The grain interface type</param>
        /// <param name="referenceType">The grain reference type.</param>
        /// <param name="invokerType">The grain method invoker type.</param>
        /// <param name="interfaceId">The interface id.</param>
        public GrainInterfaceMetadata(Type interfaceType, Type referenceType, Type invokerType, int interfaceId)
        {
            this.InterfaceType = interfaceType;
            this.ReferenceType = referenceType;
            this.InvokerType = invokerType;
            this.InterfaceId = interfaceId;
        }

        /// <summary>
        /// Gets the interface type.
        /// </summary>
        public Type InterfaceType { get; }

        /// <summary>
        /// Gets the type of the grain reference for this interface.
        /// </summary>
        public Type ReferenceType { get; }

        /// <summary>
        /// Gets the type of the grain method invoker for this interface.
        /// </summary>
        public Type InvokerType { get; }

        /// <summary>
        /// Gets the interface id.
        /// </summary>
        public int InterfaceId { get; }
    }
}