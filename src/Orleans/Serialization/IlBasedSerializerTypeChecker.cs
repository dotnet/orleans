namespace Orleans.Serialization
{
    using System;
    using System.Reflection;

    internal static class IlBasedSerializerTypeChecker
    {
        private static readonly RuntimeTypeHandle[] UnsupportedTypeHandles =
        {
            typeof(IntPtr).TypeHandle,
            typeof(UIntPtr).TypeHandle,
        };

        private static readonly TypeInfo[] UnsupportedBaseTypes = { typeof(Delegate).GetTypeInfo() };

        public static bool IsSupportedType(TypeInfo t)
        {
            return !t.IsAbstract && !t.IsInterface && !t.IsArray && IsSupportedFieldType(t);
        }

        public static bool IsSupportedFieldType(TypeInfo t)
        {
            if (t.IsPointer || t.IsByRef) return false;

            var handle = t.AsType().TypeHandle;
            for (var i = 0; i < UnsupportedTypeHandles.Length; i++)
            {
                if (handle.Equals(UnsupportedTypeHandles[i])) return false;
            }

            for (var i = 0; i < UnsupportedBaseTypes.Length; i++)
            {
                if (UnsupportedBaseTypes[i].IsAssignableFrom(t)) return false;
            }

            return true;
        }
    }
}