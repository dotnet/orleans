using System;

namespace Orleans.CodeGeneration
{
    /// <summary>
    /// Specifies the method id for the interface method which this attribute is declared on.
    /// </summary>
    /// <remarks>
    /// Method ids must be unique for all methods in a given interface.
    /// This attribute is only applicable for interface method declarations, not for method definitions on classes.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class MethodIdAttribute : Attribute
    {
        /// <summary>
        /// Gets the method id for the interface method this attribute is declared on.
        /// </summary>
        public int MethodId { get; }

        /// <summary>
        /// Specifies the method id for the interface method which this attribute is declared on.
        /// </summary>
        /// <remarks>
        /// Method ids must be unique for all methods in a given interface.
        /// This attribute is only valid only on interface method declarations, not on method definitions.
        /// </remarks>
        /// <param name="methodId">The method id.</param>
        public MethodIdAttribute(int methodId)
        {
            this.MethodId = methodId;
        }
    }
}