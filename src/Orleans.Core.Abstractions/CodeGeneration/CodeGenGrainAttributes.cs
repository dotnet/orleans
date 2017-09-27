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

    /// <summary>
    /// The VersionAttribute allows to specify the version number of the interface
    /// </summary>
    [AttributeUsage(AttributeTargets.Interface)]
    public sealed class VersionAttribute : Attribute
    {
        public ushort Version { get; private set; }

        public VersionAttribute(ushort version)
        {
            Version = version;
        }
    }

    /// <summary>
    /// Used to mark a method as providing a copier function for that type.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class CopierMethodAttribute : Attribute
    {
    }

    /// <summary>
    /// Used to mark a method as providing a serializer function for that type.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class SerializerMethodAttribute : Attribute
    {
    }

    /// <summary>
    /// Used to mark a method as providing a deserializer function for that type.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class DeserializerMethodAttribute : Attribute
    {
    }
}
