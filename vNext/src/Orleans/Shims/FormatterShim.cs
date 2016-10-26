using System;

namespace Orleans
{
#if NETSTANDARD_TODO
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Enum)]
    public class SerializableAttribute : Attribute
    {
    }

    [AttributeUsage(AttributeTargets.Field)]
    public class NonSerializedAttribute : Attribute
    {
    }
#endif
}
