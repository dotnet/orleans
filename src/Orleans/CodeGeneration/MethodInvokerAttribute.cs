using System;

namespace Orleans.CodeGeneration
{
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
}