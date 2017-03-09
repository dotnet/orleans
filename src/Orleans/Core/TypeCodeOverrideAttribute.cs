using System;

namespace Orleans.CodeGeneration
{
    /// <summary>
    /// The TypeCodeOverrideAttribute attribute allows to specify the grain interface ID or the grain class type code
    /// to override the default ones to avoid hash collisions
    /// </summary>
    [AttributeUsage(AttributeTargets.Interface | AttributeTargets.Class)]
    public sealed class TypeCodeOverrideAttribute : Attribute
    {
        /// <summary>
        /// Use a specific grain interface ID or grain class type code (e.g. to avoid hash collisions)
        /// </summary>
        public int TypeCode { get; private set; }

        public TypeCodeOverrideAttribute(int typeCode)
        {
            TypeCode = typeCode;
        }
    }
}